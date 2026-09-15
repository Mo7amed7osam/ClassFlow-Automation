using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.UIAutomation.Interop;

namespace ZoomAutoAdmit.UIAutomation.Window;

public sealed class ForegroundWindowPreserver : IDisposable
{
    private readonly IntPtr _originalForegroundHwnd;
    private readonly string _originalForegroundProcess;
    private readonly NativeMethods.POINT _originalCursorPos;
    private readonly IntPtr _zoomHwnd;
    private readonly bool _zoomOriginallyMinimized;
    private bool _zoomActivated;
    private bool _disposed;

    public ForegroundWindowPreserver(IntPtr? targetZoomHwnd = null)
    {
        _originalForegroundHwnd = NativeMethods.GetForegroundWindow();
        _originalForegroundProcess = NativeMethods.GetProcessNameSafe(_originalForegroundHwnd);
        NativeMethods.GetCursorPos(out _originalCursorPos);

        _zoomHwnd = targetZoomHwnd.GetValueOrDefault(IntPtr.Zero) != IntPtr.Zero
            ? targetZoomHwnd!.Value
            : ZoomWindowManager.FindActiveZoomWindow();
        _zoomOriginallyMinimized = _zoomHwnd != IntPtr.Zero && NativeMethods.IsIconic(_zoomHwnd);
    }

    public bool IsZoomMinimized => _zoomOriginallyMinimized;
    public IntPtr ZoomHwnd => _zoomHwnd;
    public IntPtr OriginalForegroundHwnd => _originalForegroundHwnd;
    public string OriginalForegroundProcess => _originalForegroundProcess;

    /// <summary>Zoom is only brought to the front when the person has left the computer this long.</summary>
    public static TimeSpan RequiredIdle { get; set; } = TimeSpan.FromSeconds(20);

    public bool ActivateZoomTemporarily()
    {
        if (_zoomHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_zoomHwnd))
        {
            return false;
        }

        // Never take the screen from someone who is working: while they type or point, Zoom stays
        // where it is and the caller tries again later.
        if (!ZoomAutoAdmit.UIAutomation.Input.UserActivity.IsIdleFor(RequiredIdle))
        {
            ConsoleLogger.Info("[FOREGROUND] The user is active; Zoom was not brought to the front.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("ZOOM_TEMPORARILY_RESTORED_FOR_ACTION");
        Console.WriteLine($"Original Foreground: {_originalForegroundProcess} (HWND=0x{_originalForegroundHwnd.ToInt64():X})");
        Console.WriteLine($"Zoom HWND: 0x{_zoomHwnd.ToInt64():X} (Originally Minimized: {_zoomOriginallyMinimized})");

        if (_zoomOriginallyMinimized)
        {
            NativeMethods.ShowWindow(_zoomHwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(_zoomHwnd);
        NativeMethods.BringWindowToTop(_zoomHwnd);
        _zoomActivated = true;

        Thread.Sleep(150); // Allow DWM and Zoom to render
        // Windows may refuse the switch; then keys meant for Zoom would land in the user's window.
        var front = NativeMethods.GetForegroundWindow();
        if (front != _zoomHwnd && !NativeMethods.GetProcessNameSafe(front).Contains("zoom", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLogger.Info("[FOREGROUND] Windows kept another window in front; nothing will be typed.");
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_zoomActivated)
        {
            if (_zoomOriginallyMinimized && _zoomHwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_zoomHwnd, NativeMethods.SW_MINIMIZE);
            }

            if (_originalForegroundHwnd != IntPtr.Zero && _originalForegroundHwnd != _zoomHwnd)
            {
                NativeMethods.SetForegroundWindow(_originalForegroundHwnd);
                Console.WriteLine("USER_FOREGROUND_RESTORED");
                Console.WriteLine($"Restored application: {_originalForegroundProcess}");
            }

            NativeMethods.SetCursorPos(_originalCursorPos.X, _originalCursorPos.Y);
        }
    }
}
