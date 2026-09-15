using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Window;

/// <summary>
/// Zoom's Participants panel through UI Automation only: whether it is open (docked in the meeting
/// window or floating), and opening it with Zoom's own toolbar button. Neither touches the mouse,
/// the keyboard or the window in front.
/// </summary>
public static class ZoomParticipantsPanel
{
    private static readonly Regex ListName = new(
        @"^participant list\b|^(joined|in[- ]meeting|waiting room)(\s+participants)?(\s+list)?(\s*\(\d+\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsOpen()
    {
        if (ZoomWindowManager.FindParticipantsWindow() != IntPtr.Zero) return true;
        bool open = false;
        Run(automation =>
        {
            foreach (var root in Roots(automation))
            {
                if (root.FindAllDescendants(cf => cf.ByControlType(ControlType.List))
                    .Any(list => ListName.IsMatch(Name(list)) && !Offscreen(list)))
                { open = true; return; }
            }
        });
        return open;
    }

    /// <summary>Invokes "Participants, open panel" when Zoom shows it (its toolbar can be hidden).</summary>
    public static bool TryOpenWithoutFocus()
    {
        bool invoked = false;
        Run(automation =>
        {
            foreach (var root in Roots(automation))
            {
                var button = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).FirstOrDefault(b =>
                {
                    string name = Name(b);
                    return name.StartsWith("Participants", StringComparison.OrdinalIgnoreCase) &&
                           name.Contains("open", StringComparison.OrdinalIgnoreCase);
                });
                if (button == null || !button.Patterns.Invoke.IsSupported) continue;
                button.Patterns.Invoke.Pattern.Invoke();
                invoked = true;
                return;
            }
        });
        return invoked;
    }

    private static void Run(Action<UIA3Automation> work)
    {
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                using var automation = new UIA3Automation();
                work(automation);
            });
        }
        catch { }
    }

    private static IEnumerable<AutomationElement> Roots(UIA3Automation automation)
    {
        foreach (var handle in new[] { ZoomWindowManager.FindMainZoomMeetingWindow(), ZoomWindowManager.FindParticipantsWindow() }.Distinct())
        {
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) continue;
            AutomationElement? root = null;
            try { root = automation.FromHandle(handle); } catch { }
            if (root != null) yield return root;
        }
    }

    private static string Name(AutomationElement e) { try { return e.Name ?? ""; } catch { return ""; } }
    private static bool Offscreen(AutomationElement e) { try { return e.Properties.IsOffscreen.ValueOrDefault; } catch { return true; } }
}
