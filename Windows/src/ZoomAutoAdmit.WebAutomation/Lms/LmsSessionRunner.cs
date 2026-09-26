using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>Why a dashboard step did not happen, for callers that answer differently to each.</summary>
public enum LmsFailure
{
    None,
    /// <summary>No dashboard sign-in is saved on this computer.</summary>
    NotSignedIn,
    /// <summary>The dashboard lists no session for this group on that day (and time).</summary>
    SessionNotFound,
    /// <summary>The session is there but not finished, so it does not offer a record link yet.</summary>
    SessionNotFinished,
    /// <summary>The link handed in is not a Zoom recording link.</summary>
    InvalidLink,
    /// <summary>Anything else: a page that did not load, a save that did not take.</summary>
    Failed,
}

/// <summary>What one attempt to start a session on the LMS did.</summary>
public sealed record LmsRunResult(bool IsSuccess, string Message)
{
    public LmsFailure FailureKind { get; init; }
    /// <summary>The session already carried a recording link, and it was left as it was.</summary>
    public bool AlreadyExists { get; init; }

    public static LmsRunResult Success(string message) => new(true, message);
    public static LmsRunResult Failure(string message) => new(false, message) { FailureKind = LmsFailure.Failed };
    public static LmsRunResult Fail(LmsFailure failure, string message) => new(false, message) { FailureKind = failure };
}

/// <summary>
/// What an attendance upload did, and the decision it was going to write. The plan comes back on a
/// dry run and on a real one, so what was intended can be read next to what happened.
/// </summary>
public sealed record LmsAttendanceResult(bool IsSuccess, string Message, LmsAttendancePlan? Plan)
{
    /// <summary>Why it failed, in the same terms a run failure uses.
    ///
    /// This is what decides whether the job is tried again. Without it every attendance failure was
    /// the same failure: a session the dashboard has not finished yet, which will be ready in five
    /// minutes, could not be told from one it does not list at all, which never will be.</summary>
    public LmsFailure FailureKind { get; init; }
}

/// <summary>
/// Presses "Run Session" on the DEPI dashboard for the class that is starting.
///
/// The dashboard is the record of what happened, so a live meeting whose session was never
/// started reads as a class that never ran. Doing it here means one action opens the meeting and
/// marks it running, with nobody having to remember the second half.
///
/// It signs in with the account kept in Windows Credential Manager, filters the session list to
/// today, opens the one row that matches the group, and presses the button on that session's own
/// page. It never presses anything on a row it could not match.
/// </summary>
public sealed class LmsSessionRunner(ILmsCredentialStore credentials, ZoomProfileManager? profiles = null)
{
    private const string LoginUrl = "https://dashboard.depi.eyouthbusiness.com/auth/login";
    private const string SessionsUrl = "https://dashboard.depi.eyouthbusiness.com/group_admin/sessions";
    /// <summary>
    /// The browser profile the dashboard is driven with: the chosen LMS account's own, so switching
    /// accounts never reuses another account's kept sign-in.
    /// </summary>
    private string ProfileName => credentials.Profile;
    /// <summary>The active account's browser profile, for anything that must not share it (the lock).</summary>
    public static string DashboardProfile => new LmsCredentialStore().Profile;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    // Signing in makes Chrome offer to save the password. That bubble floats over the page and
    // eats the clicks meant for the filter and the table, so the offer is turned off outright.
    private static readonly string[] ChromeSwitches =
    [
        "--disable-features=PasswordLeakDetection,PasswordLeakDetectionEnabled,AutofillServerCommunication",
        "--disable-save-password-bubble",
        "--disable-password-generation",
        "--password-store=basic",
        "--no-default-browser-check",
        "--no-first-run"
    ];

    private readonly ZoomProfileManager _profiles = profiles ?? new ZoomProfileManager();

    /// <param name="group">The group exactly as the dashboard shows it, for example CAI5_AIS4_S7.</param>
    /// <param name="startTime">
    /// The time this class is scheduled for. A group often has more than one session in a day, and
    /// the time is what tells them apart; without it, only a single listed session can be started.
    /// </param>
    /// <param name="day">Which day's sessions to look at. Today unless a test says otherwise.</param>
    /// <param name="headed">Show the browser. Useful the first time, or while checking what it did.</param>
    /// <param name="dryRun">
    /// Do everything except the final press. It reports the session it would have started, which
    /// is how the steps get checked without changing anything on a real class.
    /// </param>
    public async Task<LmsRunResult> RunAsync(
        string group,
        TimeOnly? startTime = null,
        DateOnly? day = null,
        bool headed = false,
        bool dryRun = false,
        bool keepBrowserOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var account = credentials.Read();
        if (account == null)
            return LmsRunResult.Failure("No LMS sign-in is saved. Add it in the app before starting a session.");

        DateOnly date = day ?? DateOnly.FromDateTime(DateTime.Now);
        var profile = _profiles.GetOrCreate(ProfileName);
        var plan = new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches };

        // Left open on purpose when asked: the session page is what shows the class is running,
        // and closing it the moment the button is pressed leaves nothing to look at.
        var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        await using var closing = keepBrowserOpen ? null : session;
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

        // Named so a failure says which step ran out of time instead of only "TimeoutException".
        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, date, startTime, cancellationToken);
            if (!opened.IsOpen)
                return LmsRunResult.Fail(opened.Failure, $"{opened.Reason} Nothing was pressed.");

            step = "pressing Run Session";
            var run = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Run Session\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
            // The session page draws its actions after the row is opened, so the button is waited
            // for. Looking the instant the click returns found an empty page and reported the
            // button as missing on a session that was perfectly startable.
            bool present = true;
            try { await run.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
            catch (TimeoutException) { present = false; }
            if (!present)
            {
                // Already running or already finished: the button is gone, and that is not a failure.
                string state = await ReadStatusAsync(page);
                return LmsRunResult.Failure(
                    $"The session page for {group} has no Run Session button{(state.Length > 0 ? $"; it reads \"{state}\"" : string.Empty)}.");
            }

            if (dryRun)
                return LmsRunResult.Success(
                    $"{group}: the session page is open and offers Run Session. Nothing was pressed.");

            await run.ClickAsync();
            // The dashboard answers with a toast. Catch it before it fades: it is the site's own
            // word on whether the press worked, which is worth having in the log next to ours.
            string toast = await ReadToastAsync(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            if (toast.Length > 0) ConsoleLogger.Success($"[LMS] The dashboard said: {toast}");
            else ConsoleLogger.Info("[LMS] The dashboard showed no message; the status is checked instead.");
            ConsoleLogger.Success($"[LMS] Run Session pressed for {group} on {date:yyyy-MM-dd}.");

            // A press that changed nothing is not a success: the button goes away once the
            // session is running, so it still being there means the dashboard refused it.
            bool stillOffered = await run.IsVisibleAsync().ConfigureAwait(false);
            string finalState = await ReadStatusAsync(page);
            if (stillOffered)
                return LmsRunResult.Failure(
                    $"{group}: Run Session was pressed but the session still offers it" +
                    $"{(finalState.Length > 0 ? $" and reads \"{finalState}\"" : string.Empty)}.");
            return LmsRunResult.Success(
                $"{group}: the session is now running on the dashboard" +
                $"{(finalState.Length > 0 ? $" ({finalState})" : string.Empty)}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The step name is safe to show; the exception text is not, because it can quote a
            // value that was typed into a field.
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return LmsRunResult.Failure($"{WhyItFailed(ex, step)}");
        }
    }

    /// <summary>Marks the matched LMS session complete. Dry run opens and verifies the exact action only.</summary>
    public async Task<LmsRunResult> CompleteSessionAsync(
        string group, TimeOnly? startTime = null, DateOnly? day = null, bool headed = false,
        bool dryRun = true, bool keepBrowserOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var account = credentials.Read();
        if (account == null) return LmsRunResult.Failure("No LMS sign-in is saved. Add it before completing a session.");
        DateOnly date = day ?? DateOnly.FromDateTime(DateTime.Now);
        var profile = _profiles.GetOrCreate(ProfileName);
        var browser = await new ZoomBrowserLauncher().LaunchAsync(
            new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = keepBrowserOpen ? null : browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, date, startTime, cancellationToken);
            if (!opened.IsOpen) return LmsRunResult.Fail(opened.Failure, $"{opened.Reason} Nothing was changed.");
            // The session page draws its actions a moment after it opens: the status is read only
            // once the button has had the time to appear, or an empty page reads as "no status".
            // Only "Complete Session" is ever matched; "Cancel Session" sits right under it.
            step = "finding Complete Session";
            var complete = page.GetByRole(AriaRole.Button, new()
            { NameRegex = new(@"^\s*(Complete|Finish)\s+Session\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
            bool offered = true;
            try { await complete.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
            catch (TimeoutException) { offered = false; }
            string state = await ReadStatusAsync(page);
            if (!offered)
            {
                if (state.Contains("finished", StringComparison.OrdinalIgnoreCase))
                    return LmsRunResult.Success($"{group}: the LMS session is already finished.");
                ConsoleLogger.Info($"[LMS] The session page offers: {await ListActionsAsync(page)}");
                return LmsRunResult.Failure($"The session page for {group} has no Complete Session action; it reads \"{state}\".");
            }
            if (dryRun) return LmsRunResult.Success($"{group}: Complete Session is available ({state}). Nothing was pressed.");

            step = "pressing Complete Session";
            await complete.ClickAsync();
            // A confirmation box, if the dashboard asks: its own confirm button, never a cancel/close.
            var dialog = page.Locator("[role='alertdialog'], [data-slot='dialog-content'][role='dialog'], [role='dialog']").Last;
            try
            {
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                var confirm = dialog.GetByRole(AriaRole.Button, new()
                { NameRegex = new(@"^\s*(Complete(\s+Session)?|Finish(\s+Session)?|Confirm|Yes|Continue)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).Last;
                if (await confirm.CountAsync() > 0 && await confirm.IsVisibleAsync())
                {
                    step = "confirming Complete Session";
                    await confirm.ClickAsync();
                }
            }
            catch (TimeoutException) { }             // no confirmation asked
            string toast = await ReadToastAsync(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            if (toast.Length > 0) ConsoleLogger.Success($"[LMS] The dashboard said: {toast}");
            state = await ReadStatusAsync(page);
            bool stillOffered = await complete.IsVisibleAsync().ConfigureAwait(false);
            return state.Contains("finished", StringComparison.OrdinalIgnoreCase) || !stillOffered
                ? LmsRunResult.Success($"{group}: the LMS session is complete{(state.Length > 0 ? $" ({state})" : string.Empty)}.")
                : LmsRunResult.Failure($"{group}: Complete Session was pressed but the session still offers it; it reads \"{state}\".");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return LmsRunResult.Failure($"{WhyItFailed(ex, step)}");
        }
    }

    /// <summary>
    /// Puts the recording's link on the session, the way it is done by hand: open the session,
    /// press "Add Record Link", paste, Save. The dashboard only offers that button once the
    /// session is finished, so a session that is not finished yet is reported rather than forced.
    /// </summary>
    /// <param name="recordLink">
    /// The recording's link, written exactly as given: a Zoom share link copied from My Recordings,
    /// or a Google Drive file link from the recordings sheet. Anything else is refused.
    /// </param>
    /// <param name="dryRun">Open everything and report what it would paste, without saving.</param>
    public async Task<LmsRunResult> AttachRecordLinkAsync(
        string group,
        string recordLink,
        TimeOnly? startTime = null,
        DateOnly? day = null,
        bool headed = false,
        bool dryRun = false,
        bool keepBrowserOpen = false,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (!Recordings.RecordingLinks.IsAttachable(recordLink))
            return LmsRunResult.Fail(LmsFailure.InvalidLink,
                "That is not a Zoom recording link or a Google Drive file link, so nothing was saved.");
        var account = credentials.Read();
        if (account == null)
            return LmsRunResult.Fail(LmsFailure.NotSignedIn, "No LMS sign-in is saved. Add it in the app before attaching a recording.");

        DateOnly date = day ?? DateOnly.FromDateTime(DateTime.Now);
        var profile = _profiles.GetOrCreate(ProfileName);
        var plan = new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches };
        var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        await using var closing = keepBrowserOpen ? null : session;
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, date, startTime, cancellationToken);
            if (!opened.IsOpen)
                return LmsRunResult.Fail(opened.NotListed ? LmsFailure.SessionNotFound : LmsFailure.Failed,
                    $"{opened.Reason} Nothing was saved.");

            step = "looking for Add Record Link";
            // The dashboard calls it "Add Record Link" while the session has none and "Edit Record
            // Link" once it does, so both are looked for and the wording says which case this is.
            var add = page.GetByRole(AriaRole.Button, new()
            { NameRegex = new(@"(Add|Edit)\s+Record\s+Link", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
            bool offered = true;
            try { await add.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
            catch (TimeoutException) { offered = false; }
            if (!offered)
            {
                string state = await ReadStatusAsync(page);
                // What the page does offer, so a renamed or re-shaped button is visible in the log
                // instead of leaving "not found" as the only thing anyone can see.
                ConsoleLogger.Info($"[LMS] The session page offers: {await ListActionsAsync(page)}");
                return LmsRunResult.Fail(LmsFailure.SessionNotFinished,
                    $"The session page for {group} offers no Add Record Link" +
                    $"{(state.Length > 0 ? $"; it reads \"{state}\"" : string.Empty)}. " +
                    "The dashboard only offers it once the session is finished.");
            }

            // A session that already carries a link is left alone unless replacing it was asked
            // for: quietly writing over a link somebody put there by hand is not this step's job.
            string buttonText = (await add.InnerTextAsync()).Trim();
            bool alreadyHasLink = buttonText.Contains("Edit", StringComparison.OrdinalIgnoreCase);
            bool newIsDrive = recordLink.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase);
            // Zoom first, Drive later: a Drive link replaces a Zoom one by itself. Anything else
            // already on the session (a Drive link, a link put there by hand) is left alone unless
            // replacing was asked for.
            if (alreadyHasLink && !replaceExisting && !newIsDrive)
                return LmsRunResult.Success(
                    $"{group}: the session already has a recording link, so it was left as it is.")
                    with { AlreadyExists = true };

            step = "opening the record link box";
            await add.ClickAsync();
            var field = page.Locator("input[placeholder='Enter session record link']").First;
            try { await field.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
            catch (TimeoutException)
            {
                return LmsRunResult.Failure($"The record link box did not open for {group}, so nothing was saved.");
            }
            if (alreadyHasLink && !replaceExisting)
            {
                string current = (await field.InputValueAsync()).Trim();
                if (!current.Contains("zoom.us", StringComparison.OrdinalIgnoreCase) ||
                    current.Equals(recordLink.Trim(), StringComparison.Ordinal))
                {
                    await page.Keyboard.PressAsync("Escape");       // closed unsaved
                    return LmsRunResult.Success($"{group}: the session already has a recording link, so it was left as it is.")
                        with { AlreadyExists = true };
                }
                ConsoleLogger.Info($"[LMS] {group}: the session has the Zoom link; it is replaced by the Drive link.");
            }
            await field.FillAsync(recordLink);

            if (dryRun)
                return LmsRunResult.Success(
                    $"{group}: the record link box is open and holds the recording's link. Nothing was saved.");

            step = "saving the record link";
            await page.GetByRole(AriaRole.Button, new()
            { NameRegex = new(@"^\s*Save\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First.ClickAsync();
            string toast = await ReadToastAsync(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            if (toast.Length > 0) ConsoleLogger.Success($"[LMS] The dashboard said: {toast}");

            // The box closing is the dashboard accepting it; a box still holding the link is not.
            bool stillOpen = await field.IsVisibleAsync().ConfigureAwait(false);
            if (stillOpen)
                return LmsRunResult.Failure(
                    $"{group}: Save was pressed but the record link box is still open, so it was not saved.");
            return LmsRunResult.Success($"{group}: the recording link was saved on the session.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return LmsRunResult.Failure($"{WhyItFailed(ex, step)}");
        }
    }

    /// <summary>
    /// Takes the session's attendance: opens "Take Session Attendance", ticks Joined or Not-joined
    /// for every student the dashboard lists, and presses the button that writes it.
    ///
    /// Every row is decided before anything is ticked, and a dry run reports that decision without
    /// touching the dialog - which is how an upload gets looked at before it is a real one. If any
    /// row cannot be read or ticked, nothing is submitted at all: a half-filled attendance sheet is
    /// worse than none, because it reads as a class where half the students were absent.
    /// </summary>
    /// <param name="present">The students the app saw in the meeting, by their roster names.</param>
    public async Task<LmsAttendanceResult> TakeAttendanceAsync(
        string group,
        IReadOnlyCollection<string> present,
        TimeOnly? startTime = null,
        DateOnly? day = null,
        bool headed = false,
        bool dryRun = true,
        bool keepBrowserOpen = false,
        bool everyone = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(present);
        var account = credentials.Read();
        if (account == null)
            return new(false, "No LMS sign-in is saved. Add it in the app before taking attendance.", null)
                { FailureKind = LmsFailure.NotSignedIn };

        DateOnly date = day ?? DateOnly.FromDateTime(DateTime.Now);
        var profile = _profiles.GetOrCreate(ProfileName);
        var plan = new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches };
        var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        await using var closing = keepBrowserOpen ? null : session;
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, date, startTime, cancellationToken);
            if (!opened.IsOpen)
                return new(false, $"{opened.Reason} No attendance was taken.", null) { FailureKind = opened.Failure };

            step = "opening Take Session Attendance";
            var take = page.GetByRole(AriaRole.Button, new()
            { NameRegex = new(@"Take\s+Session\s+Attendance", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
            bool offered = true;
            try { await take.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
            catch (TimeoutException) { offered = false; }
            if (!offered)
            {
                string state = await ReadStatusAsync(page);
                ConsoleLogger.Info($"[LMS] The session page offers: {await ListActionsAsync(page)}");
                return new(false,
                    $"The session page for {group} offers no Take Session Attendance" +
                    $"{(state.Length > 0 ? $"; it reads \"{state}\"" : string.Empty)}. " +
                    "Attendance may already have been taken.", null);
            }
            await take.ClickAsync();

            step = "reading the attendance list";
            var dialog = page.Locator("[data-slot='dialog-content'][role='dialog']").First;
            try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
            catch (TimeoutException) { return new(false, $"The attendance list did not open for {group}.", null); }

            var rows = await ReadAttendanceRowsAsync(dialog);
            if (rows.Count == 0)
            {
                // What the dialog does hold, so a list that could not be read can be looked at.
                string shown = (await dialog.InnerTextAsync()).ReplaceLineEndings(" | ");
                ConsoleLogger.Info($"[LMS] The dialog reads: {(shown.Length > 600 ? shown[..600] + "..." : shown)}");
                return new(false, $"The attendance list for {group} opened but listed no students.", null);
            }

            var decided = LmsAttendancePlan.Build([.. rows.Select(row => row.StudentName)], present, everyone);
            ConsoleLogger.Info($"[LMS] {group}: {decided.Summary}.");
            if (dryRun)
                return new(true, $"{group}: {decided.Summary}. Nothing was ticked.", decided);

            step = "ticking the attendance list";
            for (int index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[index];
                var box = decided.Marks[index].Joined ? row.Joined : row.NotJoined;
                if (box == null)
                    return new(false,
                        $"{group}: {row.StudentName} has no box to tick, so nothing was submitted.", decided);
                await box.ScrollIntoViewIfNeededAsync();
                await box.ClickAsync();
            }

            step = "submitting the attendance";
            await page.GetByRole(AriaRole.Button, new()
            { NameRegex = new(@"^\s*Take\s+Attendance\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })
                .Last.ClickAsync();
            string toast = await ReadToastAsync(page);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.WaitForTimeoutAsync(1500);
            if (toast.Length > 0) ConsoleLogger.Success($"[LMS] The dashboard said: {toast}");

            // The dialog closing is the dashboard accepting it; one still open is not.
            bool stillOpen = await dialog.IsVisibleAsync().ConfigureAwait(false);
            if (stillOpen)
                return new(false,
                    $"{group}: attendance was filled in but the dashboard did not accept it.", decided);
            return new(true, $"{group}: attendance taken - {decided.Summary}.", decided);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return new(false, $"{WhyItFailed(ex, step)}", null);
        }
    }

    /// <summary>
    /// Corrects an attendance that was already taken - the late joiner who turned up after the
    /// sheet was filled in. It opens "View details", compares every row with who is present now,
    /// and flips only the rows that disagree. Each flip saves itself, so the page is reloaded
    /// afterwards and read again: what the dashboard shows on a fresh load is the proof.
    /// </summary>
    public async Task<LmsAttendanceResult> CorrectAttendanceAsync(
        string group,
        IReadOnlyCollection<string> present,
        TimeOnly? startTime = null,
        DateOnly? day = null,
        bool headed = false,
        bool dryRun = true,
        bool keepBrowserOpen = false,
        bool everyone = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(present);
        var account = credentials.Read();
        if (account == null)
            return new(false, "No LMS sign-in is saved. Add it in the app before correcting attendance.", null);

        DateOnly date = day ?? DateOnly.FromDateTime(DateTime.Now);
        var profile = _profiles.GetOrCreate(ProfileName);
        var plan = new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches };
        var session = await new ZoomBrowserLauncher().LaunchAsync(plan, cancellationToken);
        await using var closing = keepBrowserOpen ? null : session;
        var page = session.Context.Pages.Count > 0
            ? session.Context.Pages[0]
            : await session.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);

        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, date, startTime, cancellationToken);
            if (!opened.IsOpen)
                return new(false, $"{opened.Reason} Nothing was changed.", null) { FailureKind = opened.Failure };

            step = "opening the attendance details";
            var (rows, states) = await OpenDetailsAsync(page, group);
            if (rows == null) return new(false, states!, null);

            var decided = LmsAttendancePlan.Build([.. rows.Select(row => row.StudentName)], present, everyone);
            var changes = new List<(int Index, string Name, bool To)>();
            for (int index = 0; index < rows.Count; index++)
            {
                bool now = await IsOnAsync(rows[index].Joined);
                bool wanted = decided.Marks[index].Joined;
                if (now != wanted) changes.Add((index, rows[index].StudentName, wanted));
            }

            if (changes.Count == 0)
                return new(true, $"{group}: the dashboard already matches - nothing to change.", decided);
            string listed = string.Join(", ", changes.Select(change =>
                $"{change.Name} → {(change.To ? "Joined" : "Not-joined")}"));
            ConsoleLogger.Info($"[LMS] {group}: {changes.Count} row(s) differ - {listed}");
            if (dryRun)
                return new(true, $"{group}: {changes.Count} row(s) would change ({listed}). Nothing was touched.", decided);

            step = "flipping the rows that differ";
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toggle = rows[change.Index].Joined;
                if (toggle == null)
                    return new(false, $"{group}: {change.Name} has no switch to flip.", decided);
                await toggle.ScrollIntoViewIfNeededAsync();
                await toggle.ClickAsync();
                // Each flip saves on its own, so they are given a moment rather than fired at once.
                await page.WaitForTimeoutAsync(600);
            }

            step = "reading the dashboard back";
            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
            var (after, reason) = await OpenDetailsAsync(page, group);
            if (after == null) return new(false, $"{group}: the rows were flipped but {reason}", decided);
            // Checked by name, not by position: a reloaded list is free to come back in a
            // different order, and comparing row 3 with row 3 would then accuse the wrong student.
            var wantedByName = decided.Marks.ToDictionary(mark => mark.StudentName, mark => mark.Joined, StringComparer.OrdinalIgnoreCase);
            var stubborn = new List<string>();
            foreach (var row in after)
                if (wantedByName.TryGetValue(row.StudentName, out bool wanted) && await IsOnAsync(row.Joined) != wanted)
                    stubborn.Add(row.StudentName);
            if (stubborn.Count > 0)
                return new(false,
                    $"{group}: after reloading, {stubborn.Count} row(s) still disagree ({string.Join(", ", stubborn)}).",
                    decided);
            return new(true, $"{group}: {changes.Count} row(s) changed and confirmed after a reload ({listed}).", decided);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return new(false, $"{WhyItFailed(ex, step)}", null);
        }
    }

    /// <summary>Opens "View details" and reads its rows, or says why it could not.</summary>
    private static async Task<(IReadOnlyList<AttendanceRow>? Rows, string? Reason)> OpenDetailsAsync(IPage page, string group)
    {
        var details = page.GetByRole(AriaRole.Button, new()
        { NameRegex = new(@"^\s*View\s+details\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        try { await details.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (TimeoutException)
        {
            return (null, $"the session page for {group} offers no View details, so its attendance was never taken.");
        }
        await details.ClickAsync();
        var dialog = page.Locator("[data-slot='dialog-content'][role='dialog']").First;
        try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (TimeoutException) { return (null, $"the attendance details did not open for {group}."); }
        var rows = await ReadAttendanceRowsAsync(dialog);
        return rows.Count == 0
            ? (null, $"the attendance details for {group} listed no students.")
            : (rows, null);
    }

    /// <summary>A switch reports itself; an unreadable one is treated as off rather than guessed on.</summary>
    private static async Task<bool> IsOnAsync(ILocator? toggle)
    {
        if (toggle == null) return false;
        string? state = await toggle.GetAttributeAsync("aria-checked");
        return string.Equals(state, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One student's row in the Take Attendance dialog: the name and its two boxes.</summary>
    private sealed record AttendanceRow(string StudentName, ILocator? Joined, ILocator? NotJoined);

    /// <summary>
    /// The dialog is a two-column grid of a name and its controls, so the rows are read in order
    /// and each name is paired with the boxes that follow it. Reading the whole list first means
    /// the decision is made against what is actually on screen, not against what was expected.
    /// </summary>
    private static async Task<IReadOnlyList<AttendanceRow>> ReadAttendanceRowsAsync(ILocator dialog)
    {
        // The dialog opens saying "Loading..." and fetches its students afterwards. Reading it the
        // moment it appears found an empty list and would have reported a class with no students.
        try
        {
            await dialog.Locator("button[role='checkbox'], button[role='switch'], input[type='checkbox']")
                .First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 25000 });
        }
        catch (TimeoutException) { return []; }

        // The two dialogs are not built the same - one has a pair of checkboxes per student, the
        // other a single switch - so the controls are found first and each is asked which name it
        // sits next to. That reads both without knowing either one's class names.
        string json = await dialog.EvaluateAsync<string>(@"root => JSON.stringify((() => {
            const controls = Array.from(root.querySelectorAll(""button[role='checkbox'], button[role='switch'], input[type='checkbox']""));
            return controls.map((control, index) => {
                let node = control, name = '';
                while (node && node !== root && !name) {
                    let previous = node.previousElementSibling;
                    while (previous && !name) {
                        const text = (previous.innerText || previous.textContent || '').trim();
                        const holdsAControl = previous.querySelector(""button[role='checkbox'], button[role='switch'], input[type='checkbox']"");
                        if (text && !holdsAControl) name = text;
                        previous = previous.previousElementSibling;
                    }
                    node = node.parentElement;
                }
                return { name: name.replace(/\s+/g, ' ').trim(), index };
            });
        })());");
        var pairs = System.Text.Json.JsonSerializer.Deserialize<NameAtIndex[]>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];

        // A heading such as ""Student Name"" sits above the first row, never beside a control, so
        // anything that repeats for consecutive controls is one student with more than one box.
        var rows = new List<AttendanceRow>();
        var controlsLocator = dialog.Locator("button[role='checkbox'], button[role='switch'], input[type='checkbox']");
        for (int index = 0; index < pairs.Length;)
        {
            string name = pairs[index].Name;
            int span = 1;
            while (index + span < pairs.Length && string.Equals(pairs[index + span].Name, name, StringComparison.Ordinal)) span++;
            if (name.Length > 0)
                rows.Add(new AttendanceRow(
                    name,
                    controlsLocator.Nth(pairs[index].Index),
                    span > 1 ? controlsLocator.Nth(pairs[index + 1].Index) : controlsLocator.Nth(pairs[index].Index)));
            index += span;
        }
        return rows;
    }

    private sealed class NameAtIndex
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
    }

    /// <summary>One session as the dashboard shows it, read without changing anything.</summary>
    public sealed record LmsSessionInfo(
        string Group, DateOnly? Date, TimeOnly? Start, string Title, string ListStatus, string? PageUrl,
        string PageStatus, string RecordLink, string LinkKind, bool? AttendanceTaken, IReadOnlyList<string> Actions)
    {
        /// <summary>The titles under Session Attachments, as a full read found them (null: not read).</summary>
        public IReadOnlyList<string>? Attachments { get; init; }
        /// <summary>The session has its assignment ("Edit Assignment"); null: not read.</summary>
        public bool? HasAssignment { get; init; }
        /// <summary>When the session's own page (attachments, assignment) was last read.</summary>
        public DateTimeOffset? DetailsReadAt { get; init; }
        /// <summary>"Physical" or "Online", as the list's type column says; empty when not read.</summary>
        public string Mode { get; init; } = "";
        /// <summary>The list's focus column: "Technical", "Freelancing", "Coaching"...; empty when not read.</summary>
        public string Focus { get; init; } = "";
        /// <summary>Held in a room, not on Zoom: nothing is opened, admitted or snapshotted for it.</summary>
        public bool IsPhysical => Mode.Equals("Physical", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex ModeCell =
        new(@"^(Physical|Online|Offline|Hybrid|Live)(\s+Session)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The session's type and focus from its list row. The row reads, one cell a tab apart,
    /// "... CAI5_AIS4_S8 second yth CAI Physical Technical Finished Location" (read 2026-09-26):
    /// the type is its own cell, and the focus is the cell after it. "Offline" is taken as Physical.
    /// </summary>
    internal static (string Mode, string Focus) ModeOfRow(string rowText)
    {
        var cells = rowText.Split(SummaryBreaks, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        for (int i = 0; i < cells.Length; i++)
        {
            var match = ModeCell.Match(cells[i]);
            if (!match.Success) continue;
            string mode = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "physical" or "offline" => "Physical",
                "online" or "live" => "Online",
                _ => "Hybrid",
            };
            string focus = i + 1 < cells.Length &&
                           !System.Text.RegularExpressions.Regex.IsMatch(cells[i + 1], @"^(pending|running|finished|cancelled|completed|location)$",
                               System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? cells[i + 1] : "";
            return (mode, focus);
        }
        return ("", "");
    }

    private static readonly System.Text.RegularExpressions.Regex GroupCode =
        new(@"\b[A-Z]{2,6}\d*_[A-Z0-9]+_[A-Z0-9]+\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads every session listed between two days - its group, time and status - and, when asked,
    /// opens each one to read its record link (Add/Edit Record Link, the box read and closed
    /// unsaved) and whether its attendance was taken. Nothing is pressed that changes a session.
    /// </summary>
    public async Task<IReadOnlyList<LmsSessionInfo>> SurveyAsync(
        DateOnly from, DateOnly to, IReadOnlyCollection<string>? groups = null, bool openEach = true,
        bool headed = false, CancellationToken cancellationToken = default,
        Func<string, DateOnly?, TimeOnly?, bool>? openWhen = null)
    {
        var account = credentials.Read() ?? throw new InvalidOperationException("No LMS sign-in is saved.");
        // Its own profile: a look at the list must never collide with the automation pressing a button.
        var profile = _profiles.GetOrCreate(ProfileName + "-view");
        var browser = await new ZoomBrowserLauncher().LaunchAsync(
            new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        await SignInAsync(page, account, cancellationToken);

        // 1. the list: one day at a time, every page of it (an admin sees 40+ sessions a day, 10 a page)
        var listed = new List<(string Group, DateOnly? Date, TimeOnly? Start, string Title, string Status, DateOnly Day, int ListPage, int Row, string Mode, string Focus)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await OpenListPageAsync(page, day, day, 1)) continue;          // nothing that day
            int pages = await ListPageCountAsync(page);
            for (int number = 1; number <= pages; number++)
            {
                if (number > 1 && !await OpenListPageAsync(page, day, day, number)) break;
                int index = 0;
                foreach (var row in await page.Locator("table tbody tr").AllAsync())
                {
                    string text;
                    try { text = await RowTextAsync(row); }
                    catch (PlaywrightException) { index++; continue; }
                    // The page has carried a second, hidden copy of the list since 2026-09-26: its rows
                    // cannot be opened, and read whole they came out glued into one word.
                    bool shown;
                    try { shown = await row.IsVisibleAsync(); } catch (PlaywrightException) { shown = false; }
                    if (!shown) { index++; continue; }
                    string key = text.Trim();
                    if (key.Length == 0 || !seen.Add(key)) { index++; continue; }
                    // A row without a group code is not a class of any group: it is left out rather than
                    // shown with the whole row for a name.
                    if (GroupCode.Match(text) is not { Success: true } g)
                    {
                        ConsoleLogger.Info($"[LMS] A listed row names no group, left out: {Summarise(text)}");
                        index++; continue;
                    }
                    string group = g.Value;
                    if (groups != null && groups.Count > 0 && !groups.Contains(group, StringComparer.OrdinalIgnoreCase)) { index++; continue; }
                    // One class once, however many copies of it the page lists.
                    if (!seen.Add($"{group}|{ReadRowTime(text)}|{System.Text.RegularExpressions.Regex.Match(text, @"\d{4}-\d{2}-\d{2}").Value}")) { index++; continue; }
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(text, @"\d{4}-\d{2}-\d{2}");
                    DateOnly? date = dateMatch.Success && DateOnly.TryParse(dateMatch.Value, out var d) ? d : null;
                    var status = System.Text.RegularExpressions.Regex.Match(text, @"\b(pending|running|finished|cancelled|completed)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    string title = TitleOfRow(text);
                    var (mode, focus) = ModeOfRow(text);
                    listed.Add((group, date, ReadRowTime(text), title, status.Success ? status.Value.ToLowerInvariant() : "", day, number, index, mode, focus));
                    index++;
                }
            }
        }

        // 2. each session's own page
        var result = new List<LmsSessionInfo>();
        foreach (var item in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A quick read still opens the sessions it is asked to (the ones this app put material on),
            // so something removed on the LMS shows at once without a full check.
            if (!openEach && openWhen?.Invoke(item.Group, item.Date, item.Start) != true)
            {
                result.Add(new(item.Group, item.Date, item.Start, item.Title, item.Status, null, "", "", "unknown", null, [])
                { Mode = item.Mode, Focus = item.Focus });
                continue;
            }
            string pageUrl = "", pageStatus = "", link = "", kind = "unknown";
            bool? attendance = null, hasAssignment = null;
            IReadOnlyList<string>? attachments = null;
            DateTimeOffset? detailsAt = null;
            var actions = new List<string>();
            try
            {
                bool onList = await OpenListPageAsync(page, item.Day, item.Day, item.ListPage);
                var row = page.Locator("table tbody tr").Nth(item.Row);
                // The row must still be this group's before it is opened: a list that moved must not
                // have another group's session read in its place.
                onList = onList && await row.CountAsync() > 0 &&
                         (!GroupCode.IsMatch(item.Group) || RowHasGroup(await RowTextAsync(row), item.Group));
                var open = row.Locator("td:first-child a, td:first-child button").First;
                if (onList && await open.CountAsync() > 0 && await OpenSessionPageAsync(page, open, item.Group))
                {
                    pageUrl = page.Url;
                    // Wait for the actions to be drawn before reading anything.
                    var anyAction = page.GetByRole(AriaRole.Button, new()
                    { NameRegex = new(@"Record\s+Link|View\s+details|Run\s+Session|Take\s+Session\s+Attendance|Complete\s+Session", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                    try { await anyAction.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); } catch (TimeoutException) { }
                    await page.WaitForTimeoutAsync(1500);             // the rest of the actions draw after the first
                    pageStatus = await ReadStatusAsync(page);
                    foreach (var name in (await ListActionsAsync(page)).Split(" | "))
                        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"Session|Record|Attendance|details", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) actions.Add(name);
                    if (actions.Any(a => a.Contains("View details", StringComparison.OrdinalIgnoreCase))) attendance = true;
                    else if (actions.Any(a => a.Contains("Take Session Attendance", StringComparison.OrdinalIgnoreCase))) attendance = false;
                    // The material and the assignment as the session shows them now: one removed on the
                    // LMS shows as missing in the app after this read.
                    attachments = await ReadAttachmentsAsync(page);
                    var assignmentButton = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*(Edit|Add)\s+Assignment\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                    if (await assignmentButton.CountAsync() > 0)
                        hasAssignment = (await assignmentButton.InnerTextAsync()).Contains("Edit", StringComparison.OrdinalIgnoreCase);
                    detailsAt = DateTimeOffset.Now;

                    var edit = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"Edit\s+Record\s+Link", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                    var add = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"Add\s+Record\s+Link", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                    if (await edit.CountAsync() > 0 && await edit.IsVisibleAsync())
                    {
                        await edit.ClickAsync();
                        var field = page.Locator("input[placeholder='Enter session record link']").First;
                        try
                        {
                            await field.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                            link = (await field.InputValueAsync()).Trim();
                        }
                        catch (TimeoutException) { link = "(box did not open)"; }
                        await page.Keyboard.PressAsync("Escape");       // closed unsaved
                        kind = Recordings.RecordingLinks.IsAttachable(link)
                            ? (link.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ? "drive" : "zoom")
                            : link.Length > 0 ? "other" : "none";
                    }
                    else if (await add.CountAsync() > 0 && await add.IsVisibleAsync()) kind = "none";
                    else kind = "not offered";
                }
                else pageStatus = "(could not open)";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { pageStatus = $"(error {ex.GetType().Name})"; }
            result.Add(new(item.Group, item.Date, item.Start, item.Title, item.Status, pageUrl, pageStatus, link, kind, attendance, actions)
            { Attachments = attachments, HasAssignment = hasAssignment, DetailsReadAt = detailsAt, Mode = item.Mode, Focus = item.Focus });
        }
        return result;
    }

    /// <summary>A group's students as the dashboard lists them, and the session they were read from.</summary>
    public sealed record LmsRosterResult(bool IsSuccess, string Message, IReadOnlyList<string> Names, DateOnly? ReadFrom = null);

    /// <summary>
    /// Reads a group's students from one of its sessions: this month's first (newest back to the
    /// 1st, then the rest of the month), then last month's. A session whose attendance was taken
    /// shows everyone under "View details"; one still running lists them under "Take Session
    /// Attendance". Either list is only read and closed with Escape - nothing is ticked or saved.
    /// </summary>
    public async Task<LmsRosterResult> ReadRosterAsync(
        string group, DateOnly? today = null, bool headed = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var account = credentials.Read();
        if (account == null)
            return new(false, "No LMS account is chosen. Pick one on the Dashboard first.", []);

        DateOnly day0 = today ?? DateOnly.FromDateTime(DateTime.Now);
        var monthStart = new DateOnly(day0.Year, day0.Month, 1);
        var days = new List<DateOnly>();
        for (var d = day0; d >= monthStart; d = d.AddDays(-1)) days.Add(d);
        for (var d = day0.AddDays(1); d.Month == day0.Month; d = d.AddDays(1)) days.Add(d);
        for (var d = monthStart.AddDays(-1); d >= monthStart.AddMonths(-1); d = d.AddDays(-1)) days.Add(d);

        // Its own browser profile: reading a roster never waits for, or collides with, a Check or a step.
        var profile = _profiles.GetOrCreate(ProfileName + "-roster");
        var browser = await new ZoomBrowserLauncher().LaunchAsync(
            new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        string step = "opening the sign-in page";
        try
        {
            await SignInAsync(page, account, cancellationToken);
            int sessionsSeen = 0;
            foreach (var day in days)
            {
                cancellationToken.ThrowIfCancellationRequested();
                step = $"reading the sessions of {day:yyyy-MM-dd}";
                var matching = await GroupRowsAsync(page, day, group);
                foreach (var (listPage, index) in matching)
                {
                    sessionsSeen++;
                    if (!await OpenListPageAsync(page, day, day, listPage)) continue;           // back to that page of the list
                    var row = page.Locator("table tbody tr").Nth(index);
                    // Only a row that still names the group is opened: another group's students must never be read.
                    if (!RowHasGroup(await RowTextAsync(row), group)) continue;
                    var link = row.Locator("td:first-child a, td:first-child button").First;
                    if (await link.CountAsync() == 0 || !await OpenSessionPageAsync(page, link, group)) continue;

                    step = $"reading the students of the {day:yyyy-MM-dd} session";
                    var names = await ReadSessionStudentsAsync(page);
                    if (names.Count > 0)
                    {
                        ConsoleLogger.Success($"[LMS] {group}: {names.Count} student(s) read from the {day:yyyy-MM-dd} session.");
                        return new(true, $"{group}: {names.Count} students read from the {day:dd MMM} session on the LMS.", names, day);
                    }
                }
            }
            return new(false, sessionsSeen == 0
                ? $"The LMS lists no session for {group} this month or last month (with this LMS account)."
                : $"{sessionsSeen} session(s) of {group} were opened, but none shows its students yet (attendance opens once a session is running or finished).", []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return new(false, $"{WhyItFailed(ex, step)}", []);
        }
    }

    /// <summary>The kinds of file the "Add Attachment" box takes (its file field accepts these only).</summary>
    public static readonly string[] AttachableExtensions = [".pdf", ".zip", ".ppt", ".pptx"];

    /// <summary>
    /// Puts a class's material on its session: each file through "Add Attachment" (its title the
    /// file's name, its type File), then - when there is one - the assignment through "Add
    /// Assignment" with its deadline. A file whose title the session already shows, and an
    /// assignment it already lists, are left alone, so running it again adds only what is missing.
    /// Every box is filled from a freshly loaded page and must close on Save; the first one that does
    /// not stops the run, and the page is read again at the end to say what is really there.
    /// </summary>
    public async Task<LmsMaterialResult> AddMaterialsAsync(
        string group, DateOnly day, TimeOnly? startTime, IReadOnlyList<LmsMaterialFile> files, LmsAssignment? assignment,
        bool dryRun = false, bool headed = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var account = credentials.Read();
        if (account == null) return LmsMaterialResult.Failed("No LMS sign-in is saved. Choose an LMS account on the Dashboard.");
        // Its own browser profile: material goes up while the class runs, when the scheduled task may be
        // pressing Run Session with the account's main one.
        var profile = _profiles.GetOrCreate(ProfileName + "-material");
        var browser = await new ZoomBrowserLauncher().LaunchAsync(
            new ZoomBrowserLaunchPlan(profile, Headless: !headed) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        string step = "opening the sign-in page";
        var added = new List<string>();
        var already = new List<string>();
        try
        {
            await SignInAsync(page, account, cancellationToken);
            step = "opening the session";
            var opened = await OpenSessionAsync(page, group, day, startTime, cancellationToken);
            if (!opened.IsOpen) return LmsMaterialResult.Failed($"{opened.Reason} Nothing was added.");
            string sessionUrl = page.Url;

            string shown = await SessionTextAsync(page);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Shows(shown, file.Title)) { already.Add(file.Title); continue; }
                if (dryRun) continue;
                step = $"adding {file.Title}";
                string? problem = await AddAttachmentAsync(page, sessionUrl, file);
                if (problem != null)
                    return new(false, $"{group}: {file.Title} was not added ({problem}).{Summary(added, already)}", added, already, false, false);
                added.Add(file.Title);
                ConsoleLogger.Success($"[LMS] {group}: attachment added - {file.Title}.");
            }

            bool created = false, assignmentThere = false;
            if (assignment != null)
            {
                assignmentThere = await HasAssignmentAsync(page, sessionUrl);
                if (!assignmentThere && !dryRun)
                {
                    step = "creating the assignment";
                    string? problem = await CreateAssignmentAsync(page, sessionUrl, assignment);
                    if (problem != null)
                        return new(false, $"{group}: the assignment was not created ({problem}).{Summary(added, already)}", added, already, false, false);
                    created = true;
                    ConsoleLogger.Success($"[LMS] {group}: assignment created - {assignment.Title}, due {assignment.Deadline:ddd dd MMM HH:mm}.");
                }
            }

            if (dryRun)
                return new(true, $"{group}: {files.Count - already.Count} file(s) would be added{(assignment != null && !assignmentThere ? $" and the assignment created (due {assignment.Deadline:ddd dd MMM HH:mm})" : "")}. Nothing was changed.{Summary(added, already)}", added, already, false, assignmentThere);

            // What the session shows now, read fresh: the files it lists are the proof.
            step = "reading the session back";
            await page.GotoAsync(sessionUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
            await page.WaitForTimeoutAsync(2000);
            string after = await SessionTextAsync(page);
            var missing = added.Where(t => !Shows(after, t)).ToArray();
            if (missing.Length > 0)
                return new(false, $"{group}: saved, but the session does not list {string.Join(", ", missing)} after reloading.", added, already, created, assignmentThere);
            if (created && !await HasAssignmentAsync(page, sessionUrl))
                return new(false, $"{group}: Create Assignment was pressed, but the session still offers Add Assignment after reloading.", added, already, false, false);
            string assignmentText = created ? $" Assignment created (due {assignment!.Deadline:ddd dd MMM HH:mm})." : assignmentThere ? " The assignment was already there." : "";
            return new(true, $"{group}: {added.Count} file(s) added{(already.Count > 0 ? $", {already.Count} already there" : "")}.{assignmentText}", added, already, created, assignmentThere);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[LMS] Failed while {step}: {ex.GetType().Name}.");
            return new(false, $"{WhyItFailed(ex, step)}{Summary(added, already)}", added, already, false, false);
        }

        static string Summary(List<string> added, List<string> already) =>
            (added.Count > 0 ? $" Added: {string.Join(", ", added)}." : "") + (already.Count > 0 ? $" Already there: {already.Count}." : "");
    }

    /// <summary>
    /// The page shows the title. A page draws "Data  Science" as "Data Science", so both sides are
    /// compared with every run of spaces as one; otherwise a file already there looks missing and is
    /// uploaded again.
    /// </summary>
    public static bool Shows(string pageText, string title) =>
        OneSpace(pageText).Contains(OneSpace(title), StringComparison.OrdinalIgnoreCase);

    private static string OneSpace(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();

    /// <summary>
    /// The titles listed under "Session Attachments" (an empty list when it says "No attachments
    /// available"), read from the card that holds that heading.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ReadAttachmentsAsync(IPage page)
    {
        try
        {
            string text = await page.EvaluateAsync<string>("""
                () => {
                  const heading = Array.from(document.querySelectorAll('div, h2, h3')).find(e => e.childElementCount <= 1 && (e.textContent || '').trim() === 'Session Attachments');
                  if (!heading) return ' ';
                  let card = heading;
                  while (card && !(card.className || '').toString().includes('rounded-xl')) card = card.parentElement;
                  if (!card) return ' ';
                  const body = card.lastElementChild;
                  return body ? body.innerText : '';
                }
                """);
            if (text == "\0") return null;
            return [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 1 && !l.Contains("No attachments available", StringComparison.OrdinalIgnoreCase))];
        }
        catch (PlaywrightException) { return null; }
    }

    /// <summary>The session page's text once its actions and attachments have drawn.</summary>
    private static async Task<string> SessionTextAsync(IPage page)
    {
        var anyAction = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"Add\s+Attachment", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        try { await anyAction.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); } catch (TimeoutException) { }
        await page.WaitForTimeoutAsync(1500);
        try { return await page.Locator("main, body").First.InnerTextAsync(new() { Timeout = 5000 }); }
        catch (PlaywrightException) { return ""; }
    }

    /// <summary>One file through "Add Attachment": title, type File, the file, Save. Null when the box closed on Save.</summary>
    private static async Task<string?> AddAttachmentAsync(IPage page, string sessionUrl, LmsMaterialFile file)
    {
        if (!File.Exists(file.Path)) return "the file is not on this PC";
        await page.GotoAsync(sessionUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });      // a fresh page for every box
        var open = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Add\s+Attachment\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        try { await open.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (TimeoutException) { return "the session offers no Add Attachment"; }
        await page.WaitForTimeoutAsync(800);
        await open.ClickAsync();
        var dialog = page.Locator("[role='dialog']").Last;
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await dialog.Locator("input[name='attachments.0.title']").FillAsync(file.Title);
        // The type is a drawn list (Link / File); File brings the file field.
        await dialog.Locator("[role=combobox]").First.ClickAsync();
        await page.Locator("[role=option]").Filter(new() { HasTextRegex = new(@"^\s*File\s*$") }).First.ClickAsync();
        var input = dialog.Locator("input[type=file]").First;
        try { await input.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 5000 }); }
        catch (TimeoutException) { return "the box did not switch to File"; }
        await input.SetInputFilesAsync(file.Path);
        await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Save\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First.ClickAsync();
        string toast = await ReadToastAsync(page);
        try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 120000 }); }   // a large file takes a while to upload
        catch (TimeoutException) { return toast.Length > 0 ? $"the dashboard said \"{toast}\"" : "the box stayed open after Save"; }
        if (toast.Length > 0) ConsoleLogger.Info($"[LMS] The dashboard said: {toast}");
        return null;
    }

    /// <summary>
    /// A session holds one assignment: once it has it, "Add Assignment" becomes "Edit Assignment"
    /// (as "Add Record Link" becomes "Edit Record Link"). Read from a fresh page.
    /// </summary>
    private static async Task<bool> HasAssignmentAsync(IPage page, string sessionUrl)
    {
        await page.GotoAsync(sessionUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await SessionTextAsync(page);
        var edit = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Edit\s+Assignment\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        return await edit.CountAsync() > 0 && await edit.IsVisibleAsync();
    }

    /// <summary>"Add Assignment": title, description, deadline, Create Assignment. Null when the box closed.</summary>
    private static async Task<string?> CreateAssignmentAsync(IPage page, string sessionUrl, LmsAssignment assignment)
    {
        await page.GotoAsync(sessionUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });
        var open = page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Add\s+Assignment\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        try { await open.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (TimeoutException) { return "the session offers no Add Assignment"; }
        await page.WaitForTimeoutAsync(800);
        await open.ClickAsync();
        var dialog = page.Locator("[role='dialog']").Last;
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await dialog.Locator("#Assignment-title").FillAsync(assignment.Title);
        await dialog.Locator("#Assignment-description").FillAsync(assignment.Description);
        await dialog.Locator("#deadline").FillAsync(assignment.Deadline.ToString("yyyy-MM-dd'T'HH:mm"));
        await dialog.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*Create\s+Assignment\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First.ClickAsync();
        string toast = await ReadToastAsync(page);
        try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 30000 }); }
        catch (TimeoutException) { return toast.Length > 0 ? $"the dashboard said \"{toast}\"" : "the box stayed open"; }
        if (toast.Length > 0) ConsoleLogger.Info($"[LMS] The dashboard said: {toast}");
        return null;
    }

    /// <summary>
    /// Describes a session page's attachments and the fields of its "Add Attachment" and "Add
    /// Assignment" boxes, for building the steps that fill them. Each box is opened, read and closed
    /// with Escape; nothing is typed or saved.
    /// </summary>
    public async Task<string> DescribeSessionAsync(string group, DateOnly day, TimeOnly? startTime = null, CancellationToken cancellationToken = default)
    {
        var account = credentials.Read() ?? throw new InvalidOperationException("No LMS sign-in is saved.");
        var profile = _profiles.GetOrCreate(ProfileName + "-roster");
        var browser = await new ZoomBrowserLauncher().LaunchAsync(new ZoomBrowserLaunchPlan(profile, Headless: true) { Arguments = ChromeSwitches }, cancellationToken);
        await using var closing = browser;
        var page = browser.Context.Pages.Count > 0 ? browser.Context.Pages[0] : await browser.Context.NewPageAsync();
        page.SetDefaultTimeout((float)StepTimeout.TotalMilliseconds);
        await SignInAsync(page, account, cancellationToken);
        var opened = await OpenSessionAsync(page, group, day, startTime, cancellationToken);
        if (!opened.IsOpen) return opened.Reason;
        await page.WaitForTimeoutAsync(2500);
        var text = new System.Text.StringBuilder();
        text.AppendLine("PAGE TEXT: " + (await SessionTextAsync(page)).ReplaceLineEndings(" | "));
        var listed = await ReadAttachmentsAsync(page);
        text.AppendLine("ATTACHMENTS READ: " + (listed == null ? "(card not found)" : listed.Count == 0 ? "(none)" : string.Join(" | ", listed)));
        const string formScript = """
            root => Array.from(root.querySelectorAll('label, input, select, textarea, button, [role=combobox], [role=dialog] h2, [role=dialog] h3')).map(e => {
              const t = e.tagName.toLowerCase();
              if (t === 'select') return 'SELECT ' + (e.name || e.id) + ' [' + Array.from(e.options).map(o => o.text + '=' + o.value).join(', ') + '] value=' + e.value;
              if (t === 'input' || t === 'textarea') return t.toUpperCase() + ' type=' + (e.type || '') + ' name=' + (e.name || '') + ' id=' + (e.id || '') + ' placeholder=' + (e.placeholder || '') + ' accept=' + (e.accept || '') + ' multiple=' + e.multiple;
              return t.toUpperCase() + ' "' + (e.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 80) + '"' + (e.getAttribute('role') ? ' role=' + e.getAttribute('role') : '') + (e.getAttribute('aria-label') ? ' aria=' + e.getAttribute('aria-label') : '');
            }).join('\n')
            """;
        try
        {
            var card = page.Locator("div").Filter(new() { HasTextRegex = new(@"^\s*Session Attachments") }).Last;
            text.AppendLine("ATTACHMENTS CARD: " + (await card.CountAsync() > 0 ? (await card.InnerTextAsync()).ReplaceLineEndings(" | ") : "(not found)"));
            string cardHtml = await page.Locator("h2, h3, div").Filter(new() { HasTextRegex = new(@"Session Attachments") }).Last.EvaluateAsync<string>("e => (e.closest('[class*=card]') || e.parentElement).outerHTML.slice(0, 2500)");
            text.AppendLine("ATTACHMENTS HTML: " + cardHtml);
        }
        catch (PlaywrightException ex) { text.AppendLine("attachments card: " + ex.GetType().Name); }
        Console.WriteLine(text.ToString()); text.Clear();
        foreach (string name in new[] { "Add Assignment", "Add Attachment" })
        {
            try
            {
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });           // a fresh page for each box
                await page.WaitForTimeoutAsync(2500);
                var button = page.GetByRole(AriaRole.Button, new() { NameRegex = new($@"^\s*{name}\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                if (await button.CountAsync() == 0) { text.AppendLine($"{name}: no button"); continue; }
                await button.ClickAsync();
                var dialog = page.Locator("[role='dialog']").Last;
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                await page.WaitForTimeoutAsync(800);
                text.AppendLine($"== {name} ==");
                text.AppendLine(await dialog.EvaluateAsync<string>(formScript));
                if (name == "Add Attachment")
                {
                    // What choosing "File" shows, read from the native select the combobox mirrors.
                    try
                    {
                        await dialog.Locator("[role=combobox]").First.ClickAsync();
                        await page.Locator("[role=option]").Filter(new() { HasTextRegex = new("^\\s*File\\s*$") }).First.ClickAsync();
                        await page.WaitForTimeoutAsync(600);
                        text.AppendLine("-- after choosing File --");
                        text.AppendLine(await dialog.EvaluateAsync<string>(formScript));
                    }
                    catch (PlaywrightException ex) { text.AppendLine("choosing File: " + ex.GetType().Name); }
                }
                Console.WriteLine(text.ToString()); text.Clear();
                // The box's own Close (never Save); Escape only when it has none.
                var close = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new(@"^\s*(Close|Cancel)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
                if (await close.CountAsync() > 0) await close.ClickAsync(new() { Force = true });
                else await page.Keyboard.PressAsync("Escape");
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                text.AppendLine($"{name}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
                Console.WriteLine(text.ToString()); text.Clear();
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });      // whatever stayed open is dropped unsaved
                await page.WaitForTimeoutAsync(2000);
            }
        }
        return text.ToString();
    }

    /// <summary>Reads every page of one day's list and returns where the group's rows are.</summary>
    private static async Task<List<(int Page, int Index)>> GroupRowsAsync(IPage page, DateOnly day, string group)
    {
        var found = new List<(int, int)>();
        if (!await OpenListPageAsync(page, day, day, 1)) return found;
        int pages = await ListPageCountAsync(page);
        for (int number = 1; number <= pages; number++)
        {
            if (number > 1 && !await OpenListPageAsync(page, day, day, number)) break;
            int index = 0;
            foreach (var row in await page.Locator("table tbody tr").AllAsync())
            {
                string text;
                try { text = await RowTextAsync(row); }
                catch (PlaywrightException) { index++; continue; }
                if (RowHasGroup(text, group)) found.Add((number, index));
                index++;
            }
        }
        return found;
    }

    /// <summary>
    /// Opens one page (1 is the first) of the session list for a span of days and waits for its rows.
    /// A list longer than a page - an admin sees every group, 45 sessions on a day - is read page by
    /// page; the page asked for is checked against the pager, and its own link pressed if the
    /// address alone did not get there. False when the list is empty or the page cannot be reached.
    /// </summary>
    private static async Task<bool> OpenListPageAsync(IPage page, DateOnly from, DateOnly to, int number)
    {
        await page.GotoAsync(ListUrl(page, from, to, number), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        if (page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The dashboard asked to sign in again.");
        await page.WaitForTimeoutAsync(800);
        try { await page.Locator("table tbody tr").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 8000 }); }
        catch (TimeoutException) { return false; }
        if (number == 1 || await CurrentListPageAsync(page) == number) return true;

        var link = page.Locator("nav[aria-label*='agination'] a").Filter(new() { HasTextRegex = new($@"^\s*{number}\s*$") }).First;
        if (await link.CountAsync() == 0) return false;
        await link.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(1000);
        return await CurrentListPageAsync(page) == number;
    }

    /// <summary>The page the pager marks as current (1 when it shows none).</summary>
    private static async Task<int> CurrentListPageAsync(IPage page)
    {
        try
        {
            var current = page.Locator("nav[aria-label*='agination'] [aria-current='page']").First;
            if (await current.CountAsync() == 0) return 1;
            return int.TryParse((await current.InnerTextAsync(new() { Timeout = 2000 })).Trim(), out int number) ? number : 1;
        }
        catch (PlaywrightException) { return 1; }
    }

    /// <summary>How many pages the list has, from its "Showing 1-10 of 45 items".</summary>
    private static async Task<int> ListPageCountAsync(IPage page)
    {
        try
        {
            var pager = page.Locator("nav[aria-label*='agination']").First;
            if (await pager.CountAsync() == 0) return 1;
            return PageCount(await pager.InnerTextAsync(new() { Timeout = 2000 }));
        }
        catch (PlaywrightException) { return 1; }
    }

    /// <summary>
    /// The row names this group as a whole word: CAI5_AIS4_S1 is not CAI5_AIS4_S10. With an admin
    /// account every group is listed, so a plain "contains" would open another group's class.
    /// </summary>
    /// <summary>
    /// A list row's text, one cell a tab apart. Read as a whole, a row the page is not laying out
    /// comes back with its cells glued together - "...2026-09-2514:00CAI5_AIS4_S7secondyth..." - and
    /// its group code cannot be found in it (2026-09-26, when the LMS changed its session list). The
    /// cells are read one by one instead, whatever the page does with them.
    /// </summary>
    private static async Task<string> RowTextAsync(ILocator row)
    {
        string cells = await row.EvaluateAsync<string>(
            "tr => [...tr.querySelectorAll('td, th')].map(c => (c.innerText || c.textContent || '').trim()).join(String.fromCharCode(9))",
            null, new() { Timeout = 3000 });
        return cells.Trim().Length > 0 ? cells : await row.InnerTextAsync(new() { Timeout = 3000 });
    }

    internal static bool RowHasGroup(string rowText, string group) =>
        System.Text.RegularExpressions.Regex.IsMatch(rowText ?? "",
            $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(group.Trim())}(?![A-Za-z0-9_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"Showing 1-10 of 45 items" is 5 pages; anything unreadable is one.</summary>
    internal static int PageCount(string pagerText)
    {
        var match = System.Text.RegularExpressions.Regex.Match(pagerText ?? "", @"(\d+)\s*[-–]\s*(\d+)\s+of\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return 1;
        int first = int.Parse(match.Groups[1].Value), last = int.Parse(match.Groups[2].Value), total = int.Parse(match.Groups[3].Value);
        if (first != 1) return 1;                                  // only the first page says how big a page is
        int size = Math.Max(1, last - first + 1);
        return Math.Clamp((total + size - 1) / size, 1, 40);
    }

    /// <summary>
    /// The students on an open session page: "View details" when attendance was taken, otherwise
    /// the "Take Session Attendance" list. The dialog is closed with Escape; nothing is pressed in it.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadSessionStudentsAsync(IPage page)
    {
        var action = page.GetByRole(AriaRole.Button, new()
        { NameRegex = new(@"^\s*View\s+details\s*$|Take\s+Session\s+Attendance", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        try { await action.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 12000 }); }
        catch (TimeoutException) { return []; }
        await page.WaitForTimeoutAsync(800);                     // the other action draws a moment later
        var details = page.GetByRole(AriaRole.Button, new()
        { NameRegex = new(@"^\s*View\s+details\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        var open = await details.CountAsync() > 0 && await details.IsVisibleAsync() ? details : action.First;
        await open.ClickAsync();
        var dialog = page.Locator("[data-slot='dialog-content'][role='dialog']").First;
        try { await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }); }
        catch (TimeoutException) { return []; }
        var rows = await ReadAttendanceRowsAsync(dialog);
        await page.Keyboard.PressAsync("Escape");               // closed; nothing ticked, nothing saved
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. rows.Select(row => row.StudentName.Trim()).Where(name => name.Length > 1 && seen.Add(name))];
    }

    /// <summary>The list for a span of days; page 1 is the address without a page number, as the pager writes it.</summary>
    private static string ListUrl(IPage page, DateOnly from, DateOnly to, int pageNumber) =>
        $"{SessionsUrlOf(page)}?date_from={from:yyyy-MM-dd}&date_to={to:yyyy-MM-dd}" + (pageNumber > 1 ? $"&page={pageNumber}" : "");

    // Each kind of account has its own part of the dashboard (group_admin, super_admin, ...); the
    // session list lives under it. It is read from where signing in lands, per browser page.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, string> Areas = new();
    private static readonly System.Text.RegularExpressions.Regex AreaOfUrl =
        new(@"^https://dashboard\.depi\.eyouthbusiness\.com/(?<area>[a-z]+_admin)(/|$|\?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The session list of the signed-in account's part of the dashboard.</summary>
    private static string SessionsUrlOf(IPage page) =>
        Areas.TryGetValue(page, out var area) ? $"https://dashboard.depi.eyouthbusiness.com/{area}/sessions" : SessionsUrl;

    /// <summary>Remembers the part of the dashboard this page's account works in ("group_admin", "super_admin").</summary>
    internal static string? RememberArea(IPage page, string url)
    {
        var match = AreaOfUrl.Match(url);
        if (!match.Success) return null;
        string area = match.Groups["area"].Value.ToLowerInvariant();
        Areas.AddOrUpdate(page, area);
        return area;
    }

    /// <summary>What one attempt to reach a session's own page ended with.</summary>
    /// <param name="NotListed">No row for the group was found, as opposed to a row that would not open.</param>
    private sealed record SessionPage(bool IsOpen, string Reason, bool NotListed = false)
    {
        /// <summary>What a page that would not open means for a retry. A session the dashboard does
        /// not list will not appear by being asked again; anything else might.</summary>
        public LmsFailure Failure => NotListed ? LmsFailure.SessionNotFound : LmsFailure.Failed;
    }

    /// <summary>
    /// The list, the row and the session's page, tried more than once. Every step here is the
    /// dashboard rendering something after a request, and a slow render has been seen to leave the
    /// row unclicked or the page unopened. Trying again costs seconds; getting it wrong costs a
    /// class's record, so this is retried rather than reported the first time it stumbles.
    /// </summary>
    private static async Task<SessionPage> OpenSessionAsync(
        IPage page, string group, DateOnly date, TimeOnly? startTime, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        string reason = $"The {group} session could not be opened.";
        bool notListed = false;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await OpenTodaysSessionsAsync(page, date, group, cancellationToken);
                var found = await FindSessionRowAsync(page, group, startTime);
                // The day's list can run to several pages (an admin sees every group): the group's
                // row is looked for on each until it is found.
                int pages = found.NoneForGroup ? await ListPageCountAsync(page) : 1;
                for (int number = 2; found.NoneForGroup && number <= pages; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!await OpenListPageAsync(page, date, date, number)) break;
                    found = await FindSessionRowAsync(page, group, startTime);
                }
                if (found.Link == null) { reason = found.Reason; notListed = true; }
                else if (await OpenSessionPageAsync(page, found.Link, group)) return new(true, string.Empty);
                else { reason = $"The row for {group} did not open its session page."; notListed = false; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { reason = $"The session list did not answer ({ex.GetType().Name})."; notListed = false; }

            if (attempt < attempts)
            {
                ConsoleLogger.Warn($"[LMS] {reason} Trying again ({attempt + 1} of {attempts})...");
                await page.WaitForTimeoutAsync(2500);
            }
        }
        return new(false, reason, notListed);
    }

    /// <summary>Every clickable thing the page is showing, named, for the log.</summary>
    private static async Task<string> ListActionsAsync(IPage page)
    {
        try
        {
            var actions = await page.Locator("button, [role='button'], a").AllAsync();
            var names = new List<string>();
            foreach (var action in actions.Take(80))
            {
                if (!await action.IsVisibleAsync()) continue;
                string text = (await action.InnerTextAsync()).Trim().ReplaceLineEndings(" ");
                if (text.Length is 0 or > 40) continue;
                if (!names.Contains(text, StringComparer.OrdinalIgnoreCase)) names.Add(text);
                if (names.Count >= 25) break;
            }
            return names.Count == 0 ? "(nothing clickable)" : string.Join(" | ", names);
        }
        catch { return "(could not be read)"; }
    }

    /// <summary>
    /// Opens the session's own page from its row. A button only means anything there: waiting for
    /// the address to leave the list is what proves we arrived, and without it a button that
    /// happens to exist in the list markup gets pressed instead while the class stays as it was.
    /// </summary>
    private static async Task<bool> OpenSessionPageAsync(IPage page, ILocator row, string group)
    {
        string listUrl = page.Url;
        await row.ClickAsync();
        try
        {
            await page.WaitForURLAsync(url => !string.Equals(url, listUrl, StringComparison.Ordinal),
                new() { Timeout = 20000 });
        }
        catch (TimeoutException) { return false; }
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        ConsoleLogger.Info($"[LMS] Session page open for {group}: {page.Url}");
        return true;
    }

    /// <summary>
    /// What a failed step is reported as. A refused sign-in says so, with the account, because that
    /// is something a person can fix; anything else names only the step, never the exception text,
    /// which can quote a value typed into a field.
    /// </summary>
    internal static string WhyItFailed(Exception ex, string step) =>
        ex is LmsSignInRefusedException refused ? refused.Message : $"The dashboard did not respond while {step}.";

    /// <summary>The error the sign-in form shows, if any; short, and only what the page printed.</summary>
    private static async Task<string> SignInErrorAsync(IPage page)
    {
        try
        {
            var shown = page.Locator("[role='alert'], .invalid-feedback, .text-danger, .error, .alert-danger, .toast, .Toastify__toast");
            foreach (var item in await shown.AllAsync())
            {
                if (!await item.IsVisibleAsync()) continue;
                string text = (await item.InnerTextAsync()).Trim().ReplaceLineEndings(" ");
                if (text.Length is > 0 and <= 160) return text;
            }
        }
        catch { }
        return "";
    }

    private static async Task SignInAsync(IPage page, LmsAccount account, CancellationToken cancellationToken)
    {
        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        cancellationToken.ThrowIfCancellationRequested();
        ConsoleLogger.Info($"[LMS] Sign-in page opened: {page.Url} (signing in as {account.Email})");

        // The dashboard draws its form after the page loads, so the field has to be waited for.
        // Checking whether it exists the instant the document arrives found nothing and skipped
        // signing in altogether, which then looked like the session list failing to open.
        var email = page.Locator("input[type='email']").First;
        try
        {
            await email.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
        }
        catch (TimeoutException)
        {
            // No form: a kept sign-in already took us past it.
            ConsoleLogger.Info("[LMS] No sign-in form; the saved session is still valid.");
            RememberArea(page, page.Url);
            return;
        }

        await email.FillAsync(account.Email);
        await page.Locator("#password, input[type='password']").First.FillAsync(account.Password);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new(@"log ?in|sign ?in", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })
            .First.ClickAsync();

        // Signed in means the login page is behind us; waiting for that is what makes the next
        // step safe to run.
        try
        {
            await page.WaitForURLAsync(url => !url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase),
                new() { Timeout = 30000 });
        }
        catch (TimeoutException)
        {
            // Whatever the page itself says (a wrong password, a locked account) is worth more than
            // a timeout: it tells the person which account to fix. It never quotes what was typed.
            string said = await SignInErrorAsync(page);
            throw new LmsSignInRefusedException(
                $"The LMS did not accept the sign-in for {account.Email}{(said.Length > 0 ? $" (it said: {said})" : "")}. " +
                "Check that account's LMS password.");
        }
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        RememberArea(page, page.Url);
        ConsoleLogger.Info($"[LMS] Signed in; the dashboard is at {page.Url}");
    }

    /// <summary>
    /// Opens one day of sessions.
    ///
    /// The dashboard keeps its filter in the address, not in the form: the dialog only rewrites the
    /// query and reloads. Setting the dates in the dialog therefore changed the boxes and left the
    /// table showing everything, which is how a request for one day came back with sessions from
    /// months earlier. Asking for the address directly is both what the site does and one step.
    /// </summary>
    private static async Task OpenTodaysSessionsAsync(
        IPage page,
        DateOnly date,
        string group,
        CancellationToken cancellationToken)
    {
        string iso = date.ToString("yyyy-MM-dd");
        string url = $"{SessionsUrlOf(page)}?date_from={iso}&date_to={iso}";
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        cancellationToken.ThrowIfCancellationRequested();
        if (page.Url.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The dashboard asked to sign in again before the session list opened.");

        // The rows arrive with the response that follows the navigation.
        await page.WaitForTimeoutAsync(800);
        ConsoleLogger.Info($"[LMS] Sessions for {iso} requested: {page.Url}");
    }

    /// <summary>
    /// Picks the group in the filter dialog's own list. The dialog offers a searchable list rather
    /// than a plain select, so the name is typed and the entry whose text matches exactly is taken;
    /// a near-match is left alone, because the wrong group would start the wrong class.
    /// </summary>
    private static async Task SelectGroupAsync(
        IPage page,
        ILocator dialog,
        string group,
        CancellationToken cancellationToken)
    {
        // The dashboard renders this as a popover trigger whose visible text is the placeholder,
        // so the accessible name does not carry it; the markup selector is what finds it.
        var trigger = dialog.Locator("button[data-slot='popover-trigger']")
            .Filter(new() { HasTextRegex = new(@"filter by group", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        if (await trigger.CountAsync() == 0)
            trigger = dialog.GetByRole(AriaRole.Button, new() { NameRegex = new(@"filter by group", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
        if (await trigger.CountAsync() == 0)
        {
            ConsoleLogger.Info("[LMS] The filter has no group list; the date alone will be used.");
            return;
        }

        try
        {
            await trigger.ClickAsync();
            var search = page.Locator("input[cmdk-input], [role='combobox'] input, input[placeholder*='earch']").First;
            if (await search.CountAsync() > 0 && await search.IsVisibleAsync())
                await search.FillAsync(group);
            await page.WaitForTimeoutAsync(400);

            var option = page.Locator("[cmdk-item], [role='option']")
                .Filter(new() { HasTextRegex = new(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(group) + @"\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).First;
            await option.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
            await option.ClickAsync();
            // No Escape here: the key closes the filter dialog itself, taking Apply with it.
            await page.WaitForTimeoutAsync(300);
            ConsoleLogger.Info($"[LMS] Filtered to the group {group}.");
        }
        catch (TimeoutException)
        {
            // Not offered for this account: the date filter still narrows the list enough to read.
            ConsoleLogger.Info($"[LMS] {group} was not in the group list; the date alone will be used.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>The same address with the dashboard's first page, which it numbers zero.</summary>
    private static string WithFirstPage(string url)
    {
        try
        {
            var builder = new UriBuilder(url);
            var query = System.Web.HttpUtility.ParseQueryString(builder.Query);
            if (query["page"] == null) return url;
            query["page"] = "0";
            builder.Query = query.ToString();
            return builder.Uri.ToString();
        }
        catch (UriFormatException) { return url; }
    }

    /// <summary>
    /// Sets one end of the date filter, the way the working script does it: write the value into
    /// the field first, and only fall back to the calendar when that did not take. Either way the
    /// field is read back afterwards, because a filter that silently kept its old day returns a
    /// completely different set of sessions - and pressing Run Session on one of those would start
    /// the wrong class.
    /// </summary>
    private static async Task SetFilterDateAsync(
        IPage page,
        ILocator dialog,
        string fieldSelector,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        string iso = date.ToString("yyyy-MM-dd");
        bool written = await TryWriteDateAsync(dialog, fieldSelector, iso);
        if (!written) await PickDateAsync(page, dialog, fieldSelector, date, cancellationToken);

        if (await ReadDateAsync(dialog, fieldSelector) == iso)
        {
            ConsoleLogger.Info($"[LMS] {fieldSelector.TrimStart('#')} set to {iso}.");
            return;
        }

        // Whichever way was tried first did not hold; the other one is the second chance.
        if (written) await PickDateAsync(page, dialog, fieldSelector, date, cancellationToken);
        else await TryWriteDateAsync(dialog, fieldSelector, iso);

        string actual = await ReadDateAsync(dialog, fieldSelector);
        if (actual != iso)
            throw new InvalidOperationException(
                $"The {fieldSelector.TrimStart('#')} filter would not take {iso}; it reads \"{actual}\".");
        ConsoleLogger.Info($"[LMS] {fieldSelector.TrimStart('#')} set to {iso}.");
    }

    /// <summary>
    /// Writes the value through the input's own value setter and raises the events a React form
    /// listens for. Assigning to value alone leaves React showing its old state.
    /// </summary>
    private static async Task<bool> TryWriteDateAsync(ILocator dialog, string fieldSelector, string iso)
    {
        var field = dialog.Locator(fieldSelector).First;
        if (await field.CountAsync() == 0) return false;
        try
        {
            await field.EvaluateAsync<object?>(
                """
                (el, value) => {
                  const proto = Object.getPrototypeOf(el);
                  const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
                  if (setter) setter.call(el, value); else el.value = value;
                  el.setAttribute("value", value);
                  el.dispatchEvent(new Event("input", { bubbles: true }));
                  el.dispatchEvent(new Event("change", { bubbles: true }));
                }
                """,
                iso);
            return true;
        }
        catch (PlaywrightException) { return false; }
    }

    /// <summary>The day the field is actually showing, whichever attribute it keeps it in.</summary>
    private static async Task<string> ReadDateAsync(ILocator dialog, string fieldSelector)
    {
        var field = dialog.Locator(fieldSelector).First;
        if (await field.CountAsync() == 0) return string.Empty;
        string raw;
        try
        {
            raw = await field.EvaluateAsync<string>(
                """
                el => [el.value, el.getAttribute("value"), el.getAttribute("aria-label"), el.textContent]
                  .filter(Boolean).join(" | ")
                """) ?? string.Empty;
        }
        catch (PlaywrightException) { return string.Empty; }

        var match = System.Text.RegularExpressions.Regex.Match(raw, @"\d{4}-\d{2}-\d{2}");
        return match.Success ? match.Value : string.Empty;
    }
    /// <summary>
    /// Chooses a day in the dashboard's calendar: press the field, walk the month header to the
    /// right month, then press the day. Typing into the field does nothing, which is why the
    /// calendar is driven instead.
    /// </summary>
    private static async Task PickDateAsync(
        IPage page,
        ILocator dialog,
        string fieldSelector,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var field = dialog.Locator(fieldSelector).First;
        if (await field.CountAsync() == 0) return;
        await field.ClickAsync();

        var calendar = page.Locator("[data-slot='calendar'], .rdp-root").First;
        await calendar.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        var caption = calendar.Locator(".rdp-caption_label").First;
        await caption.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var previous = calendar.Locator("button.rdp-button_previous, .rdp-button_previous").First;
        var next = calendar.Locator("button.rdp-button_next, .rdp-button_next").First;
        var target = new DateTime(date.Year, date.Month, 1);

        for (int step = 0; step < 24; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shown = ParseCaption(await caption.InnerTextAsync());
            if (shown == null || (shown.Value.Year == target.Year && shown.Value.Month == target.Month)) break;
            if (shown > target) await previous.ClickAsync();
            else await next.ClickAsync();
            await page.WaitForTimeoutAsync(120);
        }

        var day = calendar.Locator($"td[data-day='{date:yyyy-MM-dd}'] button").First;
        await day.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await day.ClickAsync();
        ConsoleLogger.Info($"[LMS] {fieldSelector.TrimStart('#')} set to {date:yyyy-MM-dd}.");
    }

    /// <summary>The month the calendar is showing, read from its "September 2026" header.</summary>
    private static DateTime? ParseCaption(string caption)
    {
        var parts = caption.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[^1], out int year)) return null;
        return DateTime.TryParseExact(
            $"{parts[0]} 1 {year}",
            "MMMM d yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed) ? parsed : null;
    }

    /// <param name="NoneForGroup">This page of the list has no row for the group (another page may).</param>
    private sealed record RowMatch(ILocator? Link, string Reason, bool NoneForGroup = false);

    /// <summary>
    /// The session-name link for this class. A group can hold two classes in one day, so the
    /// scheduled time decides between them; the closest listed time within half an hour is the
    /// one meant. Anything still ambiguous opens nothing and says so.
    /// </summary>
    private static async Task<RowMatch> FindSessionRowAsync(IPage page, string group, TimeOnly? startTime)
    {
        // Wait for the filtered table before reading it, and give up quickly when the day is empty.
        var rows = page.Locator("table tbody tr");
        try { await rows.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 }); }
        catch (TimeoutException) { return new(null, "The filtered session list came back empty."); }

        var matches = new List<(ILocator Link, TimeOnly? Time, string Text)>();
        var listedGroups = new List<string>();
        int rowsSeen = 0;
        foreach (var row in await rows.AllAsync())
        {
            string text;
            // A row being replaced must not cost a full default timeout each time.
            try { text = await RowTextAsync(row); }
            catch (PlaywrightException) { continue; }
            rowsSeen++;
            listedGroups.Add(Summarise(text));
            if (!RowHasGroup(text, group)) continue;

            var link = row.Locator("td:first-child a, td:first-child button").First;
            if (await link.CountAsync() == 0) continue;
            matches.Add((link, ReadRowTime(text), text));
        }

        if (matches.Count == 0)
        {
            // Say what the day does hold, so a name that does not match is obvious at a glance.
            foreach (string listed in listedGroups.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                ConsoleLogger.Info($"[LMS] Listed that day: {listed}");
            return new(null,
                $"No session for {group} is listed for that day on the dashboard " +
                $"({rowsSeen} session(s) shown after filtering).", NoneForGroup: true);
        }
        if (matches.Count == 1) return new(matches[0].Link, string.Empty);

        // The table can render one session as more than one row. Rows that agree on the time are
        // the same class, so they are not an ambiguity to refuse.
        var distinctTimes = matches.Select(match => match.Time).Distinct().ToArray();
        if (distinctTimes.Length == 1) return new(matches[0].Link, string.Empty);

        if (startTime == null)
            return new(null,
                $"{matches.Count} sessions for {group} are listed that day and no scheduled time was given, " +
                "so none was opened.");

        var closest = matches
            .Where(match => match.Time != null)
            .Select(match => (match.Link, Minutes: MinutesApart(match.Time!.Value, startTime.Value)))
            .OrderBy(match => match.Minutes)
            .ToArray();
        if (closest.Length == 0)
            return new(null, $"{matches.Count} sessions for {group} are listed that day and none shows a time.");
        // The LMS time and the timetable's can differ (Freelancing classes read 17:00 on the LMS and
        // 19:00 in the timetable); a group never has two classes of one kind in a day, so the closest
        // listed time is taken whatever the distance.
        if (closest.Length > 1 && closest[1].Minutes == closest[0].Minutes)
            return new(null, $"Two sessions for {group} share the same time; none was opened.");

        ConsoleLogger.Info($"[LMS] Matched the {startTime:HH':'mm} session for {group}.");
        return new(closest[0].Link, string.Empty);
    }

    /// <summary>A row squeezed onto one line, for a log that has to stay readable.</summary>
    /// <summary>What a class is called, from its row in the list.</summary>
    private static readonly System.Text.RegularExpressions.Regex WeekAndSession = new(
        @"week\s*\d+\s*[-–]\s*session\s*\d+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The class's name as the dashboard shows it - "Week 10 - Session 2".
    ///
    /// The columns are not in the same order for every account: read as a coordinator, the first
    /// cell of the row is the session's number, so every class of theirs was called "29.0" and the
    /// week it belongs to was lost (2026-09-21, Hosam's classes). The name is therefore looked for,
    /// and only a row that has none falls back to its first cell.
    /// </summary>
    internal static string TitleOfRow(string rowText)
    {
        var cells = rowText.Split(SummaryBreaks, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        if (cells.FirstOrDefault(cell => WeekAndSession.IsMatch(cell)) is { } named) return named;
        // Nothing named a week: the first cell that says something - not a number, a date, a time,
        // a status or the group code the row is already known by.
        bool Plain(string cell) =>
            !System.Text.RegularExpressions.Regex.IsMatch(cell, @"^[\d.,:/\s-]+$")
            && !System.Text.RegularExpressions.Regex.IsMatch(cell, @"^(pending|running|finished|cancelled|completed)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !GroupCode.IsMatch(cell);
        return cells.FirstOrDefault(Plain) ?? cells.FirstOrDefault() ?? "";
    }

    private static string Summarise(string rowText) =>
        string.Join(" | ", rowText.Split(SummaryBreaks, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()).Where(part => part.Length > 0).Take(4));

    private static readonly char[] SummaryBreaks = [(char)10, (char)13, (char)9];

    /// <summary>The HH:mm the dashboard prints in the row's date cell.</summary>
    private static TimeOnly? ReadRowTime(string rowText)
    {
        // The cell renders as "2026-09-02" and "19:00" with nothing between them once the row is
        // read as text, so the time is taken from just after the date rather than on its own.
        var afterDate = System.Text.RegularExpressions.Regex.Match(
            rowText, @"\d{4}-\d{2}-\d{2}[^\d]{0,3}(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)");
        var match = afterDate.Success
            ? afterDate
            : System.Text.RegularExpressions.Regex.Match(rowText, @"(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)");
        return match.Success && TimeOnly.TryParse($"{match.Groups["h"].Value}:{match.Groups["m"].Value}", out var time)
            ? time
            : null;
    }

    private static int MinutesApart(TimeOnly left, TimeOnly right) =>
        (int)Math.Abs((left.ToTimeSpan() - right.ToTimeSpan()).TotalMinutes);

    /// <summary>The toast the dashboard raises after an action, while it is still on screen.</summary>
    private static async Task<string> ReadToastAsync(IPage page)
    {
        var toast = page.Locator("[data-sonner-toast], [role='status'], [role='alert'], .toast, .Toastify__toast").First;
        try
        {
            await toast.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 6000 });
            return (await toast.InnerTextAsync(new() { Timeout = 2000 }))
                .Replace((char)10, ' ').Replace((char)13, ' ').Trim();
        }
        catch (TimeoutException) { return string.Empty; }
        catch (PlaywrightException) { return string.Empty; }
    }

    private static async Task<string> ReadStatusAsync(IPage page)
    {
        try
        {
            var status = page.GetByText(new System.Text.RegularExpressions.Regex(
                @"^\s*(pending|running|finished|cancelled)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)).First;
            return await status.CountAsync() > 0 ? (await status.InnerTextAsync()).Trim() : string.Empty;
        }
        catch (PlaywrightException) { return string.Empty; }
    }
}

/// <summary>The LMS kept the sign-in page after the password was sent: the account's password is not accepted.</summary>
public sealed class LmsSignInRefusedException(string message) : InvalidOperationException(message);
