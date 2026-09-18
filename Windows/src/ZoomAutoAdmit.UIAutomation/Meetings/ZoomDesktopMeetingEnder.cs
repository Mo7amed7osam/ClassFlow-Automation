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
    // Zoom Workplace writes its own shortcut into the name ("End meeting, Alt+Q", "End Meeting for
    // All"), so each control is recognised by how its name begins, never by the whole name - matching
    // the whole name is why a real class (S7, 2026-09-18) was left open twice. "End" must not match
    // "End breakout rooms" or anything that only mentions ending.
    public static readonly Regex EndButton = new(@"^\s*end(\s+(the\s+)?(meeting|session|class|webinar))?\b(?!\s*(for\s+all|breakout))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly Regex EndForAllButton = new(@"^\s*end\s+(the\s+)?(meeting|session|class|webinar)?\s*for\s+all\b|^\s*end\s+for\s+all\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Never pressed, whatever happens: leaving hands the meeting to somebody else.</summary>
    public static readonly Regex LeaveButton = new(@"^\s*leave\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public (bool Ended, string Message) EndForAll()
    {
        (bool, string) outcome = (false, "Zoom did not end the meeting.");
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            var meeting = MeetingWindow();
            if (meeting == IntPtr.Zero) { outcome = (true, "The meeting is already over."); return; }
            NativeMethods.GetWindowThreadProcessId(meeting, out uint pid);
            using var automation = new UIA3Automation();

            var end = Choices(automation, (int)pid).FirstOrDefault(c => EndButton.IsMatch(Name(c)));
            if (end == null || !Press(end)) NativeMethods.PostMessage(meeting, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

            // The popup: "End meeting for all" / "Leave meeting" - only the first is ever pressed. It
            // is a button in some versions and a menu item in others, so both are looked at, and it is
            // given long enough for a window that draws slowly.
            AutomationElement? forAll = null;
            var until = DateTime.UtcNow.AddSeconds(10);
            while (forAll == null && DateTime.UtcNow < until)
            {
                Thread.Sleep(300);
                forAll = Choices(automation, (int)pid).FirstOrDefault(c => EndForAllButton.IsMatch(Name(c)));
            }
            if (forAll == null)
            {
                // What Zoom did show, so the next version's wording can be read from the log.
                string seen = string.Join(" | ", Choices(automation, (int)pid).Select(Name)
                    .Where(n => n.Length is > 0 and < 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(12));
                outcome = (false, $"\"End meeting for all\" did not appear (is this account the host?); nothing else was pressed. Zoom offered: {seen}");
                return;
            }
            if (!Press(forAll)) { outcome = (false, "\"End meeting for all\" could not be pressed."); return; }

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

    /// <summary>Invoke, or the older way a Zoom menu item answers to; never a press of Leave.</summary>
    private static bool Press(AutomationElement element)
    {
        if (LeaveButton.IsMatch(Name(element))) return false;
        try
        {
            if (element.Patterns.Invoke.IsSupported) { element.Patterns.Invoke.Pattern.Invoke(); return true; }
            if (element.Patterns.SelectionItem.IsSupported) { element.Patterns.SelectionItem.Pattern.Select(); return true; }
            if (element.Patterns.LegacyIAccessible.IsSupported) { element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction(); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>Everything in Zoom's windows that can be pressed: buttons, menu items and list items.</summary>
    private static IEnumerable<AutomationElement> Choices(UIA3Automation automation, int processId)
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
            try
            {
                found = automation.FromHandle(handle)?.FindAllDescendants(cf =>
                    cf.ByControlType(ControlType.Button)
                        .Or(cf.ByControlType(ControlType.MenuItem))
                        .Or(cf.ByControlType(ControlType.ListItem))) ?? [];
            }
            catch { continue; }
            foreach (var choice in found) yield return choice;
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
