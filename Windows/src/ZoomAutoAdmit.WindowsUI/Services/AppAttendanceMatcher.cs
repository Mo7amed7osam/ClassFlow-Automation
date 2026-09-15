using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Matches each class's Zoom names to its roster by itself, once an hour counted from the class's
/// time (at 55 minutes, 1 h 55, …) - the Attendance page's "Match with AI" without anyone opening
/// the page. The name rules go first, then the AI for what they cannot settle (with the app's key,
/// when one is saved). The result is kept for the LMS upload, which happens an hour in: the upload
/// asks for a fresh match first if this one is old, so it never goes up unmatched.
/// </summary>
public sealed class AppAttendanceMatcher(
    ExtensionAttendanceFeed? feed = null,
    IGroupRosterService? rosters = null,
    IAiMatchingService? matching = null,
    IAiCredentialStore? key = null)
{
    public static readonly TimeSpan FirstMatchAfter = TimeSpan.FromMinutes(55);
    public static readonly TimeSpan MatchEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan StopAfter = TimeSpan.FromHours(4.5);

    private readonly ExtensionAttendanceFeed _feed = feed ?? new ExtensionAttendanceFeed();
    private readonly IGroupRosterService _rosters = rosters ?? new GroupRosterStore(log: _ => { });
    private readonly IAiMatchingService _matching = matching ?? new AiMatchingService();
    private readonly IAiCredentialStore _key = key ?? new AiCredentialStore();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, int> _slotsDone = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which hourly match a class is due (0 at 55 min, 1 at 1 h 55, …), or -1 before the first.</summary>
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
    public async Task<ExtensionAttendanceFeed.ClassResult?> MatchClassAsync(string group, DateTime classStart, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await MatchUnlockedAsync(group, classStart, token); }
        finally { _gate.Release(); }
    }

    private async Task<ExtensionAttendanceFeed.ClassResult?> MatchUnlockedAsync(string group, DateTime classStart, CancellationToken token)
    {
        // The class as it was recorded: a meeting opened by hand at 18:51 is recorded as 18:51 even
        // when its steps are the 19:00 class's, so the nearest recorded class of the group is used.
        var recorded = _feed.Since(classStart.AddHours(-2))
            .Where(s => s.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs((s.Start - classStart).TotalMinutes) <= ZoomAutoAdmit.WindowsRuntime.Scheduling.ScheduleTiming.SameClassWindow.TotalMinutes)
            .ToArray();
        if (recorded.Length == 0) return null;
        string classKey = recorded.MinBy(s => Math.Abs((s.Start - classStart).TotalMinutes))!.ClassKey;
        var names = recorded
            .Where(s => s.ClassKey.Equals(classKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s.Names).Select(n => n.Trim()).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return null;
        var roster = (await _rosters.ListAsync(token)).FirstOrDefault(g => g.GroupId.Equals(group, StringComparison.OrdinalIgnoreCase));
        if (roster == null || roster.Students.Count == 0)
        {
            ConsoleLogger.Warn($"[ATTENDANCE] {group}: no roster to match against (Groups & Students).");
            return null;
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
        ExtensionAttendanceFeed.SaveAppResults(group, classStart, present, review, absent);
        ConsoleLogger.Info($"[ATTENDANCE] {group} {classStart:HH:mm}: matched {names.Length} Zoom names{(settings != null ? " with the AI" : " by the name rules")} - " +
                           $"{present.Length} present, {review.Length} to review, {absent.Length} not seen.");
        return ExtensionAttendanceFeed.AppResultsFor(group, DateOnly.FromDateTime(classStart), TimeOnly.FromDateTime(classStart));
    }
}
