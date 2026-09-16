using Microsoft.Playwright;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.Core.Engines;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.WebAutomation.Browser;
using ZoomAutoAdmit.WebAutomation.Models;

namespace ZoomAutoAdmit.WebAutomation;

public sealed class WebAutoAdmitEngine : IAutoAdmitEngine, IAsyncDisposable
{
    private const int MaximumClickAttempts = 2;
    private static readonly TimeSpan VerificationWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan InitialVerificationStabilization = TimeSpan.FromSeconds(1);
    private readonly ZoomProfileManager _profileManager;
    private readonly IZoomBrowserLauncher _browserLauncher;
    private readonly ZoomWebMeetingController _meetingController;
    private readonly ZoomWaitingRoomDom _dom;
    private ZoomBrowserSession? _session;
    private CancellationTokenSource? _stopCancellation;
    private int _stopped;

    public WebAutoAdmitEngine(
        ZoomProfileManager? profileManager = null,
        IZoomBrowserLauncher? browserLauncher = null,
        ZoomWebMeetingController? meetingController = null,
        ZoomWaitingRoomDom? dom = null)
    {
        _profileManager = profileManager ?? new ZoomProfileManager();
        _browserLauncher = browserLauncher ?? new ZoomBrowserLauncher();
        _meetingController = meetingController ?? new ZoomWebMeetingController();
        _dom = dom ?? new ZoomWaitingRoomDom();
    }

    public string Name => "web";
    // Read-only runtime binding; attendance must never create or select a replacement page.
    public IPage? ActiveMeetingPage => _session?.ActiveMeetingPage;

    /// <summary>How long a visible browser waits for a person to complete the Zoom sign-in.</summary>
    private static readonly TimeSpan ManualSignInTimeout = TimeSpan.FromMinutes(10);

    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        int timeoutSeconds = options.TimeoutExplicitlySet ? options.TimeoutSeconds : 0;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0) linkedCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            linkedCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            await StartAsync(options, linkedCancellation.Token);
            await MonitorAsync(options, linkedCancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (PlaywrightException ex)
        {
            ConsoleLogger.Error($"WEB_BROWSER_FAILURE: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"WEB_AUTO_ADMIT_FAILURE: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await StopAsync();
        }
    }

    public async Task StartAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        if (_session != null) throw new InvalidOperationException("Web auto-admit engine is already started.");
        if (string.IsNullOrWhiteSpace(options.MeetingUrl))
            throw new ArgumentException("The web engine requires --meeting-url <Zoom URL>.", nameof(options));
        _ = ZoomWebMeetingController.ValidateMeetingUrl(options.MeetingUrl);

        Interlocked.Exchange(ref _stopped, 0);
        _stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var profile = _profileManager.GetOrCreate(options.WebProfile);
        ConsoleLogger.Success("WEB_PROFILE_LOADED");
        ConsoleLogger.Info($"Profile: {profile.Name}");
        var plan = _profileManager.CreateLaunchPlan(profile, options.WebHeaded);
        _session = await _browserLauncher.LaunchAsync(plan, _stopCancellation.Token);
        ConsoleLogger.Success("WEB_BROWSER_STARTED");
        ConsoleLogger.Info($"Browser mode: {(_session.IsHeadless ? "headless" : "visible")}");
        try { await OpenMeetingAsync(options, profile); }
        catch
        {
            // A meeting that did not open leaves nothing behind: the browser is closed (it would
            // otherwise keep the profile locked, so the next try "opened in an existing session"
            // and failed) and the engine can be started again.
            await CloseSessionAsync();
            Interlocked.Exchange(ref _stopped, 0);
            throw;
        }
        ConsoleLogger.Success("Waiting room monitor started");
    }

    private async Task OpenMeetingAsync(CliOptions options, ZoomBrowserProfile profile)
    {
        var session = _session!;
        // A profile not yet known to host (new, or it joined as a guest last time) is signed in to
        // Zoom first with the account's saved password, so it joins as the host.
        if (!profile.HasReusableSession && ZoomSignInCredential.Read(options.WebSignInCredential) is { } credential)
        {
            var outcome = await ZoomWebSignIn.EnsureSignedInAsync(session.Context, credential, _stopCancellation!.Token);
            if (outcome is ZoomSignInOutcome.NeedsPerson or ZoomSignInOutcome.Failed && session.IsHeadless)
                throw new ZoomWebSignInRequiredException(profile.Name, $"Profile '{profile.Name}' could not be signed in to Zoom by itself.");
        }
        try
        {
            await _meetingController.OpenAndWaitForHostControlsAsync(
                session,
                options.MeetingUrl!,
                _profileManager,
                _stopCancellation!.Token);
        }
        catch (ZoomWebSignInRequiredException ex) when (session.IsHeadless)
        {
            // A saved sign-in Zoom no longer accepts is a request for a login, not a dead end.
            // Reopen the same profile in a visible browser so it can be signed in once, exactly
            // like setting up a profile by hand, then carry on with this meeting.
            ConsoleLogger.Warn($"[WEB_PROFILE] {ex.Message}");
            ConsoleLogger.Info(
                $"[WEB_PROFILE] Opening a visible browser for profile '{profile.Name}'. Sign in to Zoom there and the meeting continues by itself; the sign-in is saved for next time.");
            await CloseSessionAsync();
            profile = _profileManager.MarkSessionExpired(profile);
            var plan = _profileManager.CreateLaunchPlan(profile, forceHeaded: true);
            _session = await _browserLauncher.LaunchAsync(plan, _stopCancellation!.Token);
            ConsoleLogger.Info("Browser mode: visible (waiting for sign-in)");
            await _meetingController.OpenAndWaitForHostControlsAsync(
                _session,
                options.MeetingUrl!,
                _profileManager,
                _stopCancellation.Token,
                ManualSignInTimeout);
            ConsoleLogger.Success($"[WEB_PROFILE] Profile '{profile.Name}' is signed in and saved.");
        }
    }

    private async Task CloseSessionAsync()
    {
        if (_session == null) return;
        try { await _session.DisposeAsync(); }
        catch (Exception ex) { ConsoleLogger.Debug($"Closing the previous browser failed: {ex.Message}"); }
        _session = null;
    }

    public async Task MonitorAsync(CliOptions options, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new InvalidOperationException("StartAsync must complete before MonitorAsync.");
        if (string.IsNullOrWhiteSpace(options.MeetingUrl))
            throw new ArgumentException("The web engine requires --meeting-url <Zoom URL>.", nameof(options));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stopCancellation?.Token ?? CancellationToken.None);
        bool pageMissingLogged = false;

        bool pausedLogged = false;
        var nextPanelCheck = DateTimeOffset.UtcNow;
        var nextLauncherCheck = DateTimeOffset.UtcNow;
        while (!linked.IsCancellationRequested)
        {
            try
            {
                // Same switch the desktop engine reads: while it is off nobody is let in.
                if (!ZoomAutoAdmit.Core.Meetings.AdmissionControl.IsAdmitting)
                {
                    if (!pausedLogged)
                    {
                        ConsoleLogger.Info("ADMITTING_PAUSED: waiting for the operator to switch it back on.");
                        pausedLogged = true;
                    }
                    await DelayAsync(options.WebPollIntervalMilliseconds, linked.Token);
                    continue;
                }
                if (pausedLogged)
                {
                    ConsoleLogger.Info("ADMITTING_RESUMED");
                    pausedLogged = false;
                }

                var surface = await _meetingController.FindActiveMeetingAsync(session);
                if (surface == null)
                {
                    // Back on Zoom's "open the app" page (a reload, a rejoin): join from the browser again.
                    if (DateTimeOffset.UtcNow >= nextLauncherCheck)
                    {
                        nextLauncherCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
                        try
                        {
                            if (!await ZoomLauncherPage.TryJoinFromBrowserAsync(session.Context))
                                await ZoomLauncherPage.TryPressJoinOnPreviewAsync(session.Context);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { ConsoleLogger.Debug($"WEB_LAUNCHER_PAGE: {ex.Message}"); }
                    }
                    if (ZoomWebMeetingController.HasOpenMeetingPage(session))
                    {
                        await DelayAsync(options.WebPollIntervalMilliseconds, linked.Token);
                        continue;
                    }
                    if (!pageMissingLogged)
                    {
                        ConsoleLogger.Info("WEB_MEETING_PAGE_NOT_FOUND");
                        pageMissingLogged = true;
                    }
                    await _meetingController.KeepMeetingPageAliveAsync(session, options.MeetingUrl);
                    await DelayAsync(options.WebPollIntervalMilliseconds, linked.Token);
                    continue;
                }

                pageMissingLogged = false;
                if (DateTimeOffset.UtcNow >= nextPanelCheck)
                {
                    nextPanelCheck = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                    await EnsureParticipantsPanelOpenAsync(surface);
                }
                var snapshot = await _dom.CaptureAsync(surface);
                var decision = WebAdmissionPolicy.Decide(snapshot);
                if (decision.Kind == WebAdmissionKind.None)
                {
                    await DelayAsync(options.WebPollIntervalMilliseconds, linked.Token);
                    continue;
                }

                ConsoleLogger.Info("WEB_WAITING_ROOM_DETECTED");
                ConsoleLogger.Info($"Waiting participants: {snapshot.WaitingCount}");
                await ExecuteWithRetryAsync(
                    session,
                    snapshot,
                    decision,
                    options.WebPollIntervalMilliseconds,
                    linked.Token);
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                ConsoleLogger.Warn($"WEB_DOM_RETRY: {ex.Message}");
            }
            catch (PlaywrightException ex)
            {
                // A blocked click or a timed-out lookup is one bad poll, not a reason to leave the
                // meeting. The monitor keeps watching and tries again on the next pass.
                ConsoleLogger.Warn($"WEB_POLL_FAILED: {FirstLine(ex.Message)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
            {
                // Any other error in one pass used to end the whole monitor - silently - while the
                // meeting went on with nobody admitted (2026-09-16). It is one bad pass too.
                ConsoleLogger.Warn($"[AUTO_ADMIT] WEB_POLL_FAILED ({ex.GetType().Name}): {FirstLine(ex.Message)}");
            }

            await DelayAsync(options.WebPollIntervalMilliseconds, linked.Token);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _stopCancellation?.Cancel();
        _stopCancellation?.Dispose();
        _stopCancellation = null;
        if (_session != null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        ConsoleLogger.Info("WEB_AUTO_ADMIT_STOPPED");
    }

    // Zoom's web client names these controls by what pressing them does, and the wording has
    // grown: the microphone button reads "unmute my microphone" rather than "Unmute". Matching
    // only the short forms found neither button, so the host joined a class unmuted.
    // "Mute all" in the participants panel is deliberately excluded from the mute patterns.
    public Task<bool> DisableMicrophoneAsync(CancellationToken cancellationToken = default) =>
        EnsureMeetingControlOffAsync(
            new Regex(@"^unmute(?:\s+my)?(?:\s+(?:audio|microphone|mic))?$", RegexOptions.IgnoreCase),
            new Regex(@"^mute(?:\s+my)?(?:\s+(?:audio|microphone|mic))?$", RegexOptions.IgnoreCase),
            cancellationToken);

    public Task<bool> DisableCameraAsync(CancellationToken cancellationToken = default) =>
        EnsureMeetingControlOffAsync(
            new Regex(@"^start(?:\s+my)?\s+video$", RegexOptions.IgnoreCase),
            new Regex(@"^stop(?:\s+my)?\s+video$", RegexOptions.IgnoreCase),
            cancellationToken);

    public ValueTask DisposeAsync() => new(StopAsync());

    private static readonly Regex OpenParticipants = new(@"^open the manage participants list pane|^participants(,|\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CloseParticipants = new(@"^close the manage participants list pane|^close participants", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The participants panel is kept open: the waiting room, admission and attendance all read it.
    /// Opened again whenever it was closed (by Zoom or by a person), never closed.
    /// </summary>
    private static async Task EnsureParticipantsPanelOpenAsync(ZoomMeetingSurface surface)
    {
        try
        {
            if (await ZoomWaitingRoomDom.HasWaitingRoomHeaderAsync(surface.Frame)) return;
            foreach (var close in await surface.Frame.GetByRole(AriaRole.Button, new() { NameRegex = CloseParticipants }).AllAsync())
                if (await close.IsVisibleAsync()) return;
            // With nobody moving a mouse the toolbar hides itself, and its Participants button is
            // then invisible - the panel would silently never reopen (seen live, 2026-09-16).
            await ZoomWebToolbar.WakeAsync(surface.Page);
            foreach (var open in await surface.Frame.GetByRole(AriaRole.Button, new() { NameRegex = OpenParticipants }).AllAsync())
            {
                if (!await open.IsVisibleAsync()) continue;
                await open.EvaluateAsync<object?>("element => element.click()");
                ConsoleLogger.Info("WEB_PARTICIPANTS_PANEL: opened it again.");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    private async Task<bool> EnsureMeetingControlOffAsync(
        Regex alreadyOffName,
        Regex turnOffName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _session ?? throw new InvalidOperationException("The Web meeting is not started.");
        var surface = await _meetingController.FindActiveMeetingAsync(session);
        if (surface == null) return false;
        foreach (var button in await surface.Frame.GetByRole(
                     AriaRole.Button,
                     new() { NameRegex = alreadyOffName }).AllAsync())
        {
            if (await button.IsVisibleAsync()) return true;
        }
        foreach (var button in await surface.Frame.GetByRole(
                     AriaRole.Button,
                     new() { NameRegex = turnOffName }).AllAsync())
        {
            if (!await button.IsVisibleAsync()) continue;
            // Zoom floats dialogs over its toolbar, and a blocked pointer must not leave the host
            // live, so the same DOM press the admission path uses is used here too.
            try
            {
                await button.EvaluateAsync<object?>("element => element.click()");
                return true;
            }
            catch (PlaywrightException)
            {
                await button.ClickAsync(new() { Timeout = 2000, Force = true });
                return true;
            }
        }
        return false;
    }

    private async Task ExecuteWithRetryAsync(
        ZoomBrowserSession session,
        WebWaitingRoomSnapshot initialSnapshot,
        WebAdmissionDecision initialDecision,
        int pollMilliseconds,
        CancellationToken cancellationToken)
    {
        var before = initialSnapshot;
        var decision = initialDecision;
        var singleAdmitStrategy = AdmitStrategy.NotificationThenParticipantRow;
        for (int attempt = 1; attempt <= MaximumClickAttempts; attempt++)
        {
            var surface = await _meetingController.FindActiveMeetingAsync(session);
            if (surface == null) return;

            bool clicked;
            if (decision.Kind == WebAdmissionKind.AdmitAll)
            {
                ConsoleLogger.Info("WEB_ADMIT_ALL_FOUND");
                clicked = await _dom.ClickAdmitAllAsync(surface);
            }
            else if (decision.Kind == WebAdmissionKind.Single && decision.Participant != null)
            {
                ConsoleLogger.Info("WEB_ADMIT_FOUND");
                ConsoleLogger.Info($"Participant: {decision.Participant.Name}");
                clicked = await _dom.ClickParticipantAsync(
                    surface,
                    decision.Participant.Identity,
                    singleAdmitStrategy);
            }
            else
            {
                return;
            }

            if (!clicked)
            {
                ConsoleLogger.Warn("WEB_ADMIT_TARGET_DISAPPEARED_BEFORE_CLICK");
                return;
            }
            ConsoleLogger.Success("WEB_CLICK_SENT");

            var verification = await VerifyAsync(
                session,
                before,
                decision,
                pollMilliseconds,
                cancellationToken);
            if (verification.Result.IsVerified)
            {
                ConsoleLogger.Success("WEB_ADMISSION_VERIFIED");
                ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.NotifyVerified();
                ConsoleLogger.Success("ADMISSION_CONFIRMED");
                return;
            }

            ConsoleLogger.Warn($"WEB_ADMISSION_NOT_VERIFIED: {verification.Result.Reason}");
            if (attempt >= MaximumClickAttempts || !verification.Result.ShouldRetry) return;
            before = verification.Snapshot;
            decision = WebAdmissionPolicy.Decide(before);
            if (decision.Kind == WebAdmissionKind.None) return;
            singleAdmitStrategy = AdmitStrategy.ParticipantRowOnly;
            ConsoleLogger.Info("WEB_ADMISSION_RETRY");
        }
    }

    private async Task<(WebAdmissionVerification Result, WebWaitingRoomSnapshot Snapshot)> VerifyAsync(
        ZoomBrowserSession session,
        WebWaitingRoomSnapshot before,
        WebAdmissionDecision decision,
        int pollMilliseconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + VerificationWindow;
        var latest = before;
        var result = new WebAdmissionVerification(false, true, "Waiting for the Waiting Room DOM to update.");
        await Task.Delay(InitialVerificationStabilization, cancellationToken);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await DelayAsync(pollMilliseconds, cancellationToken);
            try
            {
                var surface = await _meetingController.FindActiveMeetingAsync(session);
                if (surface == null)
                {
                    result = new(
                        false,
                        true,
                        "Waiting for the meeting page to finish updating.");
                    ConsoleLogger.Info($"ADMISSION_VERIFICATION_RETRY: {result.Reason}");
                    continue;
                }

                latest = await _dom.CaptureAsync(surface);
                result = WebAdmissionVerifier.Evaluate(before, latest, decision);
                if (result.IsVerified || !result.ShouldRetry) return (result, latest);
                ConsoleLogger.Info($"ADMISSION_VERIFICATION_RETRY: {result.Reason}");
            }
            catch (PlaywrightException ex) when (PlaywrightNavigationFailurePolicy.IsTransient(ex))
            {
                result = new(
                    false,
                    true,
                    "Waiting for the retained meeting frame to reconnect.");
                ConsoleLogger.Info($"ADMISSION_VERIFICATION_RETRY: {result.Reason}");
            }
        }
        return (result, latest);
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n');
        return newline < 0 ? message : message[..newline].Trim();
    }

    private static Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 500, 1000)), cancellationToken);
}
