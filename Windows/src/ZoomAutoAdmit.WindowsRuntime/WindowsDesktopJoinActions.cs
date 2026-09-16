using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.WindowsRuntime;

/// <summary>Desktop-only launch controls. Does not participate in OCR or Auto Admit.</summary>
internal sealed class WindowsDesktopJoinActions(UIA3Automation automation) : IDesktopMeetingLaunchActions
{
    private const string HomeClass = "ZPPTMainFrmWndClassEx";
    private const string JoinClass = "zWaitingMeetingIDWndClass";
    private int processId;
    private IntPtr home;

    public DesktopLaunchState ReadState()
    {
        var candidates = new ZoomProcessDiscovery().FindCandidates(logInfo: false);
        var homes = candidates.SelectMany(c => c.Windows).Where(w => w.ClassName == HomeClass).ToArray();
        if (homes.Length != 1) return DesktopLaunchState.Unknown;
        if (home != IntPtr.Zero && home != homes[0].Handle) return DesktopLaunchState.Unknown;
        home = homes[0].Handle;
        NativeMethods.GetWindowThreadProcessId(home, out uint pid);
        processId = (int)pid;
        // Includes preview, passcode, connecting, Join and meeting windows. Unknown dialogs
        // are treated as progress, never dismissed to force a second launch.
        if (candidates.SelectMany(c => c.Windows).Any(w => w.Handle != home && w.IsVisible &&
                !IsProfileMenu(w.ClassName) &&
                NativeMethods.GetWindowRect(w.Handle, out var r) && r.Right - r.Left > 100 && r.Bottom - r.Top > 80))
            return DesktopLaunchState.Progress;
        var root = automation.FromHandle(home);
        return root.IsEnabled && Find(root, ControlType.Button, "Join") != null
            ? DesktopLaunchState.Home : DesktopLaunchState.Unknown;
    }

    public void GoHome(CancellationToken cancellation)
    {
        var homes = new ZoomProcessDiscovery().FindCandidates(logInfo: false).SelectMany(c => c.Windows)
            .Where(w => w.ClassName == HomeClass).ToArray();
        if (homes.Length != 1) throw new InvalidOperationException("Zoom's main window was not found.");
        home = homes[0].Handle;
        NativeMethods.GetWindowThreadProcessId(home, out uint pid);
        processId = (int)pid;

        // The account switch leaves its profile menu open over the page; only that menu is closed.
        if (ProfileMenuVisible())
        {
            NativeMethods.ForceForegroundWindow(home);
            Wait(cancellation);
            for (int attempt = 0; attempt < 3 && ProfileMenuVisible(); attempt++)
            {
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint front);
                if (front != processId) break;
                Keyboard.Type(VirtualKeyShort.ESCAPE);
                Wait(cancellation);
            }
        }

        var root = automation.FromHandle(home);
        if (Find(root, ControlType.Button, "Join") != null) return;
        // "Home 1 of 6" in Zoom Workplace's left navigation.
        var tab = root.FindAllDescendants().FirstOrDefault(e =>
            e.Properties.ControlType.ValueOrDefault == ControlType.TabItem &&
            (e.Properties.Name.ValueOrDefault ?? "").TrimStart().StartsWith("Home", StringComparison.OrdinalIgnoreCase) &&
            e.Properties.BoundingRectangle.ValueOrDefault.Width > 0)
            ?? throw new InvalidOperationException("Zoom's Home tab was not found.");
        if (tab.Patterns.SelectionItem.IsSupported) tab.Patterns.SelectionItem.Pattern.Select();
        else if (tab.Patterns.Invoke.IsSupported) tab.Patterns.Invoke.Pattern.Invoke();
        else { NativeMethods.ForceForegroundWindow(home); Wait(cancellation); Click(tab); }

        for (int attempt = 0; attempt < 30; attempt++)
        {
            Wait(cancellation);
            if (Find(automation.FromHandle(home), ControlType.Button, "Join") != null)
            {
                ConsoleLogger.Info("[MEETING_LINK] Zoom's Home page is open");
                return;
            }
        }
        throw new InvalidOperationException("Zoom's Home page did not show its Join button.");
    }

    public void OpenLink(Uri url) => Process.Start(new ProcessStartInfo(
        WindowsDesktopMeetingPlatform.CreateZoomDesktopProtocolUrl(url)) { UseShellExecute = true });

    public void JoinById(string meetingId, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        NativeMethods.ForceForegroundWindow(home);
        Wait(cancellation);
        // Account switching deliberately leaves its verification menu visible. Dismiss only
        // those known profile menus; never dismiss a preview/passcode/meeting dialog.
        for (int attempt = 0; attempt < 3 && ProfileMenuVisible(); attempt++)
        {
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
            if (pid != processId) throw new InvalidOperationException("Zoom lost foreground; no key sent.");
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Wait(cancellation);
        }
        if (ReadState() != DesktopLaunchState.Home)
            throw new InvalidOperationException("Zoom changed state before fallback; no second launch sent.");
        var join = Find(automation.FromHandle(home), ControlType.Button, "Join")
            ?? throw new InvalidOperationException("Zoom home Join button not found.");
        Click(join);
        var dialog = WaitForDialog(cancellation);
        ConsoleLogger.Info("[MEETING_JOIN_FALLBACK] Join dialog detected");
        var edit = Find(dialog, ControlType.Edit, "Meeting ID or personal link name")
            ?? throw new InvalidOperationException("Meeting ID field not found in Join dialog.");
        if (!edit.Patterns.Value.IsSupported)
            throw new InvalidOperationException("Meeting ID field has no accessible Value pattern.");
        edit.Patterns.Value.Pattern.SetValue(meetingId);
        string actual = edit.Patterns.Value.Pattern.Value.Value.Replace(" ", string.Empty);
        if (actual != meetingId) throw new InvalidOperationException("Meeting ID could not be verified; Join not clicked.");
        SetChecked(dialog, "Don't connect to audio", cancellation);
        SetChecked(dialog, "Turn off my video", cancellation);
        ConsoleLogger.Info("[MEETING_JOIN_FALLBACK] Audio disconnected and video off verified");
        cancellation.ThrowIfCancellationRequested();
        // Re-check all preconditions on the current dialog immediately before submitting.
        dialog = WaitForDialog(cancellation);
        if (new ZoomProcessDiscovery().FindCandidates(logInfo: false).SelectMany(c => c.Windows)
            .Any(w => w.Handle != home && w.ClassName != JoinClass && !IsProfileMenu(w.ClassName) && w.IsVisible &&
                NativeMethods.GetWindowRect(w.Handle, out var r) && r.Right - r.Left > 100 && r.Bottom - r.Top > 80))
            throw new InvalidOperationException("Another Zoom dialog appeared during fallback; Join was not submitted.");
        if (Find(dialog, ControlType.Edit, "Meeting ID or personal link name")?.Patterns.Value.Pattern.Value.Value.Replace(" ", "") != meetingId ||
            !IsChecked(dialog, "Don't connect to audio") || !IsChecked(dialog, "Turn off my video"))
            throw new InvalidOperationException("Join settings changed; no submission sent.");
        Click(Find(dialog, ControlType.Button, "Join")
            ?? throw new InvalidOperationException("Join button missing in dialog."));
    }

    private AutomationElement WaitForDialog(CancellationToken cancellation)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == processId && NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetClassNameSafe(hwnd) == JoinClass)
                    found = hwnd;
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return automation.FromHandle(found);
            Wait(cancellation);
        }
        throw new InvalidOperationException("Zoom Join dialog did not appear.");
    }

    private static bool IsProfileMenu(string name) => name is "ZPPTMainMenuWndClass" or "ZPPTSwitchAccountSubMenuWndClass";

    private bool ProfileMenuVisible()
    {
        bool visible = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId && NativeMethods.IsWindowVisible(hwnd) && IsProfileMenu(NativeMethods.GetClassNameSafe(hwnd)))
                visible = true;
            return true;
        }, IntPtr.Zero);
        return visible;
    }

    private void SetChecked(AutomationElement dialog, string name, CancellationToken cancellation)
    {
        var box = Find(dialog, ControlType.CheckBox, name)
            ?? throw new InvalidOperationException($"Required Join option missing: {name}");
        if (!box.Patterns.Toggle.IsSupported) throw new InvalidOperationException($"Cannot read Join option state: {name}");
        if (box.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On) Click(box);
        Wait(cancellation);
        if (!IsChecked(dialog, name)) throw new InvalidOperationException($"Join option not enabled: {name}");
    }

    private static bool IsChecked(AutomationElement dialog, string name)
    {
        var box = Find(dialog, ControlType.CheckBox, name);
        return box != null && box.Patterns.Toggle.IsSupported && box.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
    }

    private void Click(AutomationElement element)
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        if (pid != processId) throw new InvalidOperationException("Zoom lost foreground; no click sent.");
        var r = element.BoundingRectangle;
        if (!element.IsEnabled || element.IsOffscreen || r.Width <= 0 || r.Height <= 0)
            throw new InvalidOperationException("Target control is not enabled and visible.");
        Mouse.Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
    }

    private static AutomationElement? Find(AutomationElement root, ControlType type, string name) =>
        root.FindAllDescendants().Where(e => e.Properties.ControlType.ValueOrDefault == type &&
            e.Properties.Name.ValueOrDefault?.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) == true &&
            e.Properties.BoundingRectangle.ValueOrDefault.Width > 0).SingleOrDefault();

    public void Wait(CancellationToken cancellation)
    {
        if (cancellation.WaitHandle.WaitOne(500)) cancellation.ThrowIfCancellationRequested();
    }
}
