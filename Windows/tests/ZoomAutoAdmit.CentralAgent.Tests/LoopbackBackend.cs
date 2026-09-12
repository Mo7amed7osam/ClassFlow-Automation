using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.CentralAgent.Tests;

/// <summary>
/// A stand-in for the central backend: a real WebSocket server on 127.0.0.1 (http.sys, no admin
/// rights needed) that checks the device token and lets a test read and write agent messages.
/// </summary>
internal sealed class LoopbackBackend : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _expectedAuthorization;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    public LoopbackBackend(string deviceToken)
    {
        _expectedAuthorization = $"Bearer {deviceToken}";
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BackendUrl = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BackendUrl.ToString());
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri BackendUrl { get; }
    public Channel<AgentConnection> Connections { get; } = Channel.CreateUnbounded<AgentConnection>();
    public ConcurrentQueue<string?> AuthorizationHeaders { get; } = new();
    public ConcurrentQueue<string> RequestUrls { get; } = new();
    public bool RefuseEveryone { get; set; }
    public int Refused;

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            string? authorization = context.Request.Headers["Authorization"];
            AuthorizationHeaders.Enqueue(authorization);
            RequestUrls.Enqueue(context.Request.Url!.ToString());
            if (!context.Request.IsWebSocketRequest || context.Request.Url!.AbsolutePath != "/ws/agent" ||
                RefuseEveryone || authorization != _expectedAuthorization)
            {
                Interlocked.Increment(ref Refused);
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }
            var webSocket = (await context.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
            var connection = new AgentConnection(webSocket);
            await Connections.Writer.WriteAsync(connection);
        }
    }

    public async Task<AgentConnection> NextConnectionAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await Connections.Reader.ReadAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Close();
        try { await _acceptLoop; } catch { }
    }
}

/// <summary>One agent connection as the backend sees it.</summary>
internal sealed class AgentConnection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    public Channel<JsonObject> Received { get; } = Channel.CreateUnbounded<JsonObject>();
    public List<JsonObject> All { get; } = [];

    public AgentConnection(WebSocket socket)
    {
        _socket = socket;
        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) { Received.Writer.TryComplete(); return; }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                var json = JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!.AsObject();
                lock (All) All.Add(json);
                await Received.Writer.WriteAsync(json);
            }
        }
        catch (Exception) { }
        Received.Writer.TryComplete();
    }

    /// <summary>The next message of this type; heartbeats are skipped unless asked for.</summary>
    public async Task<JsonObject> NextAsync(string type, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await Received.Reader.ReadAsync(cts.Token);
            string? kind = message["type"]?.GetValue<string>();
            if (kind == type) return message;
            if (kind == "heartbeat") continue;
            throw new Xunit.Sdk.XunitException($"Expected {type} but got {message.ToJsonString()}");
        }
    }

    /// <summary>Messages other than heartbeats that arrive within the window (to prove nothing else came).</summary>
    public async Task<List<JsonObject>> DrainAsync(TimeSpan window)
    {
        var extra = new List<JsonObject>();
        using var cts = new CancellationTokenSource(window);
        try
        {
            while (true)
            {
                var message = await Received.Reader.ReadAsync(cts.Token);
                if (message["type"]?.GetValue<string>() != "heartbeat") extra.Add(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        return extra;
    }

    public async Task SendAsync(object message)
    {
        string text = message is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(message);
        await _sendGate.WaitAsync();
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendGate.Release(); }
    }

    public Task AssignAsync(string jobId, JsonObject payload, string type = "job.assign") =>
        SendAsync(new JsonObject { ["type"] = type, ["jobId"] = jobId, ["jobType"] = "recording.process", ["payload"] = payload.DeepClone(), ["attempt"] = 1 });

    public Task AckAsync(string jobId, string @event) =>
        SendAsync(new JsonObject { ["type"] = "ack", ["jobId"] = jobId, ["event"] = @event });

    /// <summary>
    /// Sends the close frame only: the read loop, which owns receiving, sees the agent's reply.
    /// (CloseAsync would receive too, and two receives at once on one socket are not allowed.)
    /// </summary>
    public async Task CloseAsync()
    {
        await _sendGate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
        }
        catch { }
        finally { _sendGate.Release(); }
    }

    public void Abort() => _socket.Abort();
}

/// <summary>The recording workflow's stand-in: counts calls and answers what the test says.</summary>
internal sealed class FakeRecordingProcessor : IRecordingLinkProcessor
{
    public ConcurrentQueue<ProvidedRecordLinkRequest> Calls { get; } = new();
    public Func<ProvidedRecordLinkRequest, Task<RecordingLinkOutcome>>? Answer { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    public Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The agent must never search Zoom.");

    public async Task<RecordingLinkOutcome> AttachProvidedLinkAsync(ProvidedRecordLinkRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue(request);
        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (Answer is { } answer) return await answer(request);
        return Outcome(request, RecordingLinkStatus.Attached, "saved");
    }

    public static RecordingLinkOutcome Outcome(ProvidedRecordLinkRequest request, RecordingLinkStatus status, string message, string? reason = null) =>
        new(status, message)
        {
            Group = request.Group,
            Date = request.Date ?? new DateOnly(2026, 9, 3),
            StartTime = request.StartTime,
            Profile = RecordingLinkProcessor.DashboardProfile,
            Reason = reason,
        };
}

internal sealed class MemoryTokenStore(string? token = null) : IDeviceTokenStore
{
    public string? Token { get; private set; } = token;
    public string? Read() => Token;
    public void Save(string token) => Token = token;
    public void Delete() => Token = null;
}

/// <summary>A running agent against a <see cref="LoopbackBackend"/>, with its log collected.</summary>
internal sealed class AgentUnderTest : IAsyncDisposable
{
    public const string DeviceToken = "zaad_7d1f3c1e-7a51-4a36-9d5a-0c7e4a3f2b10.TEST-device-secret-abcdefghijklmnopqrstuvwxyz0123";
    public static readonly Guid DeviceId = Guid.Parse("7d1f3c1e-7a51-4a36-9d5a-0c7e4a3f2b10");
    public const string DriveId = "1AbCdEfGhIjKlMnOpQrStUvWxYz012345";   // made up

    private readonly CancellationTokenSource _stop = new();
    public ConcurrentQueue<string> Log { get; } = new();
    public FakeRecordingProcessor Processor { get; } = new();
    public JobJournal Journal { get; }
    public CentralAgentService Service { get; }
    public Task<AgentStopReason> Running { get; private set; } = Task.FromResult(AgentStopReason.Stopped);

    public AgentUnderTest(LoopbackBackend backend, JobJournal? journal = null, IDeviceTokenStore? tokens = null,
        TimeSpan? heartbeat = null, TimeSpan? silence = null)
    {
        Journal = journal ?? new JobJournal(path: null);
        var settings = new CentralAgentSettings
        {
            BackendUrl = backend.BackendUrl,
            Version = "1.2.3",
            HeartbeatInterval = heartbeat ?? TimeSpan.FromSeconds(5),
            ServerSilenceTimeout = silence ?? TimeSpan.FromSeconds(20),
            InitialBackoff = TimeSpan.FromMilliseconds(50),
            MaximumBackoff = TimeSpan.FromMilliseconds(200),
        };
        Service = new CentralAgentService(
            settings,
            new DeviceIdentity { InstallationId = Guid.NewGuid(), DeviceId = DeviceId, BackendUrl = backend.BackendUrl.ToString() },
            tokens ?? new MemoryTokenStore(DeviceToken),
            new ClientWebSocketFactory(),
            Journal,
            [new RecordingProcessJobHandler(Processor)],
            Log.Enqueue);
    }

    public AgentUnderTest Start()
    {
        Running = Task.Run(() => Service.RunAsync(_stop.Token));
        return this;
    }

    public string AllLog => string.Join("\n", Log);

    public static JsonObject Payload(bool replaceExisting = false) => new()
    {
        ["group"] = "AST5_DAT1_S1",
        ["recordLink"] = $"https://drive.google.com/file/d/{DriveId}/view?usp=sharing",
        ["date"] = "2026-09-03",
        ["replaceExisting"] = replaceExisting,
        ["dryRun"] = false,
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await Running.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
    }
}
