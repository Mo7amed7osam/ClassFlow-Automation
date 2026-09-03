using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Models;

namespace ZoomAutoAdmit.UIAutomation.Screen;

/// <summary>
/// Handles Per-Monitor DPI aware desktop screen captures using Win32 GDI BitBlt + CAPTUREBLT
/// to capture layered windows and notification toasts.
/// </summary>
public static class WindowsScreenCapturer
{
    private const int SM_XVIRTUALSCREEN = 78;
    private const int SM_YVIRTUALSCREEN = 79;
    private const int SM_CXVIRTUALSCREEN = 80;
    private const int SM_CYVIRTUALSCREEN = 81;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
    private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static bool _dpiAwarenessSet;

    /// <summary>
    /// Whether the process really ended up per-monitor DPI aware.
    /// </summary>
    /// <remarks>
    /// A process settles its DPI awareness once, before anything reads a DPI, so this call is a
    /// request that usually fails: WPF has already made the UI app system-aware, and early GDI or
    /// WinForms use can do the same to a console app. Reporting what the process actually got
    /// matters, because system awareness on a mixed-scaling desktop makes Windows describe every
    /// non-primary monitor scaled by the primary's factor - a real 1920x1080 panel is reported as
    /// 4800x2700, CopyFromScreen reads far outside it, and the frame handed to OCR is mostly
    /// blank. Callers that click must refuse to run rather than click blind; the manifest
    /// (src/PerMonitorV2.manifest) is what actually keeps that from happening.
    /// </remarks>
    public static bool IsPerMonitorAware { get; private set; }

    public static void EnsureDpiAwareness()
    {
        if (_dpiAwarenessSet) return;
        try
        {
            // The return value is intentionally not trusted as proof: false only means the
            // awareness was already settled, which is fine when a manifest settled it correctly.
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            IsPerMonitorAware =
                GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == PROCESS_PER_MONITOR_DPI_AWARE;
            _dpiAwarenessSet = true;
            if (!IsPerMonitorAware)
            {
                ConsoleLogger.Error(
                    "DPI_AWARENESS_NOT_PER_MONITOR: this process is not per-monitor DPI aware, so monitor " +
                    "bounds and click coordinates cannot be trusted on a desktop with mixed display scaling.");
            }
        }
        catch (Exception ex)
        {
            IsPerMonitorAware = false;
            ConsoleLogger.Debug($"SetProcessDpiAwarenessContext: {ex.Message}");
        }
    }

    /// <summary>
    /// Reproduces the original PowerShell detector's capture path: the primary
    /// screen only, captured with Graphics.CopyFromScreen and SourceCopy.
    /// </summary>
    public static (Bitmap Bitmap, BoundingRectangleInfo ScreenBounds, float DpiX, float DpiY) CapturePrimaryScreen()
    {
        EnsureDpiAwareness();

        var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("Windows did not report a primary screen.");

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Primary screen has invalid bounds: X={bounds.X}, Y={bounds.Y}, W={bounds.Width}, H={bounds.Height}.");
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                bounds.X,
                bounds.Y,
                0,
                0,
                new Size(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);

            using var desktopGraphics = Graphics.FromHwnd(IntPtr.Zero);
            var screenBounds = new BoundingRectangleInfo(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            return (bitmap, screenBounds, desktopGraphics.DpiX, desktopGraphics.DpiY);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures the full virtual desktop spanning all monitors, including layered/alpha-blended toasts.
    /// </summary>
    public static (Bitmap Bitmap, BoundingRectangleInfo ScreenBounds) CaptureDesktop()
    {
        EnsureDpiAwareness();

        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            left = 0;
            top = 0;
            width = Math.Max(1920, GetSystemMetrics(0));
            height = Math.Max(1080, GetSystemMetrics(1));
        }

        var screenBounds = new BoundingRectangleInfo(left, top, width, height);

        // Capture using Win32 GDI BitBlt + CAPTUREBLT
        IntPtr hDesktopWnd = GetDesktopWindow();
        IntPtr hSrcDC = GetDC(IntPtr.Zero);
        if (hSrcDC == IntPtr.Zero)
        {
            hSrcDC = GetWindowDC(hDesktopWnd);
        }

        if (hSrcDC == IntPtr.Zero)
        {
            // Fallback to Graphics.CopyFromScreen if DC cannot be acquired
            var fallbackBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fallbackBmp))
            {
                g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }
            return (fallbackBmp, screenBounds);
        }

        IntPtr hDestDC = CreateCompatibleDC(hSrcDC);
        IntPtr hBitmap = CreateCompatibleBitmap(hSrcDC, width, height);
        IntPtr hOldBitmap = SelectObject(hDestDC, hBitmap);

        try
        {
            bool bltOk = BitBlt(
                hDestDC, 0, 0, width, height,
                hSrcDC, left, top,
                SRCCOPY | CAPTUREBLT);

            if (!bltOk)
            {
                // Retry without CAPTUREBLT if not supported
                BitBlt(hDestDC, 0, 0, width, height, hSrcDC, left, top, SRCCOPY);
            }

            // Create Managed Bitmap from HBitmap
            using var rawBmp = Image.FromHbitmap(hBitmap);
            var clonedBmp = new Bitmap(rawBmp.Width, rawBmp.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(clonedBmp))
            {
                g.DrawImage(rawBmp, 0, 0);
            }

            return (clonedBmp, screenBounds);
        }
        finally
        {
            SelectObject(hDestDC, hOldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(hDestDC);
            ReleaseDC(IntPtr.Zero, hSrcDC);
        }
    }

    public static IReadOnlyList<MonitorCaptureInfo> CaptureAllScreens()
    {
        EnsureDpiAwareness();
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens == null || screens.Length == 0)
        {
            var primaryCapture = CapturePrimaryScreen();
            return [new MonitorCaptureInfo(@"\\.\DISPLAY1", primaryCapture.ScreenBounds, true, primaryCapture.Bitmap, primaryCapture.DpiX, primaryCapture.DpiY)];
        }

        var list = new List<MonitorCaptureInfo>();
        using var desktopGraphics = Graphics.FromHwnd(IntPtr.Zero);
        float desktopDpiX = desktopGraphics.DpiX;
        float desktopDpiY = desktopGraphics.DpiY;

        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            var bitmap = new Bitmap(bounds.Width, bounds.Height);
            try
            {
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(
                    bounds.X,
                    bounds.Y,
                    0,
                    0,
                    new Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy);

                var boundsInfo = new BoundingRectangleInfo(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                list.Add(new MonitorCaptureInfo(screen.DeviceName, boundsInfo, screen.Primary, bitmap, desktopDpiX, desktopDpiY));
            }
            catch
            {
                bitmap.Dispose();
                foreach (var captured in list) captured.Dispose();
                throw;
            }
        }

        return list;
    }

    public static Bitmap CaptureAbsoluteRegion(BoundingRectangleInfo absoluteBounds)
    {
        EnsureDpiAwareness();
        int left = checked((int)Math.Floor(absoluteBounds.X));
        int top = checked((int)Math.Floor(absoluteBounds.Y));
        int width = checked(Math.Max(1, (int)Math.Ceiling(absoluteBounds.Width)));
        int height = checked(Math.Max(1, (int)Math.Ceiling(absoluteBounds.Height)));

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        return bmp;
    }
}

public sealed record MonitorCaptureInfo(
    string DeviceName,
    BoundingRectangleInfo Bounds,
    bool IsPrimary,
    Bitmap Bitmap,
    float DpiX,
    float DpiY) : IDisposable
{
    public void Dispose() => Bitmap.Dispose();
}
