using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.WebAutomation.Browser;

namespace ZoomAutoAdmit.WebAutomation.Lms;

/// <summary>What one attempt to start a session on the LMS did.</summary>
public sealed record LmsRunResult(bool IsSuccess, string Message)
{
    public static LmsRunResult Success(string message) => new(true, message);
    public static LmsRunResult Failure(string message) => new(false, message);
}

/// <summary>
/// What an attendance upload did, and the decision it was going to write. The plan comes back on a
/// dry run and on a real one, so what was intended can be read next to what happened.
/// </summary>
public sealed record LmsAttendanceResult(bool IsSuccess, string Message, LmsAttendancePlan? Plan);

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
    private const string ProfileName = "lms-dashboard";
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
                return LmsRunResult.Failure($"{opened.Reason} Nothing was pressed.");

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
            return LmsRunResult.Failure($"The dashboard did not respond while {step}.");
        }
    }

    /// <summary>
    /// Puts the recording's link on the session, the way it is done by hand: open the session,
    /// press "Add Record Link", paste, Save. The dashboard only offers that button once the
    /// session is finished, so a session that is not finished yet is reported rather than forced.
    /// </summary>
    /// <param name="recordLink">The Zoom shareable link, already copied from the recording.</param>
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
        if (!Zoom.ZoomRecordingLinkReader.IsShareLink(recordLink))
            return LmsRunResult.Failure("That is not a Zoom recording link, so nothing was saved.");
        var account = credentials.Read();
        if (account == null)
            return LmsRunResult.Failure("No LMS sign-in is saved. Add it in the app before attaching a recording.");

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
                return LmsRunResult.Failure($"{opened.Reason} Nothing was saved.");

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
                return LmsRunResult.Failure(
                    $"The session page for {group} offers no Add Record Link" +
                    $"{(state.Length > 0 ? $"; it reads \"{state}\"" : string.Empty)}. " +
                    "The dashboard only offers it once the session is finished.");
            }

            // A session that already carries a link is left alone unless replacing it was asked
            // for: quietly writing over a link somebody put there by hand is not this step's job.
            string buttonText = (await add.InnerTextAsync()).Trim();
            bool alreadyHasLink = buttonText.Contains("Edit", StringComparison.OrdinalIgnoreCase);
            if (alreadyHasLink && !replaceExisting)
                return LmsRunResult.Success(
                    $"{group}: the session already has a recording link, so it was left as it is.");

            step = "opening the record link box";
            await add.ClickAsync();
            var field = page.Locator("input[placeholder='Enter session record link']").First;
            try { await field.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 }); }
            catch (TimeoutException)
            {
                return LmsRunResult.Failure($"The record link box did not open for {group}, so nothing was saved.");
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
            return LmsRunResult.Failure($"The dashboard did not respond while {step}.");
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
            return new(false, "No LMS sign-in is saved. Add it in the app before taking attendance.", null);

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
            if (!opened.IsOpen) return new(false, $"{opened.Reason} No attendance was taken.", null);

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
            return new(false, $"The dashboard did not respond while {step}.", null);
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
            if (!opened.IsOpen) return new(false, $"{opened.Reason} Nothing was changed.", null);

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
            return new(false, $"The dashboard did not respond while {step}.", null);
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

    /// <summary>What one attempt to reach a session's own page ended with.</summary>
    private sealed record SessionPage(bool IsOpen, string Reason);

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
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await OpenTodaysSessionsAsync(page, date, group, cancellationToken);
                var found = await FindSessionRowAsync(page, group, startTime);
                if (found.Link == null) reason = found.Reason;
                else if (await OpenSessionPageAsync(page, found.Link, group)) return new(true, string.Empty);
                else reason = $"The row for {group} did not open its session page.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { reason = $"The session list did not answer ({ex.GetType().Name})."; }

            if (attempt < attempts)
            {
                ConsoleLogger.Warn($"[LMS] {reason} Trying again ({attempt + 1} of {attempts})...");
                await page.WaitForTimeoutAsync(2500);
            }
        }
        return new(false, reason);
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

    private static async Task SignInAsync(IPage page, LmsAccount account, CancellationToken cancellationToken)
    {
        await page.GotoAsync(LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        cancellationToken.ThrowIfCancellationRequested();
        ConsoleLogger.Info($"[LMS] Sign-in page opened: {page.Url}");

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
            throw new InvalidOperationException("The dashboard stayed on the sign-in page; check the email and password.");
        }
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
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
        string url = $"{SessionsUrl}?date_from={iso}&date_to={iso}";
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

    private sealed record RowMatch(ILocator? Link, string Reason);

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
            try { text = await row.InnerTextAsync(new() { Timeout = 3000 }); }
            catch (PlaywrightException) { continue; }
            rowsSeen++;
            listedGroups.Add(Summarise(text));
            if (!text.Contains(group, StringComparison.OrdinalIgnoreCase)) continue;

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
                $"({rowsSeen} session(s) shown after filtering).");
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
        if (closest[0].Minutes > 30)
            return new(null,
                $"The nearest listed session for {group} is {closest[0].Minutes} minutes from {startTime:HH':'mm}; " +
                "none was opened.");
        if (closest.Length > 1 && closest[1].Minutes == closest[0].Minutes)
            return new(null, $"Two sessions for {group} share the same time; none was opened.");

        ConsoleLogger.Info($"[LMS] Matched the {startTime:HH':'mm} session for {group}.");
        return new(closest[0].Link, string.Empty);
    }

    /// <summary>A row squeezed onto one line, for a log that has to stay readable.</summary>
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
