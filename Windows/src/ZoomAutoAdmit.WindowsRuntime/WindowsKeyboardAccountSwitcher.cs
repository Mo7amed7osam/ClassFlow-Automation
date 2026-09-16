using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.WindowsRuntime;

/// <summary>Shared by the Accounts button and the diagnostic CLI. Input targets are read from UIA.</summary>
public sealed class WindowsKeyboardAccountSwitcher
{
    private const string ProfileMenuClass = "ZPPTMainMenuWndClass";
    private const string SubmenuClass = "ZPPTSwitchAccountSubMenuWndClass";
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    public async Task<MeetingOperationResult> SwitchAsync(string email, CancellationToken cancellation, int? requestedPid = null)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address) ||
            !address.Address.Equals(email, StringComparison.OrdinalIgnoreCase))
            return MeetingOperationResult.Failure("An exact target account email is required.");
        if (!await InputGate.WaitAsync(0, cancellation))
            return MeetingOperationResult.Failure("Another account switch is already in progress.");
        try
        {
            return await Task.Run(() =>
            {
                var result = MeetingOperationResult.Failure("Account switch did not complete.");
                DesktopThread.RunOnInteractiveDesktop(() =>
                {
                    using var automation = new UIA3Automation();
                    var runner = new Runner(automation, cancellation);
                    try
                    {
                        runner.Run(email, requestedPid);
                        result = MeetingOperationResult.Success();
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log($"Last focused item: {runner.LastFocus}");
                        Log($"Verification result: Failed - {ex}");
                        result = MeetingOperationResult.Failure(ex.Message);
                    }
                }, uint.MaxValue);
                cancellation.ThrowIfCancellationRequested();
                return result;
            }, cancellation);
        }
        finally { InputGate.Release(); }
    }

    private static void Log(string message) => ConsoleLogger.Info($"[KEYBOARD_SWITCH] {message}");

    // Kept separate so cold start, tray restore, timeout and cancellation can be tested without Zoom/input.
    internal static IntPtr EnsureMainWindow(Func<IntPtr> find, Action launch, Action<IntPtr> show,
        Action pause, CancellationToken cancellation, int attempts = 60)
    {
        cancellation.ThrowIfCancellationRequested();
        var handle = find();
        if (handle == IntPtr.Zero)
        {
            Log("Starting Zoom Desktop...");
            launch();
            for (int attempt = 0; attempt < attempts && handle == IntPtr.Zero; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                pause();
                handle = find();
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Zoom did not create its home window. Complete Zoom startup/login and try again.");
        Log("Showing Zoom Desktop home window...");
        show(handle);
        return handle;
    }

    private static void LaunchZoom()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zoom", "bin", "Zoom.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zoom", "bin", "Zoom.exe")
        };
        var executable = paths.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Zoom Desktop installation was not found. Install Zoom before switching accounts.");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    internal static bool ContainsEmail(string text, string email) => !string.IsNullOrWhiteSpace(email) && Regex.IsMatch(text,
        @"(?<![\w.+@-])" + Regex.Escape(email) + @"(?![\w.+@-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsActiveAccount(string name, uint legacyState) =>
        (legacyState & 0x10) != 0 || name.Split(',').Any(part =>
            part.Trim().Equals("Current active account", StringComparison.OrdinalIgnoreCase));

    private sealed class Runner(UIA3Automation automation, CancellationToken cancellation)
    {
        private int processId;
        private IntPtr main;
        public string LastFocus { get; private set; } = "(unavailable)";

        public void Run(string email, int? requestedPid)
        {
            var discovery = new ZoomProcessDiscovery();
            var candidates = discovery.FindCandidates(logInfo: false);
            if (candidates.SelectMany(c => c.Windows).Any(w =>
                    (w.IsVisible || NativeMethods.IsIconic(w.Handle)) &&
                    ZoomWindowManager.ClassifyZoomWindow(w.Handle) == ZoomWindowRole.MeetingWindow) ||
                ZoomWindowManager.HasReturnToMeetingButton())
                throw new InvalidOperationException("Active meeting detected; switching blocked.");
            Log("Meeting check: Zoom is idle; switching allowed.");

            IntPtr FindMain()
            {
                var handles = discovery.FindCandidates(logInfo: false)
                    .Where(c => requestedPid == null || c.ProcessId == requestedPid)
                    .SelectMany(c => c.Windows)
                    .Where(w => w.ClassName == "ZPPTMainFrmWndClassEx")
                    .Select(w => w.Handle).Distinct().ToArray();
                if (handles.Length > 1) throw new InvalidOperationException("Multiple Zoom home windows found; cannot select safely.");
                return handles.SingleOrDefault();
            }
            main = EnsureMainWindow(FindMain, LaunchZoom, NativeMethods.ForceForegroundWindow,
                () => Pause(500), cancellation);
            NativeMethods.GetWindowThreadProcessId(main, out uint pid);
            processId = (int)pid;
            Log($"Zoom detected: HWND=0x{main.ToInt64():X}, PID={processId}; target={email}");
            Pause(300);
            RequireZoomForeground();
            // Start from a clean menu state using keyboard, not user intervention.
            for (int i = 0; i < 3 && (FindPopup(SubmenuClass) != null || FindPopup(ProfileMenuClass) != null); i++)
                Key(VirtualKeyShort.ESCAPE);
            OpenProfile();
            OpenSubmenu();
            var before = MenuItems(WaitPopup(SubmenuClass));
            foreach (var item in before) Log($"Available account item: {item.Name}; LegacyState={LegacyState(item)}");
            if (before.Count(i => ContainsEmail(i.Name, email)) != 1)
                throw new InvalidOperationException("Target email is missing or ambiguous in submenu.");
            if (before.Any(i => ContainsEmail(i.Name, email) && IsActiveAccount(i)))
            {
                Log("Verification result: Success - target account was already checked/active.");
                return;
            }
            Navigate(SubmenuClass, name => ContainsEmail(name, email));
            Log("Target account focused");
            // Re-read focus immediately before Enter; never choose by position or display name.
            var focused = ReadFocus(WaitPopup(SubmenuClass));
            if (focused == null || !ContainsEmail(focused.Name, email))
                throw new InvalidOperationException("Target lost keyboard focus before Enter.");
            Key(VirtualKeyShort.RETURN);
            Log("Enter pressed");
            Pause(5000);
            // Zoom now reloads itself with the chosen account: for a while its window does not answer
            // UI Automation at all (a UIA timeout), and it can come back as a new window. Only 7
            // seconds were given before, which gave up on an account that opened fine a moment later
            // and sent the class to the browser (2026-09-16, S8). Waited out now, up to 90 seconds.
            // Re-open using the same controlled interaction. A focused row alone is NOT proof
            // of the active account: require Zoom's explicit active label or checked state.
            var deadline = DateTime.UtcNow + AccountLoadTimeout;
            string? waitingFor = null;
            while (true)
            {
                try
                {
                    var current = FindMain();
                    if (current == IntPtr.Zero) throw new InvalidOperationException("Zoom's main window is not back yet.");
                    if (current != main)
                    {
                        main = current;
                        NativeMethods.GetWindowThreadProcessId(main, out uint newPid);
                        processId = (int)newPid;
                        Log($"Zoom came back as a new window: HWND=0x{main.ToInt64():X}, PID={processId}");
                    }
                    NativeMethods.ForceForegroundWindow(main);
                    Pause(300);
                    RequireZoomForeground();
                    if (FindPopup(SubmenuClass) == null)
                    {
                        if (FindPopup(ProfileMenuClass) == null) OpenProfile();
                        OpenSubmenu();
                    }
                    var items = MenuItems(WaitPopup(SubmenuClass));
                    foreach (var item in items) Log($"Verification item: {item.Name}; LegacyState={LegacyState(item)}");
                    if (items.Any(i => ContainsEmail(i.Name, email) && IsActiveAccount(i)))
                    {
                        Log("Verification result: Success - target email is marked as the current active account.");
                        return;
                    }
                    waitingFor = "the account to be marked active";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (DateTime.UtcNow < deadline)
                {
                    string reason = ex is TimeoutException || ex.InnerException is System.Runtime.InteropServices.COMException
                        ? "Zoom is still loading the account (not answering yet)" : ex.Message;
                    if (reason != waitingFor) { Log($"Waiting: {reason}"); waitingFor = reason; }
                }
                if (DateTime.UtcNow >= deadline)
                    throw new InvalidOperationException(
                        $"Enter sent, but the account was not shown as active within {AccountLoadTimeout.TotalSeconds:0} seconds ({waitingFor}). No second Enter sent.");
                Pause(2000);
            }
        }

        /// <summary>How long Zoom may take to reload itself with another account.</summary>
        private static readonly TimeSpan AccountLoadTimeout = TimeSpan.FromSeconds(90);

        private void OpenProfile()
        {
            var profile = Walk(automation.FromHandle(main)).SingleOrDefault(e =>
                ProfileButtonMatcher.IsProfileSplitButton(e.Properties.ControlType.ValueOrDefault.ToString(),
                    e.Properties.Name.ValueOrDefault, e.Properties.IsEnabled.ValueOrDefault, e.Patterns.Invoke.IsSupported));
            if (profile == null) throw new InvalidOperationException("Unique Profile UIA button not found.");
            var r = profile.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0 || profile.IsOffscreen)
                throw new InvalidOperationException("Profile UIA bounding rectangle is not visible.");
            RequireZoomForeground();
            Log($"Profile click attempted: UIA bounds={r}");
            Mouse.Click(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
            WaitPopup(ProfileMenuClass);
            Log("Profile menu detected");
            Log("Profile opened");
        }

        private void OpenSubmenu()
        {
            Navigate(ProfileMenuClass, n => n.StartsWith("Switch account,", StringComparison.OrdinalIgnoreCase));
            Log("Switch account reached");
            Key(VirtualKeyShort.RIGHT);
            WaitPopup(SubmenuClass);
            Log("Submenu opened");
        }

        private void Navigate(string menuClass, Func<string, bool> target)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int step = 0; step < 40; step++)
            {
                var menu = WaitPopup(menuClass);
                var focused = ReadFocus(menu);
                if (focused != null)
                {
                    if (target(focused.Name)) return;
                    if (!seen.Add(focused.Name))
                        throw new InvalidOperationException("Keyboard focus repeated without reaching target.");
                }
                else if (step > 0)
                    throw new InvalidOperationException("Keyboard focus unavailable after Down; stopping.");
                Key(VirtualKeyShort.DOWN);
            }
            throw new InvalidOperationException("Keyboard navigation limit reached.");
        }

        private AutomationElement? ReadFocus(AutomationElement menu)
        {
            var items = MenuItems(menu);
            var focused = items.Where(i => i.Properties.HasKeyboardFocus.ValueOrDefault ||
                (LegacyState(i) & 4) != 0).ToArray(); // STATE_SYSTEM_FOCUSED, not CHECKED
            var result = focused.Length == 1 ? focused[0] : null;
            LastFocus = result?.Name ?? "(unavailable or ambiguous)";
            Log($"Current focused item: {LastFocus}");
            return result;
        }

        private static uint LegacyState(AutomationElement item) => item.Patterns.LegacyIAccessible.IsSupported
            ? (uint)item.Patterns.LegacyIAccessible.Pattern.State.Value : 0;
        private static bool IsActiveAccount(AutomationElement item) =>
            WindowsKeyboardAccountSwitcher.IsActiveAccount(item.Name, LegacyState(item));
        private static AutomationElement[] MenuItems(AutomationElement root) => Walk(root)
            .Where(e => e.Properties.ControlType.ValueOrDefault == ControlType.MenuItem &&
                e.Properties.BoundingRectangle.ValueOrDefault.Width > 0).ToArray();

        private static IEnumerable<AutomationElement> Walk(AutomationElement element, int depth = 0)
        {
            yield return element;
            if (depth >= 16) yield break;
            foreach (var child in element.FindAllChildren())
                foreach (var descendant in Walk(child, depth + 1)) yield return descendant;
        }

        private AutomationElement? FindPopup(string className)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == processId && NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetClassNameSafe(hwnd) == className)
                { found = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            return found == IntPtr.Zero ? null : automation.FromHandle(found);
        }

        private AutomationElement WaitPopup(string className)
        {
            for (int i = 0; i < 20; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var popup = FindPopup(className);
                if (popup != null) return popup;
                Pause(150);
            }
            throw new InvalidOperationException($"Popup not detected: {className}");
        }

        private void RequireZoomForeground()
        {
            cancellation.ThrowIfCancellationRequested();
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
            if (pid != processId) throw new InvalidOperationException("Zoom lost foreground; no input sent.");
        }

        private void Key(VirtualKeyShort key)
        {
            RequireZoomForeground();
            Keyboard.Type(key);
            Pause(180);
        }

        private void Pause(int milliseconds)
        {
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }
}
