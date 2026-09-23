using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;
using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.SessionRoles;

namespace ZoomAutoAdmit.CloudWorker.Stages;

/// <summary>
/// The meeting's Joined list on the web client, found again on the page at every read because Zoom
/// replaces it while a class runs. The same reader the Windows app uses for a web meeting - rows
/// with Zoom's own text, so a row says who is host, co-host, muted - for attendance, for the
/// co-host bridge and for deciding when the class is over.
/// </summary>
public sealed class WebJoinedList(Func<IPage?> page) : IAttendanceParticipantSource
{
    public AttendanceSource Source => AttendanceSource.Web;

    public async Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var current = page() ?? throw new InvalidOperationException("the meeting page is not open");
        if (current.IsClosed) throw new InvalidOperationException("the meeting page is closed");
        var found = await WebParticipantList.FindAsync(current, cancellationToken);
        if (found.List is not { } list)
            throw new InvalidOperationException($"no participants list on the page ({found.Seen})");
        return await new WebAttendanceParticipantSource(current, _ => list).ReadAsync(cancellationToken);
    }
}

/// <summary>
/// Who teaches each kind of session, as the admin's Windows app keeps it: fetched from the server
/// at the start of each class, and read by the same <see cref="SessionRoleBridge"/> the Windows
/// app runs. What the bridge learns - which Zoom name turned out to be which instructor - is kept
/// on this worker's volume, as the Windows app keeps it on its PC.
/// </summary>
public sealed class ServerSessionRoles(HttpClient http, Func<string?> deviceToken, Action<string>? log = null)
    : ISessionRoleStore
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly JsonSessionRoleStore _local = new();
    private IReadOnlyList<SessionRoleProfile> _profiles = [];

    private sealed record Answer([property: JsonPropertyName("profiles")] List<SessionRoleProfile>? Profiles);

    /// <summary>How many profiles the server holds, after the last refresh.</summary>
    public int Count => _profiles.Count;

    /// <summary>Asks the server again. A failure keeps what was there, and says so.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (deviceToken() is not { Length: > 0 } token) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/agent/session-roles");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log($"[roles] the server did not give the session roles: {(int)response.StatusCode}");
                return;
            }
            var answer = await response.Content.ReadFromJsonAsync<Answer>(Json, cancellationToken);
            var document = JsonSessionRoleStore.Validate(new SessionRoleDocument { Profiles = answer?.Profiles ?? [] });
            _profiles = document.Profiles;
            _log($"[roles] {_profiles.Count} session type(s) from the server");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception problem)
        {
            _log($"[roles] could not read the session roles: {problem.GetType().Name}: {problem.Message}");
        }
    }

    public SessionRoleDocument Load()
    {
        List<RoleAssignment> history;
        try { history = _local.Load().History; }
        catch (Exception) { history = []; }
        return new SessionRoleDocument { Profiles = [.. _profiles], History = history };
    }

    /// <summary>What was learnt is kept here; the profiles themselves are the admin's, on the server.</summary>
    public void Save(SessionRoleDocument document) => _local.Save(document);
}

/// <summary>The class's own name from the timetable, which is what tells the bridge its session type.</summary>
public sealed class ClassTitle(string? title) : ISessionNameSource
{
    public string? Describe(string accountId, DateTimeOffset startTime) => title;
}

/// <summary>
/// The switches the Windows app keeps beside a meeting - making the instructor co-host, and ending a
/// class once the rule says it is over - which the dashboard holds for the workers. Read once at the
/// start of each class; a server that cannot be reached leaves a class behaving as the app does.
/// </summary>
public sealed class ServerPolicy(HttpClient http, Func<string?> deviceToken, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public bool AutoCoHost { get; private set; } = true;
    public bool AutoEnd { get; private set; } = true;

    private sealed record Answer(bool? AutoCoHost, bool? AutoEnd);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (deviceToken() is not { Length: > 0 } token) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/agent/policy");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log($"[policy] the server did not give the switches: {(int)response.StatusCode}");
                return;
            }
            var answer = await response.Content.ReadFromJsonAsync<Answer>(cancellationToken: cancellationToken);
            if (answer is null) return;
            AutoCoHost = answer.AutoCoHost ?? true;
            AutoEnd = answer.AutoEnd ?? true;
            _log($"[policy] co-host {(AutoCoHost ? "on" : "off")}, ending a class {(AutoEnd ? "on" : "off")}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception problem)
        {
            _log($"[policy] could not read the switches: {problem.GetType().Name}: {problem.Message}");
        }
    }
}
