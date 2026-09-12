using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZoomAutoAdmit.CentralAgent;

public enum AgentStopReason
{
    /// <summary>Asked to stop (Ctrl+C, sign-out).</summary>
    Stopped,
    /// <summary>No device identity or token: run agent-register first.</summary>
    NotRegistered,
    /// <summary>The backend refused the device token. Reconnecting would not help, so it stops.</summary>
    Unauthorized,
}

/// <summary>
/// Keeps this PC connected to the central backend - an outbound WebSocket, reconnected with
/// backoff - and runs the jobs it is given, one at a time.
///
///   connect -> hello -> resend unacknowledged job messages -> heartbeat every 30 s
///   job.assign -> job.accepted          (nothing runs yet)
///   job.start  -> job.started -> run -> job.succeeded | job.failed   (kept until acknowledged)
///
/// A job id it has already accepted, run or finished is never run again: a repeat is answered from
/// the <see cref="JobJournal"/>. The work itself is done by an <see cref="IJobHandler"/> - for
/// recordings, the existing RecordingLinkProcessor - and keeps going if the connection drops.
/// </summary>
public sealed class CentralAgentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CentralAgentSettings _settings;
    private readonly Guid _deviceId;
    private readonly IDeviceTokenStore _tokens;
    private readonly IAgentSocketFactory _sockets;
    private readonly JobJournal _journal;
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _jobGate = new();

    private IAgentSocket? _socket;
    private string? _runningJobId;
    private Task _runningJob = Task.CompletedTask;

    public CentralAgentService(
        CentralAgentSettings settings,
        DeviceIdentity identity,
        IDeviceTokenStore tokens,
        IAgentSocketFactory sockets,
        JobJournal journal,
        IEnumerable<IJobHandler> handlers,
        Action<string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _settings = settings;
        _deviceId = identity.DeviceId ?? Guid.Empty;
        _tokens = tokens;
        _sockets = sockets;
        _journal = journal;
        _handlers = handlers.ToDictionary(h => h.JobType, StringComparer.Ordinal);
        _log = log ?? (_ => { });
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The job running now, if any.</summary>
    public string? RunningJobId { get { lock (_jobGate) return _runningJobId; } }

    /// <summary>Completes when the job running now (if any) has finished.</summary>
    public Task RunningJob { get { lock (_jobGate) return _runningJob; } }

    public async Task<AgentStopReason> RunAsync(CancellationToken cancellationToken)
    {
        string? token = _tokens.Read();
        if (_deviceId == Guid.Empty || string.IsNullOrWhiteSpace(token))
        {
            _log(AgentLog.Line("not_registered"));
            return AgentStopReason.NotRegistered;
        }

        foreach (string jobId in _journal.RecoverAfterRestart())
        {
            // It was running when the agent last stopped: whether the LMS was written is unknown.
            _journal.Finish(jobId, succeeded: false, Message("job.failed", jobId, error: new AgentJobError(
                "agentRestarted", "The agent stopped while this job was running. Check the LMS before retrying.", Retryable: false)));
            _log(AgentLog.Line("job_interrupted", ("jobId", jobId)));
        }

        var backoff = new ReconnectBackoff(_settings.InitialBackoff, _settings.MaximumBackoff, _settings.BackoffJitter);
        while (!cancellationToken.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                _log(AgentLog.Line("connecting", ("attempt", backoff.Attempt + 1), ("backend", _settings.BackendUrl.Host)));
                await using var socket = await _sockets.ConnectAsync(_settings.WebSocketUri, token, cancellationToken);
                connectedAt = DateTimeOffset.UtcNow;
                _log(AgentLog.Line("connected", ("deviceId", _deviceId)));
                string reason = await RunSessionAsync(socket, cancellationToken);
                _log(AgentLog.Line("disconnected", ("reason", reason)));
            }
            catch (AgentUnauthorizedException)
            {
                _log(AgentLog.Line("unauthorized", ("deviceId", _deviceId), ("action", "stopping; register this PC again")));
                return AgentStopReason.Unauthorized;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log(AgentLog.Line("disconnected", ("reason", ex.GetType().Name)));
            }
            finally
            {
                _socket = null;
            }

            // A connection that lasted is a fresh start; one that dropped at once keeps backing off.
            if (DateTimeOffset.UtcNow - connectedAt >= _settings.HeartbeatInterval) backoff.Reset();
            var wait = backoff.Next();
            _log(AgentLog.Line("reconnect_scheduled", ("attempt", backoff.Attempt + 1), ("delayMs", (int)wait.TotalMilliseconds)));
            try { await _delay(wait, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
        _log(AgentLog.Line("stopped"));
        return AgentStopReason.Stopped;
    }

    private async Task<string> RunSessionAsync(IAgentSocket socket, CancellationToken stopping)
    {
        _socket = socket;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        await SendAsync(new JsonObject
        {
            ["type"] = "hello",
            ["version"] = _settings.Version,
            ["capabilities"] = Capabilities(),
            ["agentState"] = RunningJobId is null ? "idle" : "busy",
            ["activeJobId"] = RunningJobId,
        }, session.Token);
        foreach (var pending in _journal.PendingMessages())
            await SendRawAsync(pending.Message, session.Token);

        var heartbeats = HeartbeatLoopAsync(session.Token);
        try
        {
            while (true)
            {
                using var silence = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                silence.CancelAfter(_settings.ServerSilenceTimeout);
                string? text;
                try { text = await socket.ReceiveAsync(silence.Token); }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    return "server silent";
                }
                if (text is null) return $"closed by backend ({socket.CloseStatus?.ToString() ?? "no code"})";
                await HandleAsync(text, stopping);
            }
        }
        finally
        {
            session.Cancel();
            _socket = null;
            try { await heartbeats; } catch (OperationCanceledException) { }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _delay(_settings.HeartbeatInterval, cancellationToken);
            await SendHeartbeatAsync(cancellationToken);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        string state = RunningJobId is null ? "idle" : "busy";
        await TrySendAsync(new JsonObject
        {
            ["type"] = "heartbeat",
            ["deviceId"] = _deviceId.ToString(),
            ["version"] = _settings.Version,
            ["status"] = state,
            ["capabilities"] = Capabilities(),
        }, cancellationToken);
        _log(AgentLog.Line("heartbeat", ("status", state)));
    }

    private async Task HandleAsync(string text, CancellationToken stopping)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            _log(AgentLog.Line("bad_message"));
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? jobId = root.TryGetProperty("jobId", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString() : null;
            if (jobId is not null && !Guid.TryParse(jobId, out _)) jobId = null;

            switch (type)
            {
                case "welcome":
                    _log(AgentLog.Line("welcome", ("deviceId", _deviceId)));
                    break;
                case "heartbeat.ack":
                    break;
                case "ack" when jobId is not null:
                    string? acked = root.TryGetProperty("event", out var e) ? e.GetString() : null;
                    if (acked is not null) _journal.Acknowledge(jobId, acked);
                    break;
                case "job.assign" when jobId is not null:
                    await OnAssignAsync(jobId, JobTypeOf(root), stopping);
                    break;
                case "job.start" when jobId is not null:
                    await OnStartAsync(jobId, JobTypeOf(root), root.TryGetProperty("payload", out var p) ? p.Clone() : default, stopping);
                    break;
                case "job.revoke" when jobId is not null:
                    _journal.Release(jobId);
                    _log(AgentLog.Line("job_revoked", ("jobId", jobId)));
                    break;
                case "error":
                    _log(AgentLog.Line("backend_error", ("code", root.TryGetProperty("code", out var c) ? c.GetString() : null)));
                    break;
                default:
                    _log(AgentLog.Line("unknown_message", ("type", type)));
                    break;
            }
        }
    }

    private static string? JobTypeOf(JsonElement root) =>
        root.TryGetProperty("jobType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private async Task OnAssignAsync(string jobId, string? jobType, CancellationToken stopping)
    {
        _log(AgentLog.Line("job_received", ("jobId", jobId), ("jobType", jobType)));
        switch (_journal.StateOf(jobId))
        {
            case JobState.Succeeded or JobState.Failed:
                await ResendFinalAsync(jobId, stopping);
                return;
            case JobState.Accepted or JobState.Running:
                _log(AgentLog.Line("duplicate_job", ("jobId", jobId)));
                await TrySendAsync(JobMessage("job.accepted", jobId), stopping);
                return;
        }
        string? busyWith = RunningJobId;
        if (busyWith is not null)
        {
            await TrySendAsync(new JsonObject { ["type"] = "job.rejected", ["jobId"] = jobId, ["reason"] = "busy" }, stopping);
            return;
        }
        if (jobType is null || !_handlers.ContainsKey(jobType))
        {
            await TrySendAsync(new JsonObject { ["type"] = "job.rejected", ["jobId"] = jobId, ["reason"] = "unsupportedType" }, stopping);
            return;
        }
        _journal.TryAccept(jobId);
        await TrySendAsync(JobMessage("job.accepted", jobId), stopping);
    }

    private async Task OnStartAsync(string jobId, string? jobType, JsonElement payload, CancellationToken stopping)
    {
        switch (_journal.StateOf(jobId))
        {
            case JobState.Succeeded or JobState.Failed:
                await ResendFinalAsync(jobId, stopping);
                return;
            case JobState.Running:
                _log(AgentLog.Line("duplicate_start", ("jobId", jobId)));
                return;
        }
        if (jobType is null || !_handlers.TryGetValue(jobType, out var handler))
        {
            await TrySendAsync(new JsonObject { ["type"] = "job.rejected", ["jobId"] = jobId, ["reason"] = "unsupportedType" }, stopping);
            return;
        }

        bool busy;
        lock (_jobGate)
        {
            busy = _runningJobId is not null;
            if (!busy)
            {
                // Recorded as running before anything happens, so a restart mid-job is known.
                if (!_journal.TryStart(jobId)) return;
                _runningJobId = jobId;
            }
        }
        if (busy)
        {
            await TrySendAsync(new JsonObject { ["type"] = "job.rejected", ["jobId"] = jobId, ["reason"] = "busy" }, stopping);
            return;
        }

        string started = Message("job.started", jobId);
        _journal.Enqueue(jobId, "job.started", started);
        await TrySendRawAsync(started, stopping);
        _log(AgentLog.Line("job_started", ("jobId", jobId), ("jobType", jobType)));
        // Not awaited: the connection keeps being served (heartbeats, acks) while the job runs, and
        // the job keeps running if the connection drops.
        var run = Task.Run(() => ExecuteAsync(handler, jobId, payload, stopping), CancellationToken.None);
        lock (_jobGate) _runningJob = run;
    }

    private async Task ExecuteAsync(IJobHandler handler, string jobId, JsonElement payload, CancellationToken stopping)
    {
        JobOutcome outcome;
        try
        {
            outcome = await handler.ExecuteAsync(payload, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopping mid-job: left as running in the journal, reported as interrupted at the next start.
            lock (_jobGate) _runningJobId = null;
            return;
        }
        catch (Exception ex)
        {
            // The type only: an exception's text can quote a page, a path or a value.
            outcome = JobOutcome.Failure("agentError", $"The job failed on the agent ({ex.GetType().Name}).");
        }

        string message = outcome.Succeeded
            ? Message("job.succeeded", jobId, outcome.Result)
            : Message("job.failed", jobId, error: outcome.Error);
        _journal.Finish(jobId, outcome.Succeeded, message);
        lock (_jobGate) _runningJobId = null;
        _log(outcome.Succeeded
            ? AgentLog.Line("job_succeeded", ("jobId", jobId), ("alreadyExists", outcome.Result?["alreadyExists"]?.ToString()))
            : AgentLog.Line("job_failed", ("jobId", jobId), ("code", outcome.Error!.Code), ("retryable", outcome.Error.Retryable)));

        try
        {
            // Kept in the journal's outbox until acknowledged; sent now if connected, else on reconnect.
            if (await TrySendRawAsync(message, stopping))
                await SendHeartbeatAsync(stopping);    // idle again: lets the backend hand out the next job now
        }
        catch (OperationCanceledException) { }
    }

    private async Task ResendFinalAsync(string jobId, CancellationToken cancellationToken)
    {
        _log(AgentLog.Line("duplicate_job", ("jobId", jobId), ("action", "resending result")));
        if (_journal.FinalMessageOf(jobId) is { } final) await TrySendRawAsync(final, cancellationToken);
    }

    private JsonArray Capabilities() => new([.. _settings.Capabilities.Select(c => (JsonNode?)JsonValue.Create(c))]);

    private static JsonObject JobMessage(string type, string jobId) => new() { ["type"] = type, ["jobId"] = jobId };

    private static string Message(string type, string jobId, JsonObject? result = null, AgentJobError? error = null)
    {
        var message = JobMessage(type, jobId);
        if (result is not null) message["result"] = result.DeepClone();
        if (error is not null)
        {
            var body = new JsonObject { ["code"] = error.Code, ["message"] = error.Message, ["retryable"] = error.Retryable };
            if (error.RetryAfterSeconds is { } after) body["retryAfterSeconds"] = after;
            message["error"] = body;
        }
        return message.ToJsonString(Json);
    }

    private Task SendAsync(JsonObject message, CancellationToken cancellationToken) =>
        SendRawAsync(message.ToJsonString(Json), cancellationToken);

    private async Task SendRawAsync(string text, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected.");
        await _sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(text, cancellationToken); }
        finally { _sendGate.Release(); }
    }

    private Task<bool> TrySendAsync(JsonObject message, CancellationToken cancellationToken) =>
        TrySendRawAsync(message.ToJsonString(Json), cancellationToken);

    /// <summary>Sends if connected. A failure is left to the receive loop, which notices and reconnects.</summary>
    private async Task<bool> TrySendRawAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await SendRawAsync(text, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
