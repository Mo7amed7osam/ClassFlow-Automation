using System.Collections.Concurrent;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Infrastructure;

namespace ZoomAutoAdmit.WindowsUI.Services;

public sealed class WindowsUiService : IWindowsUiService, IAttendanceUiActions, IMeetingActivitySource, IAsyncDisposable
{
    private readonly WindowsRuntimeBootstrapper _bootstrapper;
    private readonly MeetingActivityFeed _activity;
    private readonly ConcurrentDictionary<Guid, MeetingSession> _sessions = new();
    // Starts in flight. A session spends a minute or more in Starting, and Stop has to reach it
    // there: until the run returns there is no MeetingSession to end.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _starting = new();
    /// <summary>The accounts whose meeting is being opened right now, so one is not opened twice.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _openingAccounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _statusSync = new();
    private UiActionStatus _currentStatus = new("Application startup", "Ready", string.Empty, false, DateTimeOffset.Now);

    public WindowsUiService(WindowsRuntimeBootstrapper bootstrapper)
    {
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        _activity = new MeetingActivityFeed(bootstrapper.LifecycleEvents);
        _bootstrapper.Scheduler.SessionStarted += OnScheduledSessionStarted;
        _bootstrapper.LifecycleEvents.Lifecycle += OnMeetingLifecycleAsync;
        ConsoleLogger.EntryWritten += OnRuntimeLogEntry;
        _bootstrapper.Scheduler.Start();
    }

    public event Action<UiActionStatus>? StatusChanged;
    public event Action<LiveMeeting>? MeetingBecameLive;

    /// <summary>
    /// The runtime raises Active for a meeting started from the window and for one the scheduler
    /// opened, which is why this is the place to notice a class has begun.
    /// </summary>
    private Task OnMeetingLifecycleAsync(MeetingLifecycleEvent message)
    {
        if (message.Kind != MeetingLifecycleEventKind.Active) return Task.CompletedTask;
        var session = message.Context.Session;
        try { MeetingBecameLive?.Invoke(new LiveMeeting(session.GroupId, session.StartTime)); }
        catch (Exception ex) { WindowsUiRuntimeLog.Write("LMS", $"Meeting-live observer failed: {ex.Message}"); }
        return Task.CompletedTask;
    }
    public IReadOnlyList<MeetingActivity> GetMeetingActivity() => _activity.GetMeetingActivity();
    public async Task<UiOperationResult> CaptureAttendanceAsync(Guid sessionId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!_bootstrapper.SessionCoordinator.ActiveSessions.Any(s => s.SessionId == sessionId))
            return new(false, "Session has ended. Its saved snapshots remain available.");
        await _bootstrapper.Attendance.CaptureManualAsync(sessionId).WaitAsync(token);
        return new(true, "Capture request completed. Refresh snapshots; check the latest timestamp and capture status in logs.");
    }
    public Task<IReadOnlyList<Guid>> GetActiveSessionIdsAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Guid>>(
            _bootstrapper.SessionCoordinator.ActiveSessions.Select(session => session.SessionId).ToArray());
    }
    /// <summary>Lets the session-role module ask the AI to confirm names the local rules cannot settle.</summary>
    public void EnableSessionRoleAi(ZoomAutoAdmit.SessionRoles.IRoleAiMatcher matcher) =>
        _bootstrapper.SessionRoles.AiMatcher = matcher;
    /// <summary>Co-host outcomes worth showing on the desktop. Raised off the UI thread.</summary>
    public event Action<ZoomAutoAdmit.SessionRoles.SessionRoleNotice>? SessionRoleNotice
    {
        add => _bootstrapper.SessionRoles.Notice += value;
        remove => _bootstrapper.SessionRoles.Notice -= value;
    }
    public UiActionStatus CurrentStatus { get { lock (_statusSync) return _currentStatus; } }

    public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(
        CancellationToken cancellationToken = default) =>
        _bootstrapper.AccountManager.ListConfiguredAsync(cancellationToken);

    public Task SaveAccountAsync(
        WindowsMeetingAccountMetadata account,
        CancellationToken cancellationToken = default) =>
        _bootstrapper.AccountManager.UpsertAsync(account, cancellationToken);

    public Task<bool> DeleteAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        _bootstrapper.AccountManager.DeleteAsync(accountId, cancellationToken);

    public async Task<UiOperationResult> SwitchAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ConsoleLogger.Info("[DEBUG_SWITCH] Entered SwitchAccountAsync");
        Report("Switch account", "Switching account...", string.Empty, true);
        try
        {
            var account = await _bootstrapper.AccountManager.LoadAsync(accountId, cancellationToken);
            if (account == null)
                return Fail("Switch account", "Account could not be loaded or its credential reference could not be resolved.");
            string zoomIdentity = account.ZoomEmail ?? WindowsCredentialManagerReferenceResolver.TryGetUsername(account.CredentialReference) ?? "(unresolved)";
            ConsoleLogger.Info($"[DEBUG_SWITCH] Target account object: AccountId='{account.AccountId}', DisplayName='{account.DisplayName}', CredentialReference='{account.CredentialReference}', PreferredEngine='{account.PreferredEngine?.ToString() ?? "Auto"}', ZoomIdentity='{zoomIdentity}'");
            Report("Switch account", $"Switching {account.AccountId} to {zoomIdentity}...", string.Empty, true);
            var runtime = _bootstrapper.RuntimeFactory.Get(SessionEngineType.Desktop);
            var result = await Task.Run(
                () => runtime.SwitchAccountAsync(account, cancellationToken),
                cancellationToken);
            if (!result.IsSuccess)
                return Fail("Switch account", result.ErrorMessage ?? "Zoom Desktop account switching failed.");
            string message = $"{account.AccountId}: Zoom Desktop account verified: {zoomIdentity}.";
            Report("Switch account", message, string.Empty, false);
            return new UiOperationResult(true, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail("Switch account", "Account switching was cancelled.");
        }
        catch (Exception ex) { return Fail("Switch account", ex.Message); }
    }

    public async Task<SessionDisplayInfo> StartMeetingAsync(
        string accountId,
        string meetingUrl,
        EnginePreference preference,
        CancellationToken cancellationToken = default)
    {
        Uri url = ZoomWebMeetingController.ValidateMeetingUrl(meetingUrl);
        Report("Start meeting", "Loading account and requesting meeting start...", string.Empty, true);
        SessionEngineType? engine = preference switch
        {
            EnginePreference.Desktop => SessionEngineType.Desktop,
            EnginePreference.Web => SessionEngineType.Web,
            _ => null
        };
        // A class that is already open is never opened a second time. Pressing Start again took
        // the same browser profile and closed the meeting that was running on it (2026-09-23,
        // S8 at 20:10, Start pressed twice within half a second).
        var known = (await GetAccountsAsync(cancellationToken)).FirstOrDefault(account =>
            account.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
        string group = known?.GroupName is { Length: > 0 } named ? named : accountId;
        if (LiveMeetings.IsLive(group))
        {
            string already = $"{group} is already open on this PC. Stop it first, or leave it running.";
            Report("Start meeting", "Not opened twice", already, false);
            throw new InvalidOperationException(already);
        }
        if (!_openingAccounts.TryAdd(accountId, DateTimeOffset.Now))
        {
            string opening = $"{group} is already being opened; give it a moment.";
            Report("Start meeting", "Not opened twice", opening, false);
            throw new InvalidOperationException(opening);
        }

        // Choosing the id here, rather than letting the orchestrator invent one, is what lets Stop
        // find and cancel this run while it is still starting.
        Guid sessionId = Guid.NewGuid();
        var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _starting[sessionId] = startCancellation;
        MeetingSession session;
        try
        {
            string groupId = known?.GroupName ?? accountId;
            // The orchestration contains synchronous UI Automation and keyboard work (account
            // switch, join checks, mic/camera) that can take tens of seconds. Awaiting it directly
            // resumes every step on the WPF dispatcher thread and freezes the window ("not
            // responding"), so the whole run stays on the thread pool, like SwitchAccountAsync.
            session = await Task.Run(
                () => _bootstrapper.Orchestrator.RunAsync(
                    new ScheduledMeeting(
                        url,
                        accountId,
                        DateTimeOffset.UtcNow,
                        SessionId: sessionId,
                        PreferredEngine: engine,
                        GroupId: groupId),
                    startCancellation.Token),
                startCancellation.Token);
        }
        catch (OperationCanceledException) when (startCancellation.IsCancellationRequested)
        {
            _bootstrapper.SessionCoordinator.Release(sessionId);
            Report("Start meeting", "Meeting start was stopped before it finished.", string.Empty, false);
            throw new OperationCanceledException("Meeting start was stopped before it finished.");
        }
        finally
        {
            _starting.TryRemove(sessionId, out _);
            _openingAccounts.TryRemove(accountId, out _);
            startCancellation.Dispose();
        }

        if (session.State == MeetingState.Failed)
        {
            string reason = session.FailureReason ?? "Meeting startup failed.";
            _bootstrapper.SessionCoordinator.Release(session.SessionId);
            Fail("Start meeting", reason);
            throw new InvalidOperationException(reason);
        }
        _sessions[session.SessionId] = session;
        var display = await ToDisplayInfoAsync(session, cancellationToken);
        Report("Start meeting", $"{accountId}: meeting started using {display.EngineType}.", string.Empty, false);
        return display;
    }

    public async Task<bool> StopMeetingAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        // A run that has not finished starting owns no MeetingSession yet. Cancelling it is the
        // only way to stop it; its own cleanup releases the engine reservation.
        if (_starting.TryGetValue(sessionId, out var startCancellation))
        {
            Report("Stop meeting", "Stopping a meeting that is still starting...", string.Empty, true);
            try { startCancellation.Cancel(); } catch (ObjectDisposedException) { }
            for (int attempt = 0; attempt < 40 && _starting.ContainsKey(sessionId); attempt++)
                await Task.Delay(250, cancellationToken);
            _bootstrapper.SessionCoordinator.Release(sessionId);
            _sessions.TryRemove(sessionId, out _);
            Report("Stop meeting", "The starting meeting was stopped.", string.Empty, false);
            return true;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            // The reservation can outlive its MeetingSession (a scheduled run, or a start that
            // failed after allocating). Releasing it is what frees the Desktop engine again.
            bool released = _bootstrapper.SessionCoordinator.Release(sessionId);
            if (released) Report("Stop meeting", "Released a session reservation with no live monitor.", string.Empty, false);
            return released;
        }

        Report("Stop meeting", "Stopping the meeting...", string.Empty, true);
        bool stopped = await Task.Run(
            () => _bootstrapper.Orchestrator.EndAsync(session, cancellationToken),
            cancellationToken);
        if (stopped) _sessions.TryRemove(sessionId, out _);
        Report("Stop meeting", stopped ? "Meeting stopped." : "The meeting could not be stopped.", string.Empty, false);
        return stopped;
    }

    public async Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var configuredAccounts = await GetAccountsAsync(cancellationToken);
        var accountNames = configuredAccounts.ToDictionary(
            account => account.AccountId,
            account => account.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        var inThisApp = _bootstrapper.SessionCoordinator.ActiveSessions.Select(active =>
        {
            _sessions.TryGetValue(active.SessionId, out var meeting);
            return new SessionDisplayInfo(
                active.SessionId,
                active.AccountId,
                accountNames.GetValueOrDefault(active.AccountId, active.AccountId),
                active.EngineType,
                meeting?.State.ToString() ?? active.Status.ToString(),
                // Stored in UTC; the page shows this PC's clock (19:31, not 16:31).
                active.StartTime.ToLocalTime());
        }).ToList();
        // Meetings running in another process on this PC (a class a Windows task started) are shown too.
        foreach (var live in ZoomAutoAdmit.Core.Meetings.LiveMeetings.List())
        {
            if (inThisApp.Any(session => session.SessionId == live.SessionId)) continue;
            inThisApp.Add(new SessionDisplayInfo(
                live.SessionId,
                live.Group,
                accountNames.GetValueOrDefault(live.Group, live.Group),
                Enum.TryParse<SessionEngineType>(live.Engine, out var engine) ? engine : SessionEngineType.Desktop,
                "Running (started outside the app)",
                live.StartedAt.ToLocalTime()));
        }
        return inThisApp;
    }

    public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(
        CancellationToken cancellationToken = default) =>
        _bootstrapper.ScheduleStore.ListAsync(cancellationToken);

    public Task SaveScheduleAsync(
        MeetingSchedule schedule,
        CancellationToken cancellationToken = default) =>
        _bootstrapper.ScheduleStore.UpsertAsync(schedule, cancellationToken);

    public Task<bool> DeleteScheduleAsync(
        Guid scheduleId,
        CancellationToken cancellationToken = default) =>
        _bootstrapper.ScheduleStore.DeleteAsync(scheduleId, cancellationToken);

    private void OnScheduledSessionStarted(MeetingSession session) =>
        CompleteScheduledSession(session);

    private void CompleteScheduledSession(MeetingSession session)
    {
        _sessions[session.SessionId] = session;
        Report("Scheduled meeting", "Meeting started successfully.", string.Empty, false);
    }

    private static readonly string[] RuntimeLogCategories =
        ["ATTENDANCE", "ROLE", "COHOST", "AUTO_ADMIT", "MEETING", "SESSION", "ALLOCATOR", "PREPARE", "MATCHING", "LMS", "ADMISSION", "ERROR",
         // What the coordinators' sync, the accounts, the recordings and the notices did: without
         // them a coordinator turned off whose classes stayed left no trace (2026-09-26).
         "RUNS", "ACCOUNTS", "RECORDING", "RECORDINGS", "REPORT", "NOTIFY", "SCHEDULER", "SESSIONS"];

    private void OnRuntimeLogEntry(LogEntry entry)
    {
        string message = entry.Message;
        if (message.StartsWith("[KEYBOARD_SWITCH]", StringComparison.Ordinal))
        {
            WindowsUiRuntimeLog.Write("KEYBOARD_SWITCH", message);
            if (CurrentStatus.LastAction == "Switch account" && CurrentStatus.IsBusy)
                Report("Switch account", message["[KEYBOARD_SWITCH]".Length..].Trim(), string.Empty, true);
        }
        if (message.StartsWith("[DEBUG_SWITCH]", StringComparison.Ordinal))
            WindowsUiRuntimeLog.Write("DEBUG_SWITCH", message);
        if (message.StartsWith("WEB_SIGN_IN", StringComparison.Ordinal))
            WindowsUiRuntimeLog.Write("WEB_SIGN_IN", message);
        // These ran only on the console before. Without them in the runtime log, a meeting that
        // silently admits nobody or assigns no co-host leaves no evidence at all.
        foreach (var category in RuntimeLogCategories)
            if (message.StartsWith($"[{category}]", StringComparison.Ordinal))
            {
                WindowsUiRuntimeLog.Write(category, message);
                break;
            }
        if (message.StartsWith("[SCHEDULER] Triggering:", StringComparison.Ordinal))
            Report("Scheduled meeting", $"Schedule detected: {message["[SCHEDULER] Triggering:".Length..].Trim()}", string.Empty, true);
        else if (message.StartsWith("[ACCOUNT] Loaded:", StringComparison.Ordinal) && CurrentStatus.LastAction == "Scheduled meeting")
            Report("Scheduled meeting", $"Account loaded: {message["[ACCOUNT] Loaded:".Length..].Trim()}", string.Empty, true);
        else if (message.StartsWith("[SESSION] Created:", StringComparison.Ordinal) && CurrentStatus.LastAction == "Scheduled meeting")
            Report("Scheduled meeting", $"Session created: {message["[SESSION] Created:".Length..].Trim()}", string.Empty, true);
        else if ((message == "[MEETING] Launching" || message == "[MEETING] Joining") && CurrentStatus.LastAction == "Scheduled meeting")
            Report("Scheduled meeting", "Meeting start requested.", string.Empty, true);
        else if ((message.StartsWith("[SCHEDULER] Failed:", StringComparison.Ordinal) ||
                  message.StartsWith("[SESSION] Failed:", StringComparison.Ordinal)) &&
                 CurrentStatus.LastAction == "Scheduled meeting")
            Fail("Scheduled meeting", message[(message.IndexOf(':') + 1)..].Trim());
        else if (message.StartsWith("[ACCOUNT_SWITCH]", StringComparison.Ordinal) && CurrentStatus.LastAction == "Switch account")
        {
            if (message.Contains("Opening profile menu", StringComparison.OrdinalIgnoreCase))
                Report("Switch account", "Opening Zoom profile menu...", string.Empty, true);
            else if (message.Contains("Selecting account", StringComparison.OrdinalIgnoreCase))
                Report("Switch account", "Selecting account...", string.Empty, true);
        }
    }

    private UiOperationResult Fail(string action, string error)
    {
        Report(action, "Failed", error, false);
        return new UiOperationResult(false, error);
    }

    private void Report(string action, string operation, string error, bool isBusy)
    {
        var status = new UiActionStatus(action, operation, error, isBusy, DateTimeOffset.Now);
        lock (_statusSync) _currentStatus = status;
        WindowsUiRuntimeLog.Write("ACTION", $"{action} | {operation} | {error}");
        StatusChanged?.Invoke(status);
    }

    private async Task<SessionDisplayInfo> ToDisplayInfoAsync(
        MeetingSession session,
        CancellationToken cancellationToken)
    {
        var account = (await GetAccountsAsync(cancellationToken)).FirstOrDefault(candidate =>
            candidate.AccountId.Equals(session.AccountId, StringComparison.OrdinalIgnoreCase));
        return new SessionDisplayInfo(
            session.SessionId,
            session.AccountId,
            account?.DisplayName ?? session.AccountId,
            session.Allocation!.EngineType,
            session.State.ToString(),
            session.StartTime);
    }

    public async ValueTask DisposeAsync()
    {
        _activity.Dispose();
        _bootstrapper.Scheduler.SessionStarted -= OnScheduledSessionStarted;
        _bootstrapper.LifecycleEvents.Lifecycle -= OnMeetingLifecycleAsync;
        ConsoleLogger.EntryWritten -= OnRuntimeLogEntry;
        await _bootstrapper.Scheduler.StopAsync();
        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            try { await StopMeetingAsync(sessionId); }
            catch { }
        }
        await _bootstrapper.DisposeAsync();
    }
}
