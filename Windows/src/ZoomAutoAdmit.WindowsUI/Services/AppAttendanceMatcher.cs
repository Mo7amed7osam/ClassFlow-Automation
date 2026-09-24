using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Matches each class's Zoom names to its roster by itself, from the first quarter of an hour of the
/// class and every half hour after it - the Attendance page's "Match with AI" without anyone opening
/// the page, pressing Start attendance or loading a roster. The group is whichever one the meeting
/// belongs to; its roster is brought from the LMS when this PC has none. The name rules go first,
/// then the AI for what they cannot settle (with the app's key, when one is saved). The result is
/// kept for the LMS upload, which asks for a fresh match of its own before it uploads anything.
/// </summary>
public sealed class AppAttendanceMatcher(
    ExtensionAttendanceFeed? feed = null,
    IGroupRosterService? rosters = null,
    IAiMatchingService? matching = null,
    IAiCredentialStore? key = null)
{
    /// <summary>
    /// The first match of a class, soon after it opens: whoever is already in is matched while the
    /// class runs, so nothing waits for the hour and nobody has to start it by hand.
    /// </summary>
    public static readonly TimeSpan FirstMatchAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MatchEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StopAfter = TimeSpan.FromHours(4.5);

    private readonly ExtensionAttendanceFeed _feed = feed ?? new ExtensionAttendanceFeed();
    private readonly IGroupRosterService _rosters = rosters ?? new GroupRosterStore(log: _ => { });
    private readonly IAiMatchingService _matching = matching ?? new AiMatchingService();
    private readonly IAiCredentialStore _key = key ?? new AiCredentialStore();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _slotsDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _rosterAsked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A roster the LMS would not give is asked for again, not once and never more.</summary>
    public static readonly TimeSpan RosterRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>Reads a group's students from the LMS; the app replaces it in tests so nothing goes out.</summary>
    public Func<string, CancellationToken, Task<bool>> ImportRoster { get; init; } =
        async (group, token) => (await new LmsRosterImport().ImportAsync(group, token)).Ok;

    /// <summary>The group's roster, brought from the LMS once per app run when this PC has none.</summary>
    private async Task<RosterGroup?> BringRosterAsync(string group, CancellationToken token)
    {
        if (_rosterAsked.TryGetValue(group, out var asked) && DateTime.Now - asked < RosterRetryAfter)
        {
            ConsoleLogger.Warn($"[ATTENDANCE] {group}: no roster to match against (Groups & Students).");
            return null;
        }
        _rosterAsked[group] = DateTime.Now;
        ConsoleLogger.Info($"[ATTENDANCE] {group}: no roster on this PC; reading its students from the LMS.");
        bool ok;
        try { ok = await ImportRoster(group, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ConsoleLogger.Warn($"[ATTENDANCE] {group}: the roster could not be read from the LMS ({ex.Message})."); return null; }
        var roster = ok
            ? (await _rosters.ListAsync(token)).FirstOrDefault(g => g.GroupId.Equals(group, StringComparison.OrdinalIgnoreCase))
            : null;
        if (roster == null || roster.Students.Count == 0)
        {
            ConsoleLogger.Warn($"[ATTENDANCE] {group}: no roster to match against (Groups & Students).");
            return null;
        }
        // It is there now: a later class of the group asks for it again only if it disappears.
        _rosterAsked.Remove(group);
        ConsoleLogger.Info($"[ATTENDANCE] {group}: {roster.Students.Count} students read from the LMS.");
        return roster;
    }

    /// <summary>Which match a class is due (0 a quarter of an hour in, 1 at 45 minutes, …), or -1 before the first.</summary>
    public static int SlotAt(DateTime classStart, DateTime now)
    {
        var age = now - classStart;
        if (age < FirstMatchAfter || age > StopAfter) return -1;
        return (int)((age - FirstMatchAfter).Ticks / MatchEvery.Ticks);
    }

    /// <summary>Called every half minute by the app; matches the classes whose hour has come.</summary>
    public async Task TickAsync(DateTime now, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return;           // the previous round is still matching
        try
        {
            foreach (var tab in _feed.Tabs())
            {
                int slot = SlotAt(tab.Start, now);
                string key = $"{tab.Group}|{tab.Start:yyyy-MM-dd HH:mm}";
                if (slot < 0 || (_slotsDone.TryGetValue(key, out int done) && done >= slot)) continue;
                _slotsDone[key] = slot;
                await MatchUnlockedAsync(tab.Group, tab.Start, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ConsoleLogger.Warn($"[ATTENDANCE] Hourly matching failed: {ex.Message}"); }
        finally { _gate.Release(); }
    }

    /// <summary>Matches one class now (the LMS upload asks for this when the last match is old).</summary>
    /// <param name="extraNames">Names seen elsewhere for this class (Zoom's participants report), matched with the recordings.</param>
    public async Task<ExtensionAttendanceFeed.ClassResult?> MatchClassAsync(string group, DateTime classStart, CancellationToken token = default,
        IEnumerable<string>? extraNames = null)
    {
        await _gate.WaitAsync(token);
        try { return await MatchUnlockedAsync(group, classStart, token, extraNames); }
        finally { _gate.Release(); }
    }

    private async Task<ExtensionAttendanceFeed.ClassResult?> MatchUnlockedAsync(string group, DateTime classStart, CancellationToken token,
        IEnumerable<string>? extraNames = null)
    {
        // The class as it was recorded: a meeting opened by hand at 18:51 is recorded as 18:51 even
        // when its steps are the 19:00 class's, so the nearest recorded class of the group is used.
        var recorded = _feed.Since(classStart.AddHours(-2))
            .Where(s => s.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
                        s.Start.Date == classStart.Date &&
                        ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.IsSameClass(classStart.TimeOfDay, s.Start.TimeOfDay))
            .ToArray();
        var extra = (extraNames ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();
        if (recorded.Length == 0 && extra.Length == 0) return null;
        // Every recording of this class counts: a class reopened while it runs (the app restarted at
        // 19:31, the meeting opened again on the web at 20:43) is recorded under each start, and
        // taking only the nearest one uploaded 4 present from a 5-name recording (2026-09-16).
        var names = recorded
            .SelectMany(s => s.Names).Concat(extra).Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return null;
        var roster = (await _rosters.ListAsync(token)).FirstOrDefault(g => g.GroupId.Equals(group, StringComparison.OrdinalIgnoreCase));
        if (roster == null || roster.Students.Count == 0)
        {
            // The names of a group this PC has never read: brought from the LMS by itself, the way
            // "Load roster" does on the Attendance page, so a first class is not missed over it.
            roster = await BringRosterAsync(group, token);
            if (roster == null) return null;
        }

        AiConnectionSettings? settings = null;
        try { settings = _key.Read(); } catch { }
        AttendanceMatchResult result;
        try
        {
            result = settings != null
                ? await _matching.MatchAsync(settings, roster, names, token)
                : await _matching.MatchWithRulesOnlyAsync(roster, names, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && settings != null)
        {
            // The AI failing (no credit left, no connection) still leaves the rules' answer.
            ConsoleLogger.Warn($"[ATTENDANCE] {group}: the AI could not be reached ({ex.Message}); matched by the name rules only.");
            result = await _matching.MatchWithRulesOnlyAsync(roster, names, token);
        }
        string[] Of(AttendanceMatchStatus status) =>
            [.. result.Students.Where(s => s.Status == status).OrderBy(s => s.Order).Select(s => s.StudentName)];
        var present = Of(AttendanceMatchStatus.Present);
        var review = Of(AttendanceMatchStatus.NeedsReview);
        var absent = Of(AttendanceMatchStatus.NotObserved);
        // Who the uncertain ones might be, kept with them: the class card asks about each by name
        // ("Youssef Ayoub - seen as 'Yousef A', 72%") instead of only counting them.
        var attention = result.Students
            .Where(s => s.Status == AttendanceMatchStatus.NeedsReview)
            .OrderBy(s => s.Order)
            .Select(s => new ExtensionAttendanceFeed.ReviewName(
                s.StudentName,
                s.ObservedNames.FirstOrDefault() ?? "",
                s.Confidence,
                s.MatchSource.ToString()))
            .ToArray();
        ExtensionAttendanceFeed.SaveAppResults(group, classStart, present, review, absent, attention);
        ConsoleLogger.Info($"[ATTENDANCE] {group} {classStart:HH:mm}: matched {names.Length} Zoom names{(settings != null ? " with the AI" : " by the name rules")} - " +
                           $"{present.Length} present, {review.Length} to review, {absent.Length} not seen.");
        return ExtensionAttendanceFeed.AppResultsFor(group, DateOnly.FromDateTime(classStart), TimeOnly.FromDateTime(classStart));
    }
}
