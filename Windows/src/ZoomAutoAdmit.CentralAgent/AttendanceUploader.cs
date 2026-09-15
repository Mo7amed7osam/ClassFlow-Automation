using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// Sends the attendance this PC takes in meetings to the central backend, where it is matched
/// against the group's roster (POST /api/v1/attendance/snapshots, with the device token).
///
/// It only reads the files the attendance collector already writes -
/// %LOCALAPPDATA%\ZoomAutoAdmit\Attendance\&lt;session&gt;\*.json - so a meeting never waits on the
/// network, and whatever this PC saw while the backend was away is sent, in order, once it is back.
/// What was sent is remembered in attendance-uploads.json; the backend also ignores a snapshot it
/// already has, so a repeat is harmless.
///
///   the Windows session id  -> the backend session's reference ("win:&lt;id&gt;")
///   meeting.accountId       -> the group (the LMS group code)
///   meeting.scheduledStart  -> the session's date and start time, as this PC's clock showed them
///   a failed read at the end of the meeting (.issue) -> "the meeting ended", without names
/// </summary>
public sealed class AttendanceUploader
{
    public enum Outcome { Sent, Refused, Retry, NothingToSend }

    /// <summary>One look at the folder: sent, refused for good, and whether it stopped to retry later.</summary>
    public sealed record Pass(int Sent, int Refused, bool Waiting);

    public const int MaximumNames = 1000;
    private static readonly Regex Group = new(@"^[A-Za-z0-9][A-Za-z0-9 _.-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Uri _endpoint;
    private readonly IDeviceTokenStore _tokens;
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly string _statePath;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string? _waitingFor;

    /// <summary>How often the folder is looked at. Snapshots are taken every few minutes at most.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Older files are left alone: a new registration does not send a year of history.</summary>
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The collector keeps times in UTC; a session's date and start time are the classes' local
    /// ones, as on the dashboard and in the recordings sheet: Cairo.
    /// </summary>
    public TimeZoneInfo TimeZone { get; init; } = Cairo;

    public static TimeZoneInfo Cairo { get; } = FindCairo();

    private static TimeZoneInfo FindCairo()
    {
        foreach (string id in new[] { "Africa/Cairo", "Egypt Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }

    public AttendanceUploader(Uri backendUrl, IDeviceTokenStore tokens, HttpClient http, string? attendanceRoot = null,
        string? statePath = null, Action<string>? log = null, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _endpoint = new Uri(CentralAgentSettings.ParseBackendUrl(backendUrl.ToString()), "api/v1/attendance/snapshots");
        _tokens = tokens;
        _http = http;
        _root = attendanceRoot ?? DefaultRoot;
        _statePath = statePath ?? DefaultStatePath;
        _log = log ?? (_ => { });
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Where the collector writes (ZoomAutoAdmit.Attendance.JsonAttendanceSnapshotStore).</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Attendance");

    public static string DefaultStatePath => Path.Combine(CentralAgentPaths.Folder, "attendance-uploads.json");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await UploadPendingAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Waiting(ex.GetType().Name); }
            try { await _delay(Interval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<Pass> UploadPendingAsync(CancellationToken cancellationToken)
    {
        var state = LoadState();
        int sent = 0, refused = 0;
        foreach (var file in PendingFiles(state))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, detail) = await SendAsync(file, cancellationToken);
            if (outcome == Outcome.Retry)
            {
                // Stops here so the rest keeps its order; tried again at the next look.
                Waiting(detail);
                if (sent > 0) _log(AgentLog.Line("attendance_uploaded", ("snapshots", sent)));
                return new Pass(sent, refused, Waiting: true);
            }
            if (outcome is Outcome.Sent or Outcome.NothingToSend)
            {
                state.Sent.Add(file.Key);
                if (outcome == Outcome.Sent) sent++;
            }
            else
            {
                state.Refused[file.Key] = detail;
                refused++;
                _log(AgentLog.Line("attendance_refused", ("file", file.Key), ("reason", detail)));
            }
            SaveState(state);
        }
        if (_waitingFor is not null)
        {
            _log(AgentLog.Line("attendance_upload_resumed"));
            _waitingFor = null;
        }
        if (sent > 0) _log(AgentLog.Line("attendance_uploaded", ("snapshots", sent)));
        return new Pass(sent, refused, Waiting: false);
    }

    /// <summary>Logged once per reason, not every 30 seconds while the backend is away.</summary>
    private void Waiting(string reason)
    {
        if (_waitingFor == reason) return;
        _waitingFor = reason;
        _log(AgentLog.Line("attendance_upload_waiting", ("reason", reason)));
    }

    // ------------------------------------------------------------------ the files

    private sealed record PendingFile(string Key, string Path, string SessionFolder, bool IsIssue);

    private IEnumerable<PendingFile> PendingFiles(UploadState state)
    {
        if (!Directory.Exists(_root)) return [];
        var oldest = _time.GetUtcNow().UtcDateTime - MaximumAge;
        var files = new List<PendingFile>();
        foreach (string folder in Directory.EnumerateDirectories(_root))
        {
            string session = System.IO.Path.GetFileName(folder);
            if (!Guid.TryParse(session, out _)) continue;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                string extension = System.IO.Path.GetExtension(path);
                bool issue = extension.Equals(".issue", StringComparison.OrdinalIgnoreCase);
                if (!issue && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string key = $"{session}/{System.IO.Path.GetFileName(path)}";
                if (state.Sent.Contains(key) || state.Refused.ContainsKey(key)) continue;
                if (File.GetLastWriteTimeUtc(path) < oldest) continue;
                files.Add(new PendingFile(key, path, folder, issue));
            }
        }
        // The collector names each file after its time: this is the order they were taken in.
        return files.OrderBy(f => System.IO.Path.GetFileName(f.Path), StringComparer.Ordinal).ThenBy(f => f.Key, StringComparer.Ordinal);
    }

    private async Task<(Outcome, string)> SendAsync(PendingFile file, CancellationToken cancellationToken)
    {
        JsonObject? body;
        string reason;
        try { (body, reason) = file.IsIssue ? BuildEnded(file, TimeZone) : Build(file, File.ReadAllText(file.Path), TimeZone); }
        catch (IOException) { return (Outcome.Retry, "fileBusy"); }
        catch (UnauthorizedAccessException) { return (Outcome.Retry, "fileBusy"); }
        if (body is null) return (reason == "" ? Outcome.NothingToSend : Outcome.Refused, reason);

        string? token = _tokens.Read();
        if (string.IsNullOrWhiteSpace(token)) return (Outcome.Retry, "noDeviceToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
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
                // The backend will never take this one (a bad group, a session of another group): skip it.
                400 or 409 or 413 or 422 => (Outcome.Refused, $"http{status}"),
                401 => (Outcome.Retry, "unauthorized"),
                404 => (Outcome.Retry, "noAttendanceOnBackend"),     // a backend from before attendance
                _ => (Outcome.Retry, $"http{status}"),
            };
        }
    }

    /// <summary>A collector snapshot as the backend's snapshot. Null and a reason when it cannot be sent.</summary>
    internal static (JsonObject? Body, string Reason) Build(string key, string text, TimeZoneInfo zone) =>
        Build(new PendingFile(key, "", "", false), text, zone);

    private static (JsonObject? Body, string Reason) Build(PendingFile file, string text, TimeZoneInfo zone)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return (null, "unreadable"); }
        if (root.ValueKind != JsonValueKind.Object) return (null, "unreadable");
        if (Session(root) is not { } session) return (null, "noSessionId");
        if (!TryTime(root, "timestamp", out var captured)) return (null, "noTimestamp");
        if (SessionOf(root, session, zone) is not { } meeting) return (null, meetingProblem(root));

        var names = new JsonArray();
        if (root.TryGetProperty("participants", out var participants) && participants.ValueKind == JsonValueKind.Array)
        {
            foreach (var participant in participants.EnumerateArray())
            {
                string? name = participant.ValueKind switch
                {
                    JsonValueKind.Object when participant.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                    JsonValueKind.String => participant.GetString(),
                    _ => null,
                };
                name = name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                names.Add(name.Length > 300 ? name[..300] : name);
                if (names.Count == MaximumNames) break;
            }
        }
        string trigger = Text(root, "trigger") ?? "";
        return (new JsonObject
        {
            ["clientSnapshotId"] = $"win:{session:N}:{System.IO.Path.GetFileNameWithoutExtension(file.Key)}",
            ["session"] = meeting,
            ["capturedAt"] = captured.ToString("O", CultureInfo.InvariantCulture),
            ["source"] = string.Equals(Text(root, "source"), "Web", StringComparison.OrdinalIgnoreCase) ? "web" : "desktop",
            ["trigger"] = TriggerName(trigger),
            ["isComplete"] = root.TryGetProperty("isComplete", out var complete) && complete.ValueKind == JsonValueKind.True,
            ["ended"] = trigger == "MeetingEnd",
            ["participants"] = names,
        }, "");

        static string meetingProblem(JsonElement root) =>
            root.TryGetProperty("meeting", out var m) && m.ValueKind == JsonValueKind.Object ? "groupNotUsable" : "noMeeting";
    }

    /// <summary>
    /// The last read of a meeting failed: the backend is still told the meeting ended - with no
    /// names, marked incomplete, so nobody counts as having left. The session details come from a
    /// snapshot of the same meeting; with none, there is nothing to end.
    /// </summary>
    private static (JsonObject? Body, string Reason) BuildEnded(PendingFile file, TimeZoneInfo zone)
    {
        JsonElement root;
        try
        {
            using var issue = JsonDocument.Parse(File.ReadAllText(file.Path));
            root = issue.RootElement.Clone();
        }
        catch (JsonException) { return (null, "unreadable"); }
        // Only the end matters: a failed read in the middle of the meeting has nothing to tell.
        if (root.ValueKind != JsonValueKind.Object || Text(root, "trigger") != "MeetingEnd") return (null, "");
        if (Session(root) is not { } session || !TryTime(root, "timestamp", out var ended)) return (null, "unreadable");
        JsonObject? meeting = null;
        foreach (string path in Directory.EnumerateFiles(file.SessionFolder, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var snapshot = JsonDocument.Parse(File.ReadAllText(path));
                if ((meeting = SessionOf(snapshot.RootElement, session, zone)) is not null) break;
            }
            catch (JsonException) { }
        }
        if (meeting is null) return (null, "noSnapshotOfThisMeeting");
        return (new JsonObject
        {
            ["clientSnapshotId"] = $"win:{session:N}:{System.IO.Path.GetFileNameWithoutExtension(file.Key)}",
            ["session"] = meeting,
            ["capturedAt"] = ended.ToString("O", CultureInfo.InvariantCulture),
            ["source"] = "desktop",
            ["trigger"] = "meeting_end",
            ["isComplete"] = false,
            ["ended"] = true,
            ["participants"] = new JsonArray(),
        }, "");
    }

    private static Guid? Session(JsonElement root) =>
        root.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var guid) ? guid : null;

    private static JsonObject? SessionOf(JsonElement root, Guid session, TimeZoneInfo zone)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("meeting", out var meeting) || meeting.ValueKind != JsonValueKind.Object)
            return null;
        string group = Text(meeting, "accountId")?.Trim() ?? "";
        if (!Group.IsMatch(group) || !TryTime(meeting, "scheduledStart", out var utc)) return null;
        var start = TimeZoneInfo.ConvertTime(utc, zone);
        var body = new JsonObject
        {
            ["ref"] = $"win:{session:D}",
            ["group"] = group,
            ["date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["startTime"] = start.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
        if (Text(meeting, "meetingUrl") is { Length: > 0 and <= 2048 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            body["meetingUrl"] = uri.GetLeftPart(UriPartial.Path);     // never a passcode
        return body;
    }

    private static string TriggerName(string trigger) => trigger switch
    {
        "MeetingStart" => "meeting_start",
        "Interval" => "scheduled",
        "Admission" => "admission",
        "Manual" => "manual",
        "MeetingEnd" => "meeting_end",
        _ => "scheduled",
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryTime(JsonElement element, string name, out DateTimeOffset time)
    {
        time = default;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    // ------------------------------------------------------------------ what was sent

    private sealed class UploadState
    {
        public int SchemaVersion { get; set; } = 1;
        public HashSet<string> Sent { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Refused { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private UploadState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new UploadState();
            var state = JsonSerializer.Deserialize<UploadState>(File.ReadAllText(_statePath), StateJson) ?? new UploadState();
            return new UploadState
            {
                Sent = new HashSet<string>(state.Sent ?? [], StringComparer.OrdinalIgnoreCase),
                Refused = new Dictionary<string, string>(state.Refused ?? [], StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (JsonException)
        {
            // Everything is sent again; the backend recognises what it already has.
            return new UploadState();
        }
    }

    private void SaveState(UploadState state)
    {
        // Files the collector no longer has are forgotten, so this stays small.
        state.Sent.RemoveWhere(key => !File.Exists(System.IO.Path.Combine(_root, key)));
        foreach (string key in state.Refused.Keys.Where(key => !File.Exists(System.IO.Path.Combine(_root, key))).ToList())
            state.Refused.Remove(key);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_statePath)!);
        string temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJson));
        File.Move(temporary, _statePath, overwrite: true);
    }
}
