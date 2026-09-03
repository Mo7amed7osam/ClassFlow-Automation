using System.Runtime.InteropServices;
using System.Text;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;

namespace ZoomAutoAdmit.UIAutomation.Interop;

public static class NativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    public const uint DESKTOP_ACCESS_ALL = 0x01FF;
    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObject,
        int nIndex,
        StringBuilder? information,
        int length,
        out int needed);

    public static bool IsInteractiveInputDesktopAvailable()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            GetUserObjectInformation(desktop, UOI_NAME, null, 0, out int needed);
            if (needed <= 0)
            {
                return false;
            }

            var name = new StringBuilder(needed / sizeof(char));
            return GetUserObjectInformation(desktop, UOI_NAME, name, needed, out _) &&
                   name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public static bool EnsureInteractiveDesktop()
    {
        try
        {
            var hDesk = OpenDesktop("Default", 0, false, 0x01FF);
            if (hDesk == IntPtr.Zero)
            {
                var err1 = Marshal.GetLastWin32Error();
                ConsoleLogger.Debug($"OpenDesktop('Default') failed with Win32 Error: {err1}. Trying OpenInputDesktop...");
                hDesk = OpenInputDesktop(0, false, 0x01FF);
            }
            if (hDesk != IntPtr.Zero)
            {
                var ok = SetThreadDesktop(hDesk);
                var err2 = Marshal.GetLastWin32Error();
                ConsoleLogger.Debug($"SetThreadDesktop(0x{hDesk.ToInt64():X}) returned: {ok} (Error: {err2})");
                return ok;
            }
            else
            {
                var err3 = Marshal.GetLastWin32Error();
                ConsoleLogger.Debug($"OpenInputDesktop failed with Win32 Error: {err3}");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"EnsureInteractiveDesktop exception: {ex.Message}");
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public BoundingRectangleInfo ToBoundingRectangle() =>
            new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public static ForegroundWindowInfo GetForegroundWindowInfoSafe()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return new ForegroundWindowInfo(IntPtr.Zero, 0, "(none)", "(no window)");
        }

        GetWindowThreadProcessId(hWnd, out var pid);
        var title = GetWindowTitleSafe(hWnd);
        var procName = "(unknown)";
        if (pid > 0)
        {
            try
            {
                procName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { }
        }

        return new ForegroundWindowInfo(hWnd, (int)pid, procName, title);
    }

    public static ForegroundWindowInfo GetRootWindowAtPointInfoSafe(double x, double y)
    {
        var point = new POINT
        {
            X = checked((int)Math.Round(x)),
            Y = checked((int)Math.Round(y))
        };
        var window = WindowFromPoint(point);
        if (window == IntPtr.Zero)
        {
            return new ForegroundWindowInfo(IntPtr.Zero, 0, "(none)", "(no window at target)");
        }

        var root = GetAncestor(window, 2);
        if (root != IntPtr.Zero) window = root;
        GetWindowThreadProcessId(window, out var pid);
        string processName = "(unknown)";
        if (pid > 0)
        {
            try { processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { }
        }
        return new ForegroundWindowInfo(window, (int)pid, processName, GetWindowTitleSafe(window));
    }

    public static WindowPointEvidence GetWindowAtPointEvidenceSafe(double x, double y)
    {
        var target = WindowFromPoint(new POINT
        {
            X = checked((int)Math.Round(x)),
            Y = checked((int)Math.Round(y))
        });
        var root = target == IntPtr.Zero ? IntPtr.Zero : GetAncestor(target, 2);
        if (root == IntPtr.Zero) root = target;

        var chain = new List<string>();
        bool hasZoomParentOwner = false;
        var current = target;
        var visited = new HashSet<IntPtr>();
        while (current != IntPtr.Zero && visited.Add(current) && chain.Count < 8)
        {
            string chainProcess = GetProcessNameSafe(current);
            if (chainProcess.Contains("zoom", StringComparison.OrdinalIgnoreCase) ||
                chainProcess.Equals("cptHost", StringComparison.OrdinalIgnoreCase))
                hasZoomParentOwner = true;
            chain.Add(DescribeWindow(current));
            var parent = GetParent(current);
            if (parent == IntPtr.Zero) parent = GetWindow(current, 4);
            current = parent;
        }

        var zoomContaining = new List<string>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !GetWindowRect(window, out var rect)) return true;
            if (x < rect.Left || x > rect.Right || y < rect.Top || y > rect.Bottom) return true;
            string process = GetProcessNameSafe(window);
            if (process.Contains("zoom", StringComparison.OrdinalIgnoreCase) ||
                process.Equals("cptHost", StringComparison.OrdinalIgnoreCase))
                zoomContaining.Add($"{DescribeWindow(window)} bounds={rect.ToBoundingRectangle()}");
            return true;
        }, IntPtr.Zero);

        return new WindowPointEvidence(
            target,
            root,
            GetProcessNameSafe(target),
            GetProcessNameSafe(root),
            GetClassNameSafe(target),
            GetClassNameSafe(root),
            GetWindowTitleSafe(target),
            GetWindowTitleSafe(root),
            chain,
            zoomContaining,
            hasZoomParentOwner);
    }

    public static string GetRootWindowClassAtPointSafe(double x, double y)
    {
        var window = WindowFromPoint(new POINT
        {
            X = checked((int)Math.Round(x)),
            Y = checked((int)Math.Round(y))
        });
        if (window == IntPtr.Zero) return string.Empty;
        var root = GetAncestor(window, 2);
        return GetClassNameSafe(root != IntPtr.Zero ? root : window);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint PW_CLIENTONLY = 0x00000001;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBmp, uint nFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint MK_LBUTTON = 0x0001;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public static string GetWindowTitleSafe(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassNameSafe(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessNameSafe(IntPtr window)
    {
        if (window == IntPtr.Zero) return "(none)";
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return "(unknown)";
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return "(unknown)"; }
    }

    public static string DescribeWindow(IntPtr window) =>
        $"HWND=0x{window.ToInt64():X} process='{GetProcessNameSafe(window)}' class='{GetClassNameSafe(window)}' title='{GetWindowTitleSafe(window)}'";

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const byte VK_RETURN = 0x0D;
    public const byte VK_UP = 0x26;
    public const byte VK_DOWN = 0x28;
    public const byte VK_RIGHT = 0x27;
    public const byte VK_LEFT = 0x25;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_TAB = 0x09;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static void SendKeyPress(byte vk)
    {
        uint scan = MapVirtualKey(vk, 0);
        keybd_event(vk, (byte)scan, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        keybd_event(vk, (byte)scan, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(120);
    }

    public static void SendAltKey(byte vkKey)
    {
        uint vkAlt = 0x12; // VK_MENU
        uint scanAlt = MapVirtualKey(vkAlt, 0);
        uint scanKey = MapVirtualKey(vkKey, 0);

        // Alt down
        keybd_event((byte)vkAlt, (byte)scanAlt, 0, UIntPtr.Zero);
        Thread.Sleep(80);

        // Key down
        keybd_event(vkKey, (byte)scanKey, 0, UIntPtr.Zero);
        Thread.Sleep(120);

        // Key up
        keybd_event(vkKey, (byte)scanKey, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(80);

        // Alt up
        keybd_event((byte)vkAlt, (byte)scanAlt, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(150);
    }

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(hWnd, SW_SHOW);
        }

        IntPtr fgWnd = GetForegroundWindow();
        uint fgThreadId = fgWnd != IntPtr.Zero ? GetWindowThreadProcessId(fgWnd, out _) : 0;
        uint curThreadId = GetCurrentThreadId();

        if (fgThreadId != 0 && fgThreadId != curThreadId)
        {
            AttachThreadInput(curThreadId, fgThreadId, true);
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
            AttachThreadInput(curThreadId, fgThreadId, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
        }
    }
}

public sealed record WindowPointEvidence(
    IntPtr TargetHandle,
    IntPtr RootHandle,
    string TargetProcess,
    string RootProcess,
    string TargetClass,
    string RootClass,
    string TargetTitle,
    string RootTitle,
    IReadOnlyList<string> ParentOwnerChain,
    IReadOnlyList<string> ZoomWindowsContainingPoint,
    bool HasZoomParentOwnerChain);
