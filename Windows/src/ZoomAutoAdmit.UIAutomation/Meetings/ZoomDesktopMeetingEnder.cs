using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Meetings;

/// <summary>
/// "End meeting for all" in the Zoom app, as the host, through Zoom's accessibility tree only (no
/// mouse, no keys): the toolbar's End, then the popup's "End meeting for all". "Leave meeting" is
/// never pressed. When the toolbar is hidden (and its End with it), the meeting window is asked to
/// close, which shows the same popup.
/// </summary>
public sealed class ZoomDesktopMeetingEnder
{
    private const string MeetingWindowClass = "ConfMultiTabContentWndClass";
    private static readonly Regex EndButton = new(@"^\s*end(\s+meeting)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EndForAllButton = new(@"^\s*end\s+meeting\s+for\s+all\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public (bool Ended, string Message) EndForAll()
    {
        (bool, string) outcome = (false, "Zoom did not end the meeting.");
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            var meeting = MeetingWindow();
            if (meeting == IntPtr.Zero) { outcome = (true, "The meeting is already over."); return; }
            NativeMethods.GetWindowThreadProcessId(meeting, out uint pid);
            using var automation = new UIA3Automation();

            var end = Buttons(automation, (int)pid).FirstOrDefault(b => EndButton.IsMatch(Name(b)) && b.Patterns.Invoke.IsSupported);
            if (end != null) end.Patterns.Invoke.Pattern.Invoke();
            else NativeMethods.PostMessage(meeting, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

            // The popup: "End meeting for all" / "Leave meeting" - only the first is ever pressed.
            AutomationElement? forAll = null;
            var until = DateTime.UtcNow.AddSeconds(4);
            while (forAll == null && DateTime.UtcNow < until)
            {
                Thread.Sleep(300);
                forAll = Buttons(automation, (int)pid).FirstOrDefault(b => EndForAllButton.IsMatch(Name(b)) && b.Patterns.Invoke.IsSupported);
            }
            if (forAll == null) { outcome = (false, "\"End meeting for all\" did not appear (is this account the host?); nothing else was pressed."); return; }
            forAll.Patterns.Invoke.Pattern.Invoke();

            var gone = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < gone)
            {
                Thread.Sleep(500);
                if (MeetingWindow() == IntPtr.Zero) { outcome = (true, "The meeting was ended for everyone."); return; }
            }
            outcome = (false, "\"End meeting for all\" was pressed but the meeting window is still open.");
        }, 30000);
        return outcome;
    }

    private static IEnumerable<AutomationElement> Buttons(UIA3Automation automation, int processId)
    {
        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint owner);
            if (owner == processId && NativeMethods.IsWindowVisible(h)) windows.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var handle in windows)
        {
            AutomationElement[] found;
            try { found = automation.FromHandle(handle)?.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)) ?? []; }
            catch { continue; }
            foreach (var button in found) yield return button;
        }
    }

    /// <summary>Zoom's meeting window (visible, or hidden while this PC shares its screen), or zero.</summary>
    public static IntPtr MeetingWindow()
    {
        var zoom = Process.GetProcessesByName("Zoom").Select(p => p.Id).ToHashSet();
        if (zoom.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            if (!zoom.Contains((int)pid)) return true;
            var cls = new StringBuilder(128);
            NativeMethods.GetClassName(h, cls, 128);
            if (cls.ToString() == MeetingWindowClass) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Name(AutomationElement element)
    {
        try { return element.Name ?? ""; } catch { return ""; }
    }
}
