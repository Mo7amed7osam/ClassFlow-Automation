using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomAutoAdmit.WebAutomation.Recordings;

namespace ZoomAutoAdmit.WebAutomation.Api;

/// <summary>
/// A small HTTP endpoint on this computer that puts a recording link on a DEPI dashboard session.
///
///   GET  /health                   {"status":"ok"} - no key, so it can be checked before anything else
///   POST /api/recordings/process   X-API-Key required; { group, recordLink, date?, replaceExisting? }
///
/// The link is the Google Drive link n8n read from the recordings sheet. It is written to the session
/// exactly as sent; Zoom is never opened for these requests.
///
/// It uses the HTTP server built into Windows (http.sys, through HttpListener), which needs no extra
/// runtime and no administrator rights for 127.0.0.1. It listens on loopback only, and every request
/// whose connection does not come from this computer is refused before it is read - http.sys matches
/// requests by their Host header, so the bound address alone is not relied on.
///
/// A request keeps running if the caller hangs up: stopping halfway through a dashboard save would be
/// worse than finishing it, and a repeat is safe because an existing link is never overwritten unless
/// replaceExisting says so.
/// </summary>
public sealed class RecordingApiServer : IAsyncDisposable
{
    public const string ProcessPath = "/api/recordings/process";
    public const string HealthPath = "/health";
    public const string KeyHeader = "X-API-Key";
    private const int MaximumBodyBytes = 16 * 1024;
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Readable apostrophes and quotes in messages. The answer is JSON for n8n, never put into HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly RecordingApiOptions _options;
    private readonly IRecordingLinkProcessor _processor;
    private readonly Action<string> _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private Task? _acceptLoop;

    public RecordingApiServer(RecordingApiOptions options, IRecordingLinkProcessor processor, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _log = log ?? (_ => { });
        foreach (string prefix in options.Prefixes) _listener.Prefixes.Add(prefix);
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{_options.Port}/");

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> when the port is taken.</summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log($"[API] Recording API started on {BaseAddress.ToString().TrimEnd('/')} (loopback only).");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }

            var handling = Task.Run(() => HandleAsync(context));
            _inFlight.TryAdd(handling, 0);
            _ = handling.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var watch = Stopwatch.StartNew();
        string path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";
        int status = 500;
        try
        {
            if (request.RemoteEndPoint is not { } remote || !IPAddress.IsLoopback(remote.Address))
            {
                status = await WriteAsync(context, 403, new() { ["success"] = false, ["error"] = "Forbidden" });
                return;
            }

            if (path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                status = request.HttpMethod == "GET" || request.HttpMethod == "HEAD"
                    ? await WriteAsync(context, 200, new() { ["status"] = "ok" })
                    : await MethodNotAllowedAsync(context, "GET");
                return;
            }

            if (!path.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                status = await WriteAsync(context, 404, new() { ["success"] = false, ["error"] = "Not found" });
                return;
            }
            if (request.HttpMethod != "POST")
            {
                status = await MethodNotAllowedAsync(context, "POST");
                return;
            }

            // The key before the body: an unauthenticated caller learns nothing about the request format.
            if (!_options.KeyMatches(request.Headers[KeyHeader]))
            {
                status = await WriteAsync(context, 401, new() { ["success"] = false, ["error"] = "Unauthorized" });
                return;
            }

            string? body = await ReadBodyAsync(request);
            if (body == null)
            {
                status = await InvalidAsync(context, $"The body must be JSON of at most {MaximumBodyBytes / 1024} KB.");
                return;
            }
            if (!RecordingApiRequestParser.TryParse(body, out var parsed, out string error))
            {
                status = await InvalidAsync(context, error);
                return;
            }

            // The link only as a preview: a Drive share link opens the recording to anyone who has it.
            _log($"[API] Request: group={parsed!.Group} date={parsed.Date?.ToString("yyyy-MM-dd") ?? "(today)"} " +
                 $"recordLink={RecordingLinks.Preview(parsed.RecordLink)}" +
                 $"{(parsed.StartTime is { } time ? $" startTime={time:HH':'mm}" : string.Empty)} " +
                 $"replaceExisting={parsed.ReplaceExisting} dryRun={parsed.DryRun} headed={parsed.Headed}");

            var outcome = await _processor.AttachProvidedLinkAsync(parsed, _stopping.Token);
            var (code, response) = Describe(outcome);
            status = await WriteAsync(context, code, response);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            status = await TryWriteAsync(context, 503, new() { ["success"] = false, ["error"] = "Shutting down" });
        }
        catch (Exception ex)
        {
            // The type only: an exception's text can quote a path or a value from a page.
            _log($"[API] Unexpected {ex.GetType().Name} while handling {request.HttpMethod} {path}.");
            status = await TryWriteAsync(context, 500, new() { ["success"] = false, ["error"] = "Internal error" });
        }
        finally
        {
            _log($"[API] {request.HttpMethod} {path} -> {status} in {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>
    /// The HTTP answer for an outcome. Kept apart from the listener so the contract can be read - and
    /// tested - in one place.
    /// </summary>
    public static (int Status, Dictionary<string, object?> Body) Describe(RecordingLinkOutcome outcome)
    {
        var body = new Dictionary<string, object?>
        {
            ["success"] = outcome.IsSuccess,
            ["group"] = outcome.Group,
            ["date"] = outcome.Date.ToString("yyyy-MM-dd"),
        };
        // Only when one was sent: a dictionary writes its nulls, and "startTime": null reads like a lookup.
        if (outcome.StartTime is { } time) body["startTime"] = time.ToString("HH':'mm");
        if (outcome.RecordingStartedAtUtc is { } started) body["recordingStartedAtUtc"] = started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        if (outcome.RecordingDuration is { } length) body["recordingDuration"] = length.ToString(@"hh\:mm\:ss");

        switch (outcome.Status)
        {
            case RecordingLinkStatus.Attached:
                body["message"] = "Recording link attached successfully.";
                body["alreadyExists"] = false;
                return (200, body);
            case RecordingLinkStatus.AlreadyExists:
                body["message"] = outcome.Message;
                body["alreadyExists"] = true;
                return (200, body);
            case RecordingLinkStatus.DryRun:
                body["message"] = outcome.Message;
                body["alreadyExists"] = false;
                body["dryRun"] = true;
                return (200, body);
            case RecordingLinkStatus.RecordingNotFound:
                body["error"] = "Recording not found";
                body["reason"] = outcome.Reason;
                body["message"] = outcome.Message;
                return (404, body);
            case RecordingLinkStatus.Busy:
                body["error"] = "Busy";
                body["message"] = outcome.Message;
                return (409, body);
            case RecordingLinkStatus.ZoomFailed:
                body["error"] = "Zoom operation failed";
                body["reason"] = outcome.Reason;
                body["message"] = outcome.Message;
                return (500, body);
            default:
                body["error"] = "LMS operation failed";
                body["reason"] = outcome.Reason;
                body["message"] = outcome.Message;
                return (500, body);
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaximumBodyBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static Task<int> InvalidAsync(HttpListenerContext context, string details) =>
        WriteAsync(context, 400, new() { ["success"] = false, ["error"] = "Invalid request", ["details"] = details });

    private static Task<int> MethodNotAllowedAsync(HttpListenerContext context, string allowed)
    {
        context.Response.AddHeader("Allow", allowed);
        return WriteAsync(context, 405, new() { ["success"] = false, ["error"] = "Method not allowed" });
    }

    private static async Task<int> WriteAsync(HttpListenerContext context, int status, Dictionary<string, object?> body)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = payload.Length;
        response.Headers["Cache-Control"] = "no-store";
        await response.OutputStream.WriteAsync(payload);
        response.Close();
        return status;
    }

    /// <summary>For an error path: the caller may already have gone, which is not worth a second error.</summary>
    private static async Task<int> TryWriteAsync(HttpListenerContext context, int status, Dictionary<string, object?> body)
    {
        try { return await WriteAsync(context, status, body); }
        catch { return status; }
    }

    /// <summary>
    /// Stops taking requests, lets the ones already running finish (a dashboard save is not cut
    /// short), and only then cancels whatever is still going.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_listener.IsListening)
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
        }
        var running = _inFlight.Keys.ToArray();
        if (running.Length > 0)
        {
            _log($"[API] Waiting for {running.Length} request(s) to finish before stopping.");
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(ShutdownGrace));
        }
        _stopping.Cancel();
        if (_acceptLoop != null) await Task.WhenAny(_acceptLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        _listener.Close();
        _stopping.Dispose();
        _log("[API] Recording API stopped.");
    }
}
