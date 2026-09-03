using System.Runtime.InteropServices;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Window;

public enum ZoomWindowRole
{
    Unknown,
    MeetingWindow,
    ParticipantsWindow,
    MeetingChat,
    NotificationToast,
    Other
}

public sealed class ZoomWindowManager
{
    private static readonly string[] ZoomClasses =
    [
        "ZPContentViewWndClass",
        "ConfMultiTabContentWndClass",
        "zMeetingNotificationWndClass",
        "ZPFloatVideoWndClass",
        "ZVideoStatusWndClass"
    ];

    public static ZoomWindowRole ClassifyZoomWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd)) return ZoomWindowRole.Unknown;

        string title = NativeMethods.GetWindowTitleSafe(hWnd);
        string className = NativeMethods.GetClassNameSafe(hWnd);
        string process = NativeMethods.GetProcessNameSafe(hWnd);

        if (!process.Contains("zoom", StringComparison.OrdinalIgnoreCase) &&
            !process.Equals("cptHost", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.Unknown;
        }

        if (title.Contains("Chat", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Meeting Chat", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.MeetingChat;
        }

        if (title.Contains("Participants", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.ParticipantsWindow;
        }

        if (className.Equals("zMeetingNotificationWndClass", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.NotificationToast;
        }

        // Current Zoom Workplace embeds the full meeting in these native content windows
        // while keeping the generic "Zoom Workplace" title. Treat them as meeting surfaces;
        // otherwise discovery falls through to a tiny hidden floating-video helper.
        if (className.Equals("ConfMultiTabContentWndClass", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("ZPContentViewWndClass", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.MeetingWindow;
        }

        // CptHost is the dedicated Zoom meeting host process
        if (process.Equals("cptHost", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("airhost", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.MeetingWindow;
        }

        // Active meeting window titles:
        // "Zoom Meeting", "Zoom Webinar", "Meeting ID: ...", "Zoom - Free Account" (in meeting)
        // Zoom Workplace Home screen has title "Zoom Workplace" or "Zoom", which is NOT an active meeting.
        if (title.Contains("Zoom Meeting", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Zoom Webinar", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("Meeting ID:", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.MeetingWindow;
        }

        // Floating meeting video thumbnail window
        if (className.Equals("ZPFloatVideoWndClass", StringComparison.OrdinalIgnoreCase))
        {
            return ZoomWindowRole.MeetingWindow;
        }

        return ZoomWindowRole.Other;
    }

    public static bool IsActiveMeetingPresent()
    {
        // 1. Check for dedicated meeting window (e.g. CptHost or Zoom Meeting window)
        if (FindMainZoomMeetingWindow() != IntPtr.Zero || FindParticipantsWindow() != IntPtr.Zero)
        {
            return true;
        }

        // 2. Check for visible floating meeting / mini-window
        bool meetingWindowFound = false;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd))
            {
                var role = ClassifyZoomWindow(hWnd);
                if (role == ZoomWindowRole.MeetingWindow || role == ZoomWindowRole.ParticipantsWindow)
                {
                    meetingWindowFound = true;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (meetingWindowFound) return true;

        // 3. Check if Zoom Home screen has "Return to meeting" / "Back to meeting" button
        return HasReturnToMeetingButton();
    }

    public static bool HasReturnToMeetingButton()
    {
        bool found = false;
        try
        {
            Discovery.DesktopThread.RunOnInteractiveDesktop(() =>
            {
                var candidate = new Discovery.ZoomProcessDiscovery().FindPrimaryCandidate();
                if (candidate == null) return;

                using var automation = new FlaUI.UIA3.UIA3Automation();
                var roots = candidate.Windows
                    .Where(w => w.IsVisible)
                    .Select(w => w.Handle)
                    .Append(candidate.MainWindowHandle)
                    .Where(h => h != IntPtr.Zero)
                    .Distinct();

                foreach (var h in roots)
                {
                    try
                    {
                        var root = automation.FromHandle(h);
                        if (root == null) continue;
                        if (FindReturnToMeetingElement(root))
                        {
                            found = true;
                            return;
                        }
                    }
                    catch { }
                }
            });
        }
        catch { }
        return found;
    }

    private static bool FindReturnToMeetingElement(FlaUI.Core.AutomationElements.AutomationElement element)
    {
        try
        {
            string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            if (name.IndexOf("return to meeting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("back to meeting", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (var child in element.FindAllChildren())
            {
                if (FindReturnToMeetingElement(child)) return true;
            }
        }
        catch { }
        return false;
    }

    public static IntPtr FindParticipantsWindow()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (ClassifyZoomWindow(hWnd) == ZoomWindowRole.ParticipantsWindow)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr FindMainZoomMeetingWindow()
    {
        var candidates = new List<(IntPtr Handle, int Score, long Area)>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (ClassifyZoomWindow(hWnd) == ZoomWindowRole.MeetingWindow)
            {
                string className = NativeMethods.GetClassNameSafe(hWnd);
                int score = NativeMethods.IsWindowVisible(hWnd) ? 100 : 0;
                if (className.Equals("ConfMultiTabContentWndClass", StringComparison.OrdinalIgnoreCase) ||
                    className.Equals("ZPContentViewWndClass", StringComparison.OrdinalIgnoreCase))
                    score += 50;
                if (className.Equals("ZPFloatVideoWndClass", StringComparison.OrdinalIgnoreCase))
                    score -= 20;
                long area = 0;
                if (NativeMethods.GetWindowRect(hWnd, out var rect))
                    area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
                candidates.Add((hWnd, score, area));
            }
            return true;
        }, IntPtr.Zero);
        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Area)
            .Select(candidate => candidate.Handle)
            .FirstOrDefault();
    }

    public static IntPtr FindActiveZoomWindow()
    {
        // 1. If Participants window is open, prefer it for waiting room operations
        var partWnd = FindParticipantsWindow();
        if (partWnd != IntPtr.Zero)
        {
            Console.WriteLine($"PARTICIPANTS_WINDOW_SELECTED: HWND=0x{partWnd.ToInt64():X}");
            return partWnd;
        }

        // 2. Otherwise use main meeting window
        var meetingWnd = FindMainZoomMeetingWindow();
        if (meetingWnd != IntPtr.Zero)
        {
            Console.WriteLine($"ZOOM_MEETING_WINDOW_SELECTED: HWND=0x{meetingWnd.ToInt64():X}");
            return meetingWnd;
        }

        // 3. Fallback to any valid non-chat Zoom window
        IntPtr fallback = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var role = ClassifyZoomWindow(hWnd);
            if (role != ZoomWindowRole.Unknown && role != ZoomWindowRole.MeetingChat)
            {
                fallback = hWnd;
                return false;
            }
            if (role == ZoomWindowRole.MeetingChat)
            {
                Console.WriteLine($"MEETING_CHAT_IGNORED: HWND=0x{hWnd.ToInt64():X}");
            }
            return true;
        }, IntPtr.Zero);

        return fallback;
    }

    public static bool IsZoomTopmostAt(double x, double y)
    {
        var evidence = NativeMethods.GetWindowAtPointEvidenceSafe(x, y);
        string proc = evidence.TargetProcess.Trim();
        if (proc.Contains("zoom", StringComparison.OrdinalIgnoreCase) ||
            proc.Equals("cptHost", StringComparison.OrdinalIgnoreCase) ||
            evidence.HasZoomParentOwnerChain)
        {
            return true;
        }

        return false;
    }

    public static bool IsWindowMinimized(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        return NativeMethods.IsIconic(hWnd);
    }
}
