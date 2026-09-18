using System.Text.RegularExpressions;
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
    // Zoom draws its toolbar late, and a class that joined unmuted stayed unmuted after 8 seconds of
    // trying (S7, 2026-09-18): it is now watched for two minutes, and stops the moment both are off.
    private const int MediaAttempts = 60;
    private static readonly TimeSpan MediaRetryDelay = TimeSpan.FromSeconds(2);

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
                microphoneOff = TurnControlOff(MuteButton, UnmuteButton, "microphone", cancellationToken);
            if (!cameraOff)
                cameraOff = TurnControlOff(StopVideoButton, StartVideoButton, "camera", cancellationToken);
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
    // Zoom words the host's own toolbar buttons differently between versions: "Mute", "Mute my
    // microphone (Alt+A)", "Unmute my audio (Alt+A). Or you can simply press and hold the Space
    // bar…", "Start my video, Alt+V". Matching the exact word left the host unmuted in a real class
    // (S7, 2026-09-18). The participants list has buttons of its own with the very same short names
    // - "Mute", "Unmute", "Start video" belong to a STUDENT's row, and "Mute all" to the panel - so
    // a name that says whose control it is ("my audio", "Alt+A") is taken first, and a bare name is
    // only believed when it is not inside the participants list.
    public static readonly Regex MuteButton = new(@"^mute(\s+(my\s+)?(audio|microphone|mic))?\b(?!\s*all)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex UnmuteButton = new(@"^unmute\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex StopVideoButton = new(@"^stop(\s+my)?\s+video\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex StartVideoButton = new(@"^start(\s+my)?\s+video\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>A name that says the control is the host's own, whoever else is in the list.</summary>
    public static readonly Regex MyOwnControl = new(@"\bmy\s+(audio|microphone|mic|video)\b|\balt\+[av]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TurnControlOff(
        Regex turnOffName,
        Regex alreadyOffName,
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

                // The host's own controls: never a button inside the participants list, which is a
                // student's row.
                var buttons = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .Where(button => !InParticipantsList(button))
                    .ToArray();
                if (Pick(buttons, alreadyOffName) != null)
                {
                    isOff = true;   // Zoom offers to turn it on, so it is already off.
                    return;
                }

                var turnOff = Pick(buttons, turnOffName);
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

    /// <summary>The host's own button: one that names itself as theirs first, else a plain one.</summary>
    private static AutomationElement? Pick(IEnumerable<AutomationElement> buttons, Regex name)
    {
        var matching = buttons.Where(button => HasName(button, name)).ToArray();
        return matching.FirstOrDefault(button => MyOwnControl.IsMatch(SafeName(button))) ?? matching.FirstOrDefault();
    }

    /// <summary>A button belonging to a row of the participants list rather than to the toolbar.</summary>
    private static bool InParticipantsList(AutomationElement element)
    {
        try
        {
            var walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
            for (var parent = walker.GetParent(element); parent != null; parent = walker.GetParent(parent))
            {
                var type = parent.Properties.ControlType.ValueOrDefault;
                if (type == ControlType.List || type == ControlType.ListItem) return true;
                if (type == ControlType.Window) return false;
            }
        }
        catch { }
        return false;
    }

    private static string SafeName(AutomationElement element)
    {
        try { return (element.Name ?? string.Empty).Trim(); }
        catch { return string.Empty; }
    }

    private static bool HasName(AutomationElement element, Regex name)
    {
        try { return name.IsMatch(SafeName(element)); }
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
