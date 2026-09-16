using System.Diagnostics;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Meetings;

/// <summary>
/// Whether breakout rooms are open in the Zoom app right now.
///
/// A class can look empty while everyone is inside a breakout room: the main participants list
/// shows only the people who are not in one, so the host can be "alone" with twenty students a
/// click away. Nothing is ever ended while rooms are open.
///
/// Zoom only shows "Close all rooms" while rooms are actually open - rooms that are merely
/// prepared offer "Open All Rooms" instead - and it puts the panel in a window of its own titled
/// "Breakout rooms - In progress". Both were read from a real meeting with five rooms open
/// (2026-09-16), and a window of its own is why this works even though the meeting's toolbar
/// auto-hides. Read through the accessibility tree only: no mouse, no keys, nothing is pressed.
/// </summary>
public static class ZoomBreakoutRooms
{
    // While rooms are open. "Open All Rooms" and "Recreate" belong to rooms that are not running.
    private static readonly Regex OpenNow = new(
        @"^\s*close\s+all\s+rooms\s*$|^\s*broadcast\s+(a\s+)?message\b|\bbreakout\s+rooms?\s*[-–—]\s*in\s+progress\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True only when Zoom is showing that rooms are open; anything unreadable is false.</summary>
    public static bool AreOpen()
    {
        bool open = false;
        try { DesktopThread.RunOnInteractiveDesktop(() => open = Look(), 15000); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        return open;
    }

    private static bool Look()
    {
        var zoom = Process.GetProcessesByName("Zoom").Select(p => p.Id).ToHashSet();
        foreach (var name in new[] { "CptHost" }) zoom.UnionWith(Process.GetProcessesByName(name).Select(p => p.Id));
        if (zoom.Count == 0) return false;

        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(h, out uint owner);
            if (zoom.Contains((int)owner) && NativeMethods.IsWindowVisible(h)) windows.Add(h);
            return true;
        }, IntPtr.Zero);

        using var automation = new UIA3Automation();
        foreach (var handle in windows)
        {
            AutomationElement? root;
            try { root = automation.FromHandle(handle); }
            catch { continue; }
            if (root == null) continue;
            if (Matches(SafeName(root))) return true;
            AutomationElement[] children;
            try { children = root.FindAllDescendants(); }
            catch { continue; }
            foreach (var element in children)
                if (Matches(SafeName(element))) return true;
        }
        return false;
    }

    private static bool Matches(string name) => name.Length > 0 && OpenNow.IsMatch(name);

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? ""; } catch { return ""; }
    }
}
