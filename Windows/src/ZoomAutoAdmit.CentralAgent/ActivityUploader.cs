using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZoomAutoAdmit.Core.Central;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// Sends what this PC did to the central server (POST /api/v1/devices/activity, with the device
/// token): the classes it opened, the LMS steps it finished, the classes it ended.
///
/// It only reads notes the app has already written down (<see cref="ActivityLog"/>), so nothing a
/// class does ever waits for the network. A note is removed once the server has it; a server that
/// was away simply gets everything on the next pass, in order, and re-sending the same event
/// changes nothing there.
/// </summary>
public sealed class ActivityUploader
{
    public enum Outcome { Sent, Refused, Retry }

    /// <summary>One look at the notes: how many went, how many the server refused, whether it stopped to retry.</summary>
    public sealed record Pass(int Sent, int Refused, bool Waiting);

    private const int BatchSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _endpoint;
    private readonly IDeviceTokenStore _tokens;
    private readonly HttpClient _http;
    private readonly ActivityLog _log;
    private readonly Action<string> _write;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string? _waitingFor;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);

    public ActivityUploader(Uri backendUrl, IDeviceTokenStore tokens, HttpClient http, ActivityLog? log = null,
        Action<string>? write = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _endpoint = new Uri(CentralAgentSettings.ParseBackendUrl(backendUrl.ToString()), "api/v1/devices/activity");
        _tokens = tokens;
        _http = http;
        _log = log ?? new ActivityLog();
        _write = write ?? (_ => { });
        _delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await SendPendingAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Waiting(ex.GetType().Name); }
            try { await _delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<Pass> SendPendingAsync(CancellationToken cancellationToken)
    {
        int sent = 0, refused = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = _log.Pending(BatchSize);
            if (batch.Count == 0) break;

            var (outcome, reason) = await SendAsync([.. batch.Select(item => item.Event)], cancellationToken);
            if (outcome == Outcome.Retry)
            {
                // Left where they are, in order, for the next pass.
                Waiting(reason);
                if (sent > 0) _write(AgentLog.Line("activity_sent", ("events", sent)));
                return new Pass(sent, refused, Waiting: true);
            }
            // Refused for good means the server will never take these; keeping them would block the rest.
            foreach (var (path, _) in batch) _log.Forget(path);
            if (outcome == Outcome.Sent) sent += batch.Count;
            else
            {
                refused += batch.Count;
                _write(AgentLog.Line("activity_refused", ("events", batch.Count), ("reason", reason)));
            }
            if (batch.Count < BatchSize) break;
        }
        if (_waitingFor is not null)
        {
            _write(AgentLog.Line("activity_send_resumed"));
            _waitingFor = null;
        }
        if (sent > 0) _write(AgentLog.Line("activity_sent", ("events", sent)));
        return new Pass(sent, refused, Waiting: false);
    }

    private void Waiting(string reason)
    {
        if (_waitingFor == reason) return;
        _waitingFor = reason;
        _write(AgentLog.Line("activity_send_waiting", ("reason", reason)));
    }

    private async Task<(Outcome, string)> SendAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
    {
        string? token = _tokens.Read();
        if (string.IsNullOrWhiteSpace(token)) return (Outcome.Retry, "noDeviceToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { events }, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException ex) { return (Outcome.Retry, $"unreachable_{ex.HttpRequestError}"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return (Outcome.Retry, "timeout"); }
        using (response)
        {
            int status = (int)response.StatusCode;
            return status switch
            {
                200 or 201 => (Outcome.Sent, ""),
                400 or 403 or 413 or 422 => (Outcome.Refused, $"http{status}"),
                404 => (Outcome.Retry, "noActivityOnBackend"),          // a server from before this existed
                _ => (Outcome.Retry, $"http{status}"),
            };
        }
    }
}
