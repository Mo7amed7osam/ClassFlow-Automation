using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.WindowsRuntime;

public interface IWindowsDesktopAutoAdmitPreparation
{
    Task PrepareAsync(CancellationToken cancellationToken);
}

internal sealed class NoOpWindowsDesktopAutoAdmitPreparation : IWindowsDesktopAutoAdmitPreparation
{
    public static NoOpWindowsDesktopAutoAdmitPreparation Instance { get; } = new();
    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Best-effort readiness step for the Desktop monitor. Opening Zoom's Participants panel makes
/// people who were already waiting before the monitor started visible to the existing OCR flow,
/// and the same pass turns the microphone and camera off so the host does not join a class live.
/// It never clicks an Admit control and never changes detector or admission policy.
/// The monitor now starts before Zoom has finished opening the meeting, so this step waits for
/// the meeting window first; that wait never delays admission because it runs beside the monitor.
///
/// Muting belongs here rather than only in the launch sequence: the launch tries while Zoom is
/// still drawing the meeting and usually finds no toolbar at all, which is why the microphone
/// stayed on. By the time this step has a meeting window the controls really exist.
/// </summary>
public sealed class WindowsDesktopAutoAdmitPreparation : IWindowsDesktopAutoAdmitPreparation
{
    private static readonly TimeSpan MeetingWindowTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MeetingWindowPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PanelRetryDelay = TimeSpan.FromSeconds(1);
    private const int PanelAttempts = 5;
    private const int MediaAttempts = 8;
    private static readonly TimeSpan MediaRetryDelay = TimeSpan.FromSeconds(1);

    public Task PrepareAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool panelAlreadyOpen = IsParticipantsPanelOpen();
        if (panelAlreadyOpen)
            ConsoleLogger.Info("[AUTO_ADMIT] Initial Waiting Room sweep ready; Participants panel already open");

        try
        {
            if (!WaitForMeetingWindow(cancellationToken))
            {
                ConsoleLogger.Warn("[AUTO_ADMIT] Zoom meeting window did not appear in time; Participants panel not opened, notification monitoring will continue");
                return;
            }

            // The meeting window exists, so its toolbar is reachable: silence the host first.
            EnsureMediaOff(cancellationToken);
            if (panelAlreadyOpen) return;

            // Zoom draws the toolbar shortly after the window exists; retry a few times.
            for (int attempt = 1; attempt <= PanelAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsParticipantsPanelOpen())
                {
                    ConsoleLogger.Success("[AUTO_ADMIT] Initial Waiting Room sweep enabled; Participants panel is open");
                    return;
                }
                TryOpenParticipantsPanel(cancellationToken);
                Thread.Sleep(700);
                if (IsParticipantsPanelOpen())
                {
                    ConsoleLogger.Success("[AUTO_ADMIT] Initial Waiting Room sweep enabled; Participants panel opened");
                    return;
                }
                if (cancellationToken.WaitHandle.WaitOne(PanelRetryDelay)) break;
            }

            ConsoleLogger.Warn("[AUTO_ADMIT] Participants panel could not be opened automatically; notification monitoring will continue");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[AUTO_ADMIT] Participants readiness check failed; notification monitoring will continue: {ex.Message}");
        }
    }, cancellationToken);

    /// <summary>
    /// Turns the microphone and camera off, retrying while Zoom finishes drawing its toolbar.
    /// Each control is read before it is pressed, so a meeting that already joined muted is left
    /// alone rather than being switched back on.
    /// </summary>
    private static void EnsureMediaOff(CancellationToken cancellationToken)
    {
        bool microphoneOff = false;
        bool cameraOff = false;
        for (int attempt = 1; attempt <= MediaAttempts && !(microphoneOff && cameraOff); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!microphoneOff)
                microphoneOff = TurnControlOff("Mute", "Unmute", "microphone", cancellationToken);
            if (!cameraOff)
                cameraOff = TurnControlOff("Stop Video", "Start Video", "camera", cancellationToken);
            if (microphoneOff && cameraOff) break;
            if (cancellationToken.WaitHandle.WaitOne(MediaRetryDelay)) break;
        }

        if (!microphoneOff)
            ConsoleLogger.Warn("[PREPARE] The microphone control never appeared; the microphone was left as Zoom joined it.");
        if (!cameraOff)
            ConsoleLogger.Warn("[PREPARE] The camera control never appeared; the camera was left as Zoom joined it.");
    }

    /// <summary>
    /// True once the control is off: either Zoom already shows the "turn it on" button, or this
    /// call pressed the "turn it off" one.
    /// </summary>
    private static bool TurnControlOff(
        string turnOffName,
        string alreadyOffName,
        string label,
        CancellationToken cancellationToken)
    {
        bool isOff = false;
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
                if (meeting == IntPtr.Zero) return;

                using var automation = new UIA3Automation();
                var root = automation.FromHandle(meeting);
                if (root == null) return;

                var buttons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                if (buttons.Any(button => HasName(button, alreadyOffName)))
                {
                    isOff = true;   // Zoom offers to turn it on, so it is already off.
                    return;
                }

                var turnOff = buttons.FirstOrDefault(button => HasName(button, turnOffName));
                if (turnOff?.Patterns.Invoke.IsSupported != true) return;
                turnOff.Patterns.Invoke.Pattern.Invoke();
                isOff = true;
                ConsoleLogger.Success($"[PREPARE] Zoom Desktop {label} turned off.");
            }, MediaThreadTimeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"[PREPARE] {label} control read failed: {ex.Message}");
        }
        return isOff;
    }

    private static bool HasName(AutomationElement element, string name)
    {
        try { return (element.Name ?? string.Empty).Trim().Equals(name, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private const uint MediaThreadTimeoutMs = 8000;

    private static bool WaitForMeetingWindow(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MeetingWindowTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ZoomWindowManager.FindMainZoomMeetingWindow() != IntPtr.Zero) return true;
            if (cancellationToken.WaitHandle.WaitOne(MeetingWindowPoll)) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static bool TryOpenParticipantsPanel(CancellationToken cancellationToken)
    {
        bool invoked = false;
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = ResolveMeetingProcess();
            if (process == null) return;

            using var automation = new UIA3Automation();
            var button = process.Windows
                .Where(window => window.IsVisible)
                .Select(window => automation.FromHandle(window.Handle))
                .Where(root => root != null)
                .SelectMany(root => root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                .FirstOrDefault(IsClosedParticipantsButton);
            if (button?.Patterns.Invoke.IsSupported != true) return;
            button.Patterns.Invoke.Pattern.Invoke();
            invoked = true;
        }, uint.MaxValue);
        return invoked;
    }

    /// <summary>
    /// The participants button on Zoom's meeting toolbar. Zoom words its accessible name
    /// differently between versions and layouts - "Participants", "Participants (2)",
    /// "open the participants list panel" - so the match is on the word itself, while anything
    /// that would close the panel or belongs to another control is left alone.
    /// </summary>
    internal static bool IsClosedParticipantsButton(AutomationElement element)
    {
        string name;
        try { name = (element.Name ?? string.Empty).Trim(); }
        catch { return false; }
        if (name.Length == 0) return false;
        if (!name.Contains("participant", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("close", StringComparison.OrdinalIgnoreCase)) return false;
        // "Manage participants" settings, invites and mute-all all mention participants too.
        if (name.Contains("invite", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mute", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("setting", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// True when the waiting-room list is reachable: either Zoom detached the Participants window,
    /// or the panel is docked inside the meeting window, where a separate window never exists.
    /// Checking the list itself is what makes this work for both layouts.
    /// </summary>
    private static bool IsParticipantsPanelOpen()
    {
        if (ZoomWindowManager.FindParticipantsWindow() != IntPtr.Zero) return true;

        bool found = false;
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
                if (meeting == IntPtr.Zero) return;
                using var automation = new UIA3Automation();
                var root = automation.FromHandle(meeting);
                if (root == null) return;
                found = root.FindAllDescendants().Any(IsParticipantsListMarker);
            }, PanelProbeTimeoutMs);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"[AUTO_ADMIT] Participants panel probe failed: {ex.Message}");
        }
        return found;
    }

    /// <summary>Text Zoom only shows once the participants list is on screen.</summary>
    private static bool IsParticipantsListMarker(AutomationElement element)
    {
        string name;
        try { name = (element.Name ?? string.Empty).Trim(); }
        catch { return false; }
        return name.StartsWith("Waiting room", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Joined", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Mute all", StringComparison.OrdinalIgnoreCase);
    }

    private const uint PanelProbeTimeoutMs = 8000;

    private static ZoomProcessCandidate? ResolveMeetingProcess()
    {
        var candidates = new ZoomProcessDiscovery().FindCandidates(logInfo: false)
            .Where(candidate =>
                candidate.ProcessName.Equals("Zoom", StringComparison.OrdinalIgnoreCase) ||
                candidate.ProcessName.Equals("CptHost", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
        if (meeting != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(meeting, out uint processId);
            var owner = candidates.FirstOrDefault(candidate => candidate.ProcessId == (int)processId);
            if (owner != null) return owner;
        }
        return candidates.FirstOrDefault(candidate => candidate.Windows.Any(window => window.IsVisible));
    }
}
