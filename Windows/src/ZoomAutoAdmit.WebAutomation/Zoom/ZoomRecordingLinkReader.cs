using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Zoom;

/// <summary>
/// One recording as the list shows it: what it is called, when it was recorded, and where its own
/// page is. A group records the same name week after week, so the time is what tells them apart.
/// </summary>
public sealed record ZoomRecordingEntry(string Topic, string DetailUrl)
{
    public DateTime? RecordedAt { get; init; }
    /// <summary>How long it runs, as the list prints it. A restarted class leaves a short one behind.</summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>Why a recording could not be read, for callers that answer differently to each.</summary>
public enum ZoomRecordingFailure
{
    None,
    /// <summary>The browser profile is not signed in to Zoom.</summary>
    NotSignedIn,
    /// <summary>No recording of this group matches the requested day and time.</summary>
    NotFound,
    /// <summary>The recording Zoom handed over starts outside the requested session.</summary>
    TimeMismatch,
    /// <summary>Anything else: a page that did not load, a button that did not answer.</summary>
    Failed,
}

public sealed record ZoomRecordingLinkResult(bool IsSuccess, string Message, string? ShareUrl = null)
{
    public ZoomRecordingFailure FailureKind { get; init; }
    /// <summary>The recording that was picked, when one was.</summary>
    public ZoomRecordingEntry? Recording { get; init; }
    /// <summary>
    /// When the recording really started, read from the share link itself rather than from the
    /// list's text - the list prints times in the Zoom account's own time zone.
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    public static ZoomRecordingLinkResult Success(string shareUrl, string message) => new(true, message, shareUrl);
    public static ZoomRecordingLinkResult Fail(ZoomRecordingFailure failure, string message) =>
        new(false, message) { FailureKind = failure };
}

/// <summary>
/// Reads the shareable link of a group's cloud recording, the way it is read by hand: open My
/// Recordings signed in as the account that owns them, find the recording named after the group,
/// open it and press "Copy shareable link". Zoom hands that link over on the clipboard, so the
/// clipboard is what is read - there is no field on the page holding it.
///
/// Nothing here changes a recording. It opens pages and presses one copy button.
/// </summary>
/// <param name="zoomDisplayTimeZone">
/// The time zone of the Zoom account's profile, which is the zone My Recordings prints its times in.
/// Given, those times are converted to this computer's time before a recording is picked. Not
/// given, they are read as they are - which only works when the two zones are the same.
/// </param>
/// <param name="localTimeZone">This computer's zone; the requested day and time are in it.</param>
public sealed class ZoomRecordingLinkReader(
    ZoomProfileManager? profiles = null,
    TimeZoneInfo? zoomDisplayTimeZone = null,
    TimeZoneInfo? localTimeZone = null)
{
    private const string RecordingsUrl = "https://zoom.us/recording/";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private readonly ZoomProfileManager _profiles = profiles ?? new ZoomProfileManager();
    private readonly TimeZoneInfo _local = localTimeZone ?? TimeZoneInfo.Local;

    /// <summary>
    /// The environment variable that names the Zoom account's time zone, as a Windows or IANA id
    /// (for example "Pacific Standard Time" or "America/Los_Angeles").
    /// </summary>
    public const string ZoomTimeZoneVariable = "ZOOM_AUTO_ADMIT_ZOOM_TIMEZONE";

    /// <summary>The configured Zoom account time zone, or null when none (or an unknown one) is set.</summary>
    public static TimeZoneInfo? ConfiguredZoomTimeZone(Func<string, string?>? readVariable = null)
    {
        string? id = (readVariable ?? Environment.GetEnvironmentVariable)(ZoomTimeZoneVariable)?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
    }

    /// <param name="group">The recording's name, which is the group, for example CAI5_AIS4_S7.</param>
    /// <param name="profileName">The signed-in browser profile that owns the recordings.</param>
    /// <param name="day">The session's day. Without it, any of the group's recordings will do.</param>
    /// <param name="startTime">The session's time, which separates two classes on the same day.</param>
    public async Task<ZoomRecordingLinkResult> ReadAsync(
        string group,
        string profileName,
        DateOnly? day = null,
        TimeOnly? startTime = null,
        bool headed = false,
        bool keepBrowserOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var profile = _profiles.GetOrCreate(profileName);
        var plan = new ZoomBrowserLaunchPlan(profile, Headless: !headed);
        var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        await using var closing = keepBrowserOpen ? null : session;
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

        string step = "opening My Recordings";
        try
        {
            await page.GotoAsync(RecordingsUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (page.Url.Contains("/signin", StringComparison.OrdinalIgnoreCase))
                return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.NotSignedIn,
                    $"The '{profileName}' browser profile is not signed in to Zoom, so the recordings could not be read.");

            step = "searching the recordings for the group";
            await SearchAsync(page, group, cancellationToken);

            step = "finding the group's recording";
            var entries = ToLocalTimes(await ReadEntriesAsync(page), zoomDisplayTimeZone, _local);
            var picked = PickRecording(entries, group, day, startTime);
            if (picked == null)
            {
                string when = day is { } d
                    ? $" on {d:yyyy-MM-dd}{(startTime is { } t ? $" around {t:HH\\:mm}" : string.Empty)}"
                    : string.Empty;
                return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.NotFound, entries.Count == 0
                    ? $"No cloud recording is listed for {group}{when}."
                    : $"None of the {entries.Count} listed recordings is {group}{when}.");
            }
            ConsoleLogger.Info(
                $"[RECORDING] Found {group}'s recording of " +
                $"{(picked.RecordedAt is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "an unknown time")}" +
                $"{(picked.Duration is { } length ? $", {length:hh\\:mm\\:ss} long" : string.Empty)}.");

            // Zoom answers both copy buttons on the clipboard, not on the page, so the clipboard has
            // to be readable before either is pressed.
            await session.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"],
                new() { Origin = new Uri(page.Url).GetLeftPart(UriPartial.Authority) });

            // A recording Zoom is still processing has no copy button on its own page, but the
            // list's Share dialog hands out its link all the same - so that is read first, and the
            // recording page is only the fallback.
            step = "copying the link from the list's Share dialog";
            string link = await CopyFromShareDialogAsync(page, picked, cancellationToken);
            if (link.Length == 0)
            {
                step = "opening the recording";
                await page.GotoAsync(picked.DetailUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                step = "copying the shareable link";
                var copy = page.Locator("[aria-label^='Copy shareable link']").First;
                try { await copy.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
                catch (TimeoutException)
                {
                    return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.Failed,
                        $"Neither the Share dialog nor the recording page for {group} handed over a link.");
                }
                await page.EvaluateAsync("() => navigator.clipboard.writeText('')");
                await copy.EvaluateAsync("element => element.click()");
                link = await WaitForClipboardLinkAsync(page, cancellationToken);
            }
            if (link.Length == 0)
                return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.Failed,
                    $"The shareable link for {group} was pressed but nothing was copied.");

            // The list's text is in the Zoom account's clock; the link carries the real start in
            // UTC. Checking the link is what makes a wrong time zone setting fail safe: it can make
            // a recording go unfound, but it can never put the wrong week's video on a session.
            var startedAt = ReadShareLinkStart(link);
            if (!StartsWithinSession(startedAt, day, startTime, _local, out string why))
                return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.TimeMismatch,
                    $"{group}: {why} Nothing was attached. Check {ZoomTimeZoneVariable} against the Zoom profile's time zone.")
                    with { Recording = picked, StartedAtUtc = startedAt };
            return ZoomRecordingLinkResult.Success(link, $"{group}: the recording's shareable link was copied.")
                with { Recording = picked, StartedAtUtc = startedAt };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[RECORDING] Failed while {step}: {ex.GetType().Name}.");
            return ZoomRecordingLinkResult.Fail(ZoomRecordingFailure.Failed, $"Zoom did not respond while {step}.");
        }
    }

    /// <summary>
    /// The picked recording's row in the list, its "Share recording" button, and the dialog's
    /// "Copy link". Works while Zoom is still processing the recording ("Users you shared with can
    /// watch the recording when it is ready"). Empty when any part of it is not there.
    /// </summary>
    private static async Task<string> CopyFromShareDialogAsync(IPage page, ZoomRecordingEntry picked, CancellationToken cancellationToken)
    {
        try
        {
            // The row is found by the recording's own meeting id in its link, never by position.
            string meetingId = new Uri(picked.DetailUrl).Query.Split('&', '?')
                .FirstOrDefault(p => p.StartsWith("meeting_id=", StringComparison.Ordinal))?["meeting_id=".Length..] ?? "";
            if (meetingId.Length == 0) return string.Empty;
            string decoded = Uri.UnescapeDataString(meetingId);
            var anchor = page.Locator($"a[href*='{meetingId}'], a[href*='{decoded}']").First;
            if (await anchor.CountAsync() == 0) return string.Empty;
            var row = anchor.Locator("xpath=ancestor::*[.//*[starts-with(@aria-label,'Share recording')]][1]");
            var share = row.Locator("[aria-label^='Share recording']").First;
            await share.ClickAsync(new() { Timeout = 10000 });
            var dialog = page.Locator("[role=dialog]").Filter(new() { HasText = "Copy link" }).Last;
            var copy = dialog.GetByText("Copy link", new() { Exact = true }).First;
            await copy.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await page.EvaluateAsync("() => navigator.clipboard.writeText('')");
            await copy.ClickAsync();
            string link = await WaitForClipboardLinkAsync(page, cancellationToken);
            try { await page.Keyboard.PressAsync("Escape"); } catch { }
            ConsoleLogger.Info(link.Length > 0
                ? "[RECORDING] The link was copied from the list's Share dialog."
                : "[RECORDING] The list's Share dialog was opened but nothing was copied.");
            return link;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Info($"[RECORDING] The list's Share dialog could not be used ({ex.GetType().Name}); trying the recording page.");
            return string.Empty;
        }
    }

    /// <summary>The list is searchable, which is faster and safer than paging through everything.</summary>
    private static async Task SearchAsync(IPage page, string group, CancellationToken cancellationToken)
    {
        var search = page.Locator("input[aria-label='Search by name or meeting ID']").First;
        try
        {
            await search.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await search.FillAsync(group);
            await search.PressAsync("Enter");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        catch (TimeoutException)
        {
            // No search box: the whole list is read instead, which still finds the recording.
            ConsoleLogger.Info("[RECORDING] The recordings page offered no search box; reading the list as it is.");
        }
        await page.WaitForTimeoutAsync(2500);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<IReadOnlyList<ZoomRecordingEntry>> ReadEntriesAsync(IPage page)
    {
        var links = await page.Locator("a[href*='/recording/detail']").AllAsync();
        var entries = new List<ZoomRecordingEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            string href = await link.GetAttributeAsync("href") ?? "";
            if (href.Length == 0) continue;
            string absolute = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? href
                : new Uri(new Uri(page.Url), href).AbsoluteUri;
            string text = (await link.InnerTextAsync()).Trim();
            // The same recording is linked from its thumbnail and its title; one entry is enough.
            if (!seen.Add(absolute + "|" + text)) continue;
            entries.Add(new ZoomRecordingEntry(text, absolute)
            {
                RecordedAt = ReadRecordedAt(text),
                Duration = ReadDuration(text)
            });
        }
        return entries;
    }

    /// <summary>
    /// Moves each listed time from the Zoom account's clock to this computer's. Without a Zoom zone
    /// the entries are returned untouched, which is how this behaved before the zone was known.
    /// </summary>
    public static IReadOnlyList<ZoomRecordingEntry> ToLocalTimes(
        IReadOnlyList<ZoomRecordingEntry> entries, TimeZoneInfo? zoomZone, TimeZoneInfo localZone)
    {
        if (zoomZone == null) return entries;
        return [.. entries.Select(entry => entry.RecordedAt is { } shown
            ? entry with { RecordedAt = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(shown, DateTimeKind.Unspecified), zoomZone, localZone) }
            : entry)];
    }

    /// <summary>
    /// The real start of a recording, from the "startTime" Zoom puts in every share link (Unix
    /// milliseconds, UTC). Null when the link carries none.
    /// </summary>
    public static DateTimeOffset? ReadShareLinkStart(string? shareUrl)
    {
        if (string.IsNullOrWhiteSpace(shareUrl)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(shareUrl, @"[?&]startTime=(\d{10,13})(?:&|$)");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out long value)) return null;
        return match.Groups[1].Value.Length >= 13
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    /// <summary>
    /// Whether a recording that really started at <paramref name="startedAtUtc"/> belongs to the
    /// requested session: the same local day and, when a time is given, inside that session's hours.
    /// A link with no start is not evidence either way and is let through, as before.
    /// </summary>
    public static bool StartsWithinSession(
        DateTimeOffset? startedAtUtc, DateOnly? day, TimeOnly? startTime, TimeZoneInfo localZone, out string reason)
    {
        reason = string.Empty;
        if (startedAtUtc is not { } started || (day == null && startTime == null)) return true;
        var local = TimeZoneInfo.ConvertTime(started, localZone).DateTime;
        string at = local.ToString("yyyy-MM-dd HH:mm");
        if (day is { } wantedDay && DateOnly.FromDateTime(local) != wantedDay)
        {
            reason = $"the recording Zoom handed over started {at} local time, not on {wantedDay:yyyy-MM-dd}.";
            return false;
        }
        if (startTime is { } wantedTime && !IsWithinSession(TimeOnly.FromDateTime(local), wantedTime))
        {
            reason = $"the recording Zoom handed over started {at} local time, outside the {wantedTime:HH':'mm} session.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The recording of this group's session. The name narrows it to the group - an exact name
    /// first, then one the group's name appears in - and the day and time pick the session out of
    /// the group's own history, because every week's recording carries the same name.
    ///
    /// When a day is given, a recording from another day is never returned, and a recording whose
    /// own date could not be read is not eligible either: the wrong week's video attached to a
    /// session is worse than no video at all.
    /// </summary>
    public static ZoomRecordingEntry? PickRecording(
        IReadOnlyList<ZoomRecordingEntry> entries, string group, DateOnly? day = null, TimeOnly? startTime = null)
    {
        string wanted = group.Trim();
        if (wanted.Length == 0) return null;

        ZoomRecordingEntry[] exact = [.. entries.Where(entry =>
            string.Equals(entry.Topic.Trim(), wanted, StringComparison.OrdinalIgnoreCase))];
        ZoomRecordingEntry[] loose = [.. entries.Where(entry =>
            entry.Topic.Contains(wanted, StringComparison.OrdinalIgnoreCase))];

        foreach (var named in new[] { exact, loose })
        {
            var candidates = named;
            if (day is { } wantedDay)
                candidates = [.. candidates.Where(entry =>
                    entry.RecordedAt.HasValue && DateOnly.FromDateTime(entry.RecordedAt.Value) == wantedDay)];
            if (candidates.Length == 0) continue;
            if (candidates.Length == 1) return candidates[0];

            // A class that is stopped and restarted leaves several recordings behind, so the ones
            // that fall inside this session's own hours are gathered and the longest of them wins:
            // the short leftover from a restart is never the one worth keeping.
            if (startTime is { } wantedTime)
            {
                var inSession = candidates
                    .Where(entry => entry.RecordedAt.HasValue && IsWithinSession(TimeOnly.FromDateTime(entry.RecordedAt.Value), wantedTime))
                    .ToArray();
                // Nothing inside the session's hours is a different class, not this one running long.
                if (inSession.Length == 0) continue;
                return Longest(inSession);
            }
            return Longest(candidates);
        }
        return null;
    }

    private static int MinutesApart(TimeOnly left, TimeOnly right) =>
        (int)Math.Abs((left.ToTimeSpan() - right.ToTimeSpan()).TotalMinutes);

    /// <summary>
    /// A class opens a little before its time and runs about three hours, so a recording that
    /// starts inside that stretch belongs to it and one outside it belongs to a different class.
    /// </summary>
    private static readonly TimeSpan OpensBefore = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RunsFor = TimeSpan.FromHours(3.5);

    private static bool IsWithinSession(TimeOnly recordedAt, TimeOnly sessionStart)
    {
        var offset = recordedAt.ToTimeSpan() - sessionStart.ToTimeSpan();
        return offset >= -OpensBefore && offset <= RunsFor;
    }

    /// <summary>
    /// The longest of them. A recording whose length could not be read counts as the shortest, so
    /// it is never preferred over one that is known to be long.
    /// </summary>
    private static ZoomRecordingEntry Longest(IReadOnlyList<ZoomRecordingEntry> entries) =>
        entries.OrderByDescending(entry => entry.Duration ?? TimeSpan.Zero)
               .ThenBy(entry => entry.RecordedAt ?? DateTime.MaxValue)
               .First();

    /// <summary>
    /// The length Zoom prints on the row, like "03:29:13". Three parts on purpose: the row also
    /// carries a clock time such as "08:58 AM", and two parts would read that as a duration.
    /// </summary>
    public static TimeSpan? ReadDuration(string rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return null;
        var match = DurationPattern.Match(rowText.ReplaceLineEndings(" "));
        return match.Success && TimeSpan.TryParse(match.Value, out var parsed) ? parsed : null;
    }

    private static readonly System.Text.RegularExpressions.Regex DurationPattern = new(
        @"\b\d{1,2}:[0-5]\d:[0-5]\d\b",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The date Zoom prints on the row, e.g. "Sep 1, 2026 08:58 AM". It sits in the middle of the
    /// row's whole text, so it is found rather than parsed from a known position.
    /// </summary>
    public static DateTime? ReadRecordedAt(string rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText)) return null;
        var match = RecordedAtPattern.Match(rowText.ReplaceLineEndings(" "));
        if (!match.Success) return null;
        return DateTime.TryParseExact(
            match.Value.Replace("  ", " ").Trim(),
            ["MMM d, yyyy hh:mm tt", "MMM d, yyyy h:mm tt", "MMMM d, yyyy hh:mm tt", "MMMM d, yyyy h:mm tt"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed) ? parsed : null;
    }

    private static readonly System.Text.RegularExpressions.Regex RecordedAtPattern = new(
        @"[A-Z][a-z]{2,8}\s+\d{1,2},\s*\d{4}\s+\d{1,2}:\d{2}\s*(AM|PM)",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The copy is not instant; the clipboard is read until it holds a Zoom share link.</summary>
    private static async Task<string> WaitForClipboardLinkAsync(IPage page, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string clipboard;
            try { clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"); }
            catch { clipboard = ""; }
            if (IsShareLink(clipboard)) return clipboard.Trim();
            await page.WaitForTimeoutAsync(500);
        }
        return string.Empty;
    }

    /// <summary>A Zoom share link and nothing else, so a stale clipboard is never passed on.</summary>
    public static bool IsShareLink(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.EndsWith("zoom.us", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("/rec/", StringComparison.OrdinalIgnoreCase);
}
