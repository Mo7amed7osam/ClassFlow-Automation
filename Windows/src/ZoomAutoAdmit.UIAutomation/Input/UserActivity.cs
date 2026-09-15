using System.Runtime.InteropServices;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Input;

/// <summary>
/// Whether the person at the computer is using it right now, so the monitor never moves their
/// mouse, clicks, or brings Zoom to the front while they type or point. Input the monitor sends
/// itself is not counted as the person's.
/// </summary>
public static class UserActivity
{
    private static long _lastInjectedTick;

    /// <summary>How long ago the person last pressed a key or moved/clicked the mouse.</summary>
    public static TimeSpan SinceLastInput()
    {
        var info = new LASTINPUTINFO { Size = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;
        uint now = unchecked((uint)Environment.TickCount);
        // The last input was the monitor's own click or keystroke: the person has been away since before it.
        long injected = Interlocked.Read(ref _lastInjectedTick);
        if (injected != 0 && Math.Abs(unchecked((int)(info.Time - (uint)injected))) < 400) return TimeSpan.MaxValue;
        return TimeSpan.FromMilliseconds(unchecked(now - info.Time));
    }

    public static bool IsIdleFor(TimeSpan quiet) => SinceLastInput() >= quiet;

    /// <summary>Waits up to <paramref name="patience"/> for a quiet moment of <paramref name="quiet"/>.</summary>
    public static bool WaitForQuiet(TimeSpan quiet, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        while (true)
        {
            if (IsIdleFor(quiet)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(150);
        }
    }

    /// <summary>Called right after the monitor sends input of its own.</summary>
    public static void MarkInjected() => Interlocked.Exchange(ref _lastInjectedTick, unchecked((uint)Environment.TickCount));

    /// <summary>Whether the screen point is on a Zoom window, so a click there can only reach Zoom.</summary>
    public static bool IsZoomAt(int x, int y)
    {
        IntPtr hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return false;
        string process = NativeMethods.GetProcessNameSafe(hwnd);
        return process.Contains("zoom", StringComparison.OrdinalIgnoreCase) || process.Contains("CptHost", StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint Size; public uint Time; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
}
