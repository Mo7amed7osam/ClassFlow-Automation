using System.Text.RegularExpressions;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Zoom;

namespace ZoomAutoAdmit.WebAutomation.Recordings;

/// <summary>One session whose Zoom recording should be put on the dashboard.</summary>
public sealed record RecordingLinkRequest
{
    public required string Group { get; init; }
    /// <summary>The session's day, in this computer's time. Today when absent.</summary>
    public DateOnly? Date { get; init; }
    /// <summary>The session's time, in this computer's time. Absent, any recording of that day will do.</summary>
    public TimeOnly? StartTime { get; init; }
    /// <summary>A browser profile name; absent or "default" means the account's own profile.</summary>
    public string? Profile { get; init; }
    public bool Headed { get; init; }
    public bool DryRun { get; init; }
    public bool ReplaceExisting { get; init; }
    /// <summary>Leave the dashboard browser open afterwards. The terminal's --headed; never the API.</summary>
    public bool KeepBrowserOpen { get; init; }
}

/// <summary>
/// A session and the recording link to put on it, handed in by the caller - the HTTP API passes the
/// Google Drive link n8n read from the recordings sheet. Nothing is looked up in Zoom.
/// </summary>
public sealed record ProvidedRecordLinkRequest
{
    public required string Group { get; init; }
    /// <summary>Written exactly as given.</summary>
    public required string RecordLink { get; init; }
    /// <summary>The session's day. Today when absent.</summary>
    public DateOnly? Date { get; init; }
    /// <summary>
    /// Only to choose between several dashboard sessions of the group on the same day; it is never
    /// worked out by the application. Absent, a day with one session is enough.
    /// </summary>
    public TimeOnly? StartTime { get; init; }
    public bool Headed { get; init; }
    public bool DryRun { get; init; }
    public bool ReplaceExisting { get; init; }
}

public enum RecordingLinkStatus
{
    Attached,
    AlreadyExists,
    DryRun,
    RecordingNotFound,
    Busy,
    ZoomFailed,
    LmsFailed,
}

/// <summary>What processing one session ended with, in terms any caller can act on.</summary>
public sealed record RecordingLinkOutcome(RecordingLinkStatus Status, string Message)
{
    public required string Group { get; init; }
    public required DateOnly Date { get; init; }
    public TimeOnly? StartTime { get; init; }
    public required string Profile { get; init; }
    /// <summary>A short machine-readable cause for a failure, e.g. "sessionNotFinished".</summary>
    public string? Reason { get; init; }
    public DateTimeOffset? RecordingStartedAtUtc { get; init; }
    public TimeSpan? RecordingDuration { get; init; }
    /// <summary>The start of the share link, for a log line. The full link opens the recording to anyone.</summary>
    public string? ShareLinkPreview { get; init; }

    public bool IsSuccess => Status is RecordingLinkStatus.Attached or RecordingLinkStatus.AlreadyExists or RecordingLinkStatus.DryRun;
}

/// <summary>Where the Zoom share link comes from.</summary>
public interface IRecordingLinkSource
{
    Task<ZoomRecordingLinkResult> ReadAsync(string group, string profile, DateOnly day, TimeOnly? startTime,
        bool headed, CancellationToken cancellationToken);
}

/// <summary>Where it is written.</summary>
public interface IRecordingLinkTarget
{
    Task<LmsRunResult> AttachAsync(string group, string recordLink, TimeOnly? startTime, DateOnly day, bool headed,
        bool dryRun, bool keepBrowserOpen, bool replaceExisting, CancellationToken cancellationToken);
}

public interface IRecordingLinkProcessor
{
    /// <summary>Find the session's recording in Zoom, then attach its share link (lms-record-link).</summary>
    Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Attach a link the caller already has (the HTTP API). Zoom is never opened.</summary>
    Task<RecordingLinkOutcome> AttachProvidedLinkAsync(ProvidedRecordLinkRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The one path from "this session's recording" to the link on the dashboard. The terminal's
/// lms-record-link and the HTTP API both come through here, so there is a single place where the
/// recording is picked, the profiles are taken, and the dashboard is written.
///
/// It takes each browser profile only for as long as it is used: the Zoom profile while the link is
/// read, then the dashboard profile while it is written. They are never held together, so two
/// requests can never each hold one and wait for the other.
/// </summary>
public sealed class RecordingLinkProcessor(
    IRecordingLinkSource source,
    IRecordingLinkTarget target,
    IProfileLock locks,
    Func<string, CancellationToken, Task<string?>>? accountProfile = null,
    Action<string>? log = null,
    TimeSpan? lockWait = null,
    Func<DateOnly>? today = null,
    Func<string, string>? dashboardProfile = null) : IRecordingLinkProcessor
{
    /// <summary>The browser profile the dashboard is driven with, for a group nobody else claims.</summary>
    public static string DashboardProfile => LmsSessionRunner.DashboardProfile;

    /// <summary>
    /// The dashboard profile this group is written with: its own coordinator's, when this PC runs
    /// other people's classes. The lock is taken on that name, so one coordinator's recording step
    /// does not wait behind another's - and never drives another's signed-in browser.
    /// </summary>
    private readonly Func<string, string> _dashboardProfile = dashboardProfile ?? (_ => DashboardProfile);

    /// <summary>How long to wait for a busy profile before answering "busy".</summary>
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromMinutes(2);

    private static readonly Regex SafeProfileName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    private readonly Action<string> _log = log ?? (_ => { });
    private readonly TimeSpan _wait = lockWait ?? DefaultLockWait;
    private readonly Func<DateOnly> _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));

    public static bool IsValidProfileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SafeProfileName.IsMatch(name.Trim());

    /// <summary>
    /// The profile to read Zoom with: the one named, or for "default" the account's own configured
    /// web profile - the same one its live meetings sign in with - and only failing that, a profile
    /// named after the group, which is what the terminal did before accounts carried a profile.
    /// </summary>
    public async Task<string> ResolveProfileAsync(string group, string? requested, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requested) && !requested.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
            return requested.Trim();
        if (accountProfile != null)
        {
            string? configured = await accountProfile(group, cancellationToken);
            if (IsValidProfileName(configured)) return configured!.Trim();
        }
        return group;
    }

    public async Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string group = request.Group?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(request.Group));
        DateOnly day = request.Date ?? _today();
        string profile = await ResolveProfileAsync(group, request.Profile, cancellationToken);
        string time = request.StartTime is { } t ? t.ToString("HH':'mm") : "any time";

        RecordingLinkOutcome Outcome(RecordingLinkStatus status, string message) =>
            new(status, message) { Group = group, Date = day, StartTime = request.StartTime, Profile = profile };

        _log($"[RECORDINGS] {group} {day:yyyy-MM-dd} {time}: profile '{profile}'" +
             $"{(request.DryRun ? ", dry run" : string.Empty)}{(request.ReplaceExisting ? ", replacing an existing link" : string.Empty)}.");

        // ---- read the link from Zoom, holding only the Zoom profile
        ZoomRecordingLinkResult recording;
        await using (var zoomProfile = await locks.TryAcquireAsync(profile, _wait, cancellationToken))
        {
            if (zoomProfile == null)
            {
                _log($"[RECORDINGS] {group}: the '{profile}' browser profile is busy.");
                return Outcome(RecordingLinkStatus.Busy,
                    $"The '{profile}' browser profile is in use by another operation or an open browser. Try again shortly.");
            }
            _log($"[RECORDINGS] {group}: searching Zoom for the recording.");
            try
            {
                recording = await source.ReadAsync(group, profile, day, request.StartTime, request.Headed, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"[RECORDINGS] {group}: reading Zoom failed ({ex.GetType().Name}).");
                return Outcome(RecordingLinkStatus.ZoomFailed, "Zoom could not be read.") with { Reason = "zoomFailed" };
            }
        }

        if (!recording.IsSuccess || recording.ShareUrl == null)
        {
            _log($"[RECORDINGS] {group}: {recording.Message}");
            return recording.FailureKind switch
            {
                ZoomRecordingFailure.NotFound =>
                    Outcome(RecordingLinkStatus.RecordingNotFound, recording.Message) with { Reason = "notFound" },
                ZoomRecordingFailure.TimeMismatch =>
                    Outcome(RecordingLinkStatus.RecordingNotFound, recording.Message) with
                    { Reason = "timeMismatch", RecordingStartedAtUtc = recording.StartedAtUtc },
                ZoomRecordingFailure.NotSignedIn =>
                    Outcome(RecordingLinkStatus.ZoomFailed, recording.Message) with { Reason = "zoomNotSignedIn" },
                _ => Outcome(RecordingLinkStatus.ZoomFailed, recording.Message) with { Reason = "zoomFailed" },
            };
        }

        string preview = Preview(recording.ShareUrl);
        _log($"[RECORDINGS] {group}: recording found" +
             $"{(recording.StartedAtUtc is { } s ? $", started {s.ToLocalTime():yyyy-MM-dd HH:mm} local" : string.Empty)}" +
             $"{(recording.Recording?.Duration is { } d ? $", {d:hh\\:mm\\:ss} long" : string.Empty)} ({preview}).");

        var found = Outcome(RecordingLinkStatus.Attached, string.Empty) with
        {
            RecordingStartedAtUtc = recording.StartedAtUtc,
            RecordingDuration = recording.Recording?.Duration,
            ShareLinkPreview = preview,
        };
        return await WriteToDashboardAsync(found, recording.ShareUrl, request.StartTime, request.Headed,
            request.DryRun, request.KeepBrowserOpen, request.ReplaceExisting, cancellationToken);
    }

    /// <summary>
    /// Puts a link the caller already has on the session. There is no Zoom step at all: no Zoom
    /// profile is taken, no Zoom page is opened, and the link goes to the dashboard exactly as given.
    /// The dashboard profile is still held for the length of the write, as for every other writer.
    /// </summary>
    public async Task<RecordingLinkOutcome> AttachProvidedLinkAsync(ProvidedRecordLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string group = request.Group?.Trim() ?? string.Empty;
        ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(request.Group));
        string link = request.RecordLink ?? string.Empty;
        DateOnly day = request.Date ?? _today();
        string preview = RecordingLinks.Preview(link);

        var outcome = new RecordingLinkOutcome(RecordingLinkStatus.Attached, string.Empty)
        {
            Group = group,
            Date = day,
            StartTime = request.StartTime,
            Profile = _dashboardProfile(group),
            ShareLinkPreview = preview,
        };
        var kind = RecordingLinks.Classify(link);
        _log($"[RECORDINGS] {group} {day:yyyy-MM-dd}: attaching a given {(kind == RecordingLinkKind.GoogleDrive ? "Google Drive" : kind == RecordingLinkKind.ZoomShare ? "Zoom" : "unrecognised")} link ({preview})" +
             $"{(request.DryRun ? ", dry run" : string.Empty)}{(request.ReplaceExisting ? ", replacing an existing link" : string.Empty)}.");
        if (kind == RecordingLinkKind.None)
            return outcome with
            {
                Status = RecordingLinkStatus.LmsFailed,
                Message = "That is not a Zoom recording link or a Google Drive file link, so nothing was saved.",
                Reason = "invalidLink",
            };

        return await WriteToDashboardAsync(outcome, link, request.StartTime, request.Headed, request.DryRun,
            keepBrowserOpen: false, request.ReplaceExisting, cancellationToken);
    }

    /// <summary>
    /// The dashboard half, the same for a link found in Zoom and a link handed in: take the
    /// dashboard profile, write the link, and turn the dashboard's answer into an outcome.
    /// </summary>
    private async Task<RecordingLinkOutcome> WriteToDashboardAsync(RecordingLinkOutcome found, string link,
        TimeOnly? startTime, bool headed, bool dryRun, bool keepBrowserOpen, bool replaceExisting,
        CancellationToken cancellationToken)
    {
        string group = found.Group;
        LmsRunResult attached;
        string profile = _dashboardProfile(group);
        await using (var dashboard = await locks.TryAcquireAsync(profile, _wait, cancellationToken))
        {
            if (dashboard == null)
            {
                _log($"[RECORDINGS] {group}: the dashboard browser profile is busy.");
                return found with
                {
                    Status = RecordingLinkStatus.Busy,
                    Message = "The dashboard browser profile is in use by another operation or an open browser. Try again shortly.",
                };
            }
            _log($"[RECORDINGS] {group}: attaching the link on the dashboard.");
            try
            {
                attached = await target.AttachAsync(group, link, startTime, found.Date, headed,
                    dryRun, keepBrowserOpen, replaceExisting, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"[RECORDINGS] {group}: the dashboard failed ({ex.GetType().Name}).");
                return found with { Status = RecordingLinkStatus.LmsFailed, Message = "The dashboard could not be reached.", Reason = "lmsFailed" };
            }
        }

        if (!attached.IsSuccess)
        {
            _log($"[RECORDINGS] {group}: attaching failed - {attached.Message}");
            return found with
            {
                Status = RecordingLinkStatus.LmsFailed,
                Message = attached.Message,
                Reason = attached.FailureKind switch
                {
                    LmsFailure.NotSignedIn => "lmsNotSignedIn",
                    LmsFailure.SessionNotFound => "sessionNotFound",
                    LmsFailure.SessionNotFinished => "sessionNotFinished",
                    LmsFailure.InvalidLink => "invalidLink",
                    LmsFailure.RecordingConflict => "recordingConflict",
                    _ => "lmsFailed",
                },
            };
        }
        var status = attached.AlreadyExists ? RecordingLinkStatus.AlreadyExists
            : dryRun ? RecordingLinkStatus.DryRun
            : RecordingLinkStatus.Attached;
        _log($"[RECORDINGS] {group}: {status} - {attached.Message}");
        return found with { Status = status, Message = attached.Message };
    }

    /// <summary>Enough of a share link to recognise it in a log, not enough to open it.</summary>
    public static string Preview(string url) => RecordingLinks.Preview(url);
}

/// <summary>The real Zoom side: My Recordings, read through the signed-in browser profile.</summary>
public sealed class ZoomRecordingSource(ZoomRecordingLinkReader reader) : IRecordingLinkSource
{
    public Task<ZoomRecordingLinkResult> ReadAsync(string group, string profile, DateOnly day, TimeOnly? startTime,
        bool headed, CancellationToken cancellationToken) =>
        reader.ReadAsync(group, profile, day, startTime, headed, keepBrowserOpen: false, cancellationToken);
}

/// <summary>The real dashboard side, signed in as whoever the group belongs to.</summary>
public sealed class LmsRecordingTarget(Func<string, LmsSessionRunner> runnerFor) : IRecordingLinkTarget
{
    /// <summary>The same sign-in for every group: a PC that runs only its own classes.</summary>
    public LmsRecordingTarget(LmsSessionRunner runner) : this(_ => runner) { }

    public Task<LmsRunResult> AttachAsync(string group, string recordLink, TimeOnly? startTime, DateOnly day, bool headed,
        bool dryRun, bool keepBrowserOpen, bool replaceExisting, CancellationToken cancellationToken) =>
        runnerFor(group).AttachRecordLinkAsync(group, recordLink, startTime, day, headed, dryRun, keepBrowserOpen,
            replaceExisting, cancellationToken);
}
