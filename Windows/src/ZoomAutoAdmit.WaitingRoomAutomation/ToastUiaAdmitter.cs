using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

/// <summary>
/// Presses Admit on a Waiting Room notification through UI Automation.
///
/// The screen-clicking path has to move the real cursor onto the toast, which fails whenever the
/// person is using the mouse, the toast sits on a monitor the capture missed, or another window
/// covers it. Invoking the button through the accessibility tree needs none of that: it works
/// while the user keeps typing on another screen, and it never moves their pointer.
///
/// Zoom shows the arrival two ways and both are handled here: its own floating toast
/// (zMeetingNotificationWndClass) and, when Zoom is not in the foreground, the Windows
/// notification card, which belongs to the shell rather than to Zoom.
/// </summary>
internal sealed class ToastUiaAdmitter
{
    private const uint UiaTimeoutMs = 10000;
    // zoom_acc_notify_wnd is deliberately absent: it is the window Zoom writes announcements to
    // for screen readers. Its text says somebody entered the waiting room, but it holds no
    // buttons at all, so treating it as a notification found the arrival and could never act on
    // it - every pass rediscovered the same text and admitted nobody.
    private static readonly string[] ZoomToastClasses =
    [
        "zMeetingNotificationWndClass",
        "ZPNotificationWndClass"
    ];
    private static readonly string[] ShellNotificationProcesses =
    [
        "explorer", "ShellExperienceHost", "ShellHost"
    ];
    private static readonly string[] AdmitNames = ["Admit", "Admit all", "Admit All"];
    private static readonly string[] ViewNames = ["View"];

    private readonly WaitingRoomSessionLog _log;
    private readonly ActionExecutor _actions;

    public ToastUiaAdmitter(Guid sessionId, IWaitingRoomLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _log = new WaitingRoomSessionLog(logger, sessionId, SessionEngineType.Desktop);
        _actions = new ActionExecutor(logger, sessionId, SessionEngineType.Desktop);
    }

    /// <summary>What a pass over the visible notifications managed to do.</summary>
    internal enum ToastOutcome
    {
        /// <summary>No waiting-room notification was on screen.</summary>
        NoNotification,
        /// <summary>Admit was pressed; the person is in the meeting.</summary>
        Admitted,
        /// <summary>
        /// The notification had no Admit to press, so View was pressed instead. Zoom opens the
        /// Participants panel, where the panel flow admits from the row on a later pass.
        /// </summary>
        OpenedParticipants
    }

    internal sealed record ToastAdmitResult(ToastOutcome Outcome, string Participant);

    /// <summary>
    /// Presses Admit on a visible arrival notification, or View when Zoom shrank the notification
    /// and left no Admit on it. Nothing is clicked with the mouse, so it works while the person is
    /// using the pointer or looking at another screen.
    /// </summary>
    public ToastAdmitResult TryAdmit(CancellationToken cancellationToken)
    {
        var result = new ToastAdmitResult(ToastOutcome.NoNotification, string.Empty);
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var automation = new UIA3Automation();
                foreach (IntPtr handle in NotificationWindows())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AutomationElement? root;
                    try { root = automation.FromHandle(handle); }
                    catch { continue; }
                    if (root == null) continue;

                    string text = ReadText(root);
                    if (!MentionsWaitingRoom(text)) continue;

                    // Say nothing until there is something to press: a notification with no
                    // controls is worth skipping quietly, not repeating every second.
                    var admit = FindNamedButton(root, AdmitNames);
                    var viewButton = FindNamedButton(root, ViewNames);
                    if (admit == null && viewButton == null) continue;

                    string participant = ParticipantNameFrom(text);
                    _log.Desktop($"[TOAST_UIA] Waiting Room notification found for '{participant}' (HWND=0x{handle.ToInt64():X})");
                    if (admit != null && _actions.InvokeDesktopElementUiaOnly(admit, "Admit"))
                    {
                        result = new ToastAdmitResult(ToastOutcome.Admitted, participant);
                        return;
                    }

                    // Zoom collapses the notification when its own meeting controls sit on top of
                    // it, leaving only View. View opens the Participants panel, which carries the
                    // same person with an Admit that is always reachable.
                    if (viewButton != null && _actions.InvokeDesktopElementUiaOnly(viewButton, "View"))
                    {
                        _log.Desktop($"[TOAST_UIA] No Admit on the notification for '{participant}'; opened the Participants panel with View");
                        result = new ToastAdmitResult(ToastOutcome.OpenedParticipants, participant);
                        return;
                    }
                }
            }, UiaTimeoutMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Error($"[TOAST_UIA] Notification admit through UI Automation failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>Zoom's own toast windows plus the shell's notification host, newest first.</summary>
    private static IReadOnlyList<IntPtr> NotificationWindows()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            string className = NativeMethods.GetClassNameSafe(hWnd);
            string process = NativeMethods.GetProcessNameSafe(hWnd);
            bool zoomToast = ZoomToastClasses.Any(candidate =>
                className.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            bool shellToast = ShellNotificationProcesses.Any(candidate =>
                process.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (zoomToast || shellToast) handles.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private static bool MentionsWaitingRoom(string text) =>
        text.Contains("waiting room", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name Zoom puts before "entered the waiting room".</summary>
    private static string ParticipantNameFrom(string text)
    {
        const string marker = "entered the waiting room";
        int index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index <= 0) return "(unknown)";
        string name = text[..index].Trim();
        // The card repeats "Zoom" as its app title above the message.
        if (name.StartsWith("Zoom ", StringComparison.OrdinalIgnoreCase)) name = name[5..].Trim();
        return name.Length == 0 ? "(unknown)" : name;
    }

    private static AutomationElement? FindNamedButton(AutomationElement root, string[] names)
    {
        foreach (var element in Descendants(root))
        {
            var type = SafeControlType(element);
            if (type != ControlType.Button && type != ControlType.MenuItem) continue;
            if (!SafeIsEnabled(element)) continue;
            string name = NameOf(element);
            if (names.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return element;
        }
        return null;
    }

    private static string ReadText(AutomationElement root)
    {
        var parts = new List<string>();
        foreach (var element in Descendants(root))
        {
            string name = NameOf(element);
            if (name.Length > 0) parts.Add(name);
            if (parts.Count > 60) break;
        }
        return string.Join(' ', parts);
    }

    private static ControlType SafeControlType(AutomationElement element)
    {
        try { return element.Properties.ControlType.ValueOrDefault; }
        catch { return ControlType.Custom; }
    }

    private static bool SafeIsEnabled(AutomationElement element)
    {
        try { return element.Properties.IsEnabled.ValueOrDefault; }
        catch { return false; }
    }

    private static string NameOf(AutomationElement element)
    {
        try
        {
            string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            if (element.Patterns.LegacyIAccessible.IsSupported)
                return (element.Patterns.LegacyIAccessible.Pattern.Name.ValueOrDefault ?? string.Empty).Trim();
        }
        catch { }
        return string.Empty;
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root)
    {
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0)
        {
            // A notification card is a small tree; the cap only guards against a shell host that
            // exposes far more than the card itself.
            if (visited++ > 400) yield break;
            var element = stack.Pop();
            yield return element;
            AutomationElement[] children;
            try { children = element.FindAllChildren(); }
            catch { continue; }
            foreach (var child in children) stack.Push(child);
        }
    }
}
