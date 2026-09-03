using System.Diagnostics;
using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.WindowsRuntime;

public sealed class WindowsDesktopMeetingPlatform : IWindowsDesktopMeetingPlatform
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(30);
    // Zoom's meeting tree can be enormous when panels are open; an unbounded walk previously
    // stalled meeting startup for over a minute per control.
    private static readonly TimeSpan MeetingControlSearchBudget = TimeSpan.FromSeconds(8);
    // A single blocking UIA/COM call never reaches the budget check above, so the desktop thread
    // itself gets a hard wall-clock limit. Without it each control cost the full 60 s default.
    private const uint MeetingControlThreadTimeoutMs = 12000;

    private readonly Func<string, string?> _resolveSwitchEmail;
    private readonly Func<string, CancellationToken, Task<MeetingOperationResult>> _switchAccount;

    public WindowsDesktopMeetingPlatform()
        : this(WindowsCredentialManagerReferenceResolver.TryGetUsername,
            (email, token) => new WindowsKeyboardAccountSwitcher().SwitchAsync(email, token)) { }

    internal WindowsDesktopMeetingPlatform(Func<string, string?> resolveEmail,
        Func<string, CancellationToken, Task<MeetingOperationResult>> switchAccount)
    {
        _resolveSwitchEmail = resolveEmail;
        _switchAccount = switchAccount;
    }

    public async Task<MeetingOperationResult> SwitchAccountAsync(
        MeetingAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? email = WindowsMeetingAccountManager.NormalizeZoomEmail(account.ZoomEmail);
        if (email == null)
        {
            ConsoleLogger.Warn($"[ACCOUNT_SWITCH] Legacy account {account.AccountId}: configure Zoom Email to stop relying on credential usernames.");
            email = _resolveSwitchEmail(account.CredentialReference);
        }
        if (string.IsNullOrWhiteSpace(email))
        {
            const string error = "The target account email could not be resolved from Windows Credential Manager.";
            ConsoleLogger.Error($"[ACCOUNT_SWITCH] Failure: {error}");
            return MeetingOperationResult.Failure(error);
        }

        ConsoleLogger.Info($"[ACCOUNT_SWITCH] Selecting account: {email}");
        var result = await _switchAccount(email, cancellationToken);
        if (result.IsSuccess) ConsoleLogger.Success($"[ACCOUNT_SWITCH] Success: {email}");
        else ConsoleLogger.Error($"[ACCOUNT_SWITCH] Failure: {result.ErrorMessage}");
        return result;
    }

    public async Task<MeetingOperationResult> LaunchMeetingAsync(
        Uri meetingUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() =>
            {
                var result = MeetingOperationResult.Failure("Desktop launch did not complete.");
                DesktopThread.RunOnInteractiveDesktop(() =>
                {
                    using var automation = new UIA3Automation();
                    result = new DesktopMeetingLaunchFlow(new WindowsDesktopJoinActions(automation))
                        .Run(meetingUrl, cancellationToken);
                }, uint.MaxValue);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return MeetingOperationResult.Failure(ex.GetBaseException().Message);
        }
    }

    public async Task<MeetingOperationResult> VerifyJoinedAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + JoinTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new ZoomProcessDiscovery().FindCandidates(logInfo: false).SelectMany(c => c.Windows)
                .Any(w => (w.IsVisible || NativeMethods.IsIconic(w.Handle)) &&
                    ZoomWindowManager.ClassifyZoomWindow(w.Handle) == ZoomWindowRole.MeetingWindow))
                return MeetingOperationResult.Success();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return MeetingOperationResult.Failure("Zoom Desktop meeting window was not detected within 30 seconds.");
    }

    public Task<MeetingOperationResult> DisableMicrophoneAsync(CancellationToken cancellationToken) =>
        SetMeetingControlOffAsync("Mute", "Unmute", "microphone", cancellationToken);

    public Task<MeetingOperationResult> DisableCameraAsync(CancellationToken cancellationToken) =>
        SetMeetingControlOffAsync("Stop Video", "Start Video", "camera", cancellationToken);

    public Task<MeetingOperationResult> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Stopping orchestration intentionally does not end the user's Zoom meeting.
        return Task.FromResult(MeetingOperationResult.Success());
    }

    internal static string CreateZoomDesktopProtocolUrl(Uri meetingUrl)
    {
        string meetingId = DesktopMeetingLaunchFlow.ExtractMeetingId(meetingUrl);
        string password = meetingUrl.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2 && part[0].Equals("pwd", StringComparison.OrdinalIgnoreCase))
            .Select(part => Uri.UnescapeDataString(part[1]))
            .FirstOrDefault() ?? string.Empty;
        string passwordPart = string.IsNullOrWhiteSpace(password)
            ? string.Empty
            : $"&pwd={Uri.EscapeDataString(password)}";
        return $"zoommtg://zoom.us/join?action=join&confno={meetingId}{passwordPart}";
    }

    private static Task<MeetingOperationResult> SetMeetingControlOffAsync(
        string turnOffName,
        string alreadyOffName,
        string controlLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MeetingOperationResult result = MeetingOperationResult.Failure(
            $"Zoom Desktop {controlLabel} control was not found.");
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr meetingWindow = ZoomWindowManager.FindMainZoomMeetingWindow();
            if (meetingWindow == IntPtr.Zero) return;
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(meetingWindow);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + MeetingControlSearchBudget;
            var match = FindFirstNamedElement(root, [alreadyOffName, turnOffName], deadline);
            if (match == null)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    ConsoleLogger.Warn(
                        $"[PREPARE] Zoom Desktop {controlLabel} control search exceeded {MeetingControlSearchBudget.TotalSeconds:0}s; skipping");
                return;
            }
            if (match.Value.Name.Equals(alreadyOffName, StringComparison.OrdinalIgnoreCase))
            {
                result = MeetingOperationResult.Success();
                return;
            }
            if (Invoke(match.Value.Element)) result = MeetingOperationResult.Success();
        }, MeetingControlThreadTimeoutMs);
        return Task.FromResult(result);
    }

    private static AutomationElement? FindProfileButton(AutomationElement element, HashSet<int> zoomPids)
    {
        try
        {
            if (!zoomPids.Contains(element.Properties.ProcessId.ValueOrDefault)) return null;
            string controlType = element.Properties.ControlType.ValueOrDefault.ToString();
            string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            bool enabled = element.Properties.IsEnabled.ValueOrDefault;
            bool hasAction = element.Patterns.Invoke.IsSupported ||
                             element.Patterns.ExpandCollapse.IsSupported ||
                             element.Patterns.LegacyIAccessible.IsSupported;

            if (ProfileButtonMatcher.IsProfileSplitButton(controlType, name, enabled, element.Patterns.Invoke.IsSupported) ||
                (enabled && hasAction && name.StartsWith("Zoom,", StringComparison.OrdinalIgnoreCase) && name.IndexOf("Status", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (enabled && (controlType == "Button" || controlType == "SplitButton") && (name.Contains("Licensed account", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic account", StringComparison.OrdinalIgnoreCase))))
            {
                return element;
            }

            foreach (var child in element.FindAllChildren())
            {
                var match = FindProfileButton(child, zoomPids);
                if (match != null) return match;
            }
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<IntPtr> GetZoomRootHandles(
        IReadOnlyList<ZoomProcessCandidate> candidates,
        HashSet<int> zoomPids)
    {
        var roots = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd))
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                string className = NativeMethods.GetClassNameSafe(hWnd);
                if (zoomPids.Contains((int)pid) &&
                    (className.Contains("MainFrm", StringComparison.OrdinalIgnoreCase) ||
                     className.StartsWith("ZPPT", StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(hWnd);
                }
            }
            return true;
        }, IntPtr.Zero);

        foreach (var candidate in candidates)
        {
            if (candidate.MainWindowHandle != IntPtr.Zero) roots.Add(candidate.MainWindowHandle);
            roots.AddRange(candidate.Windows
                .Where(window => window.IsVisible &&
                    (window.ClassName.Contains("MainFrm", StringComparison.OrdinalIgnoreCase) ||
                     window.ClassName.StartsWith("ZPPT", StringComparison.OrdinalIgnoreCase)))
                .Select(window => window.Handle));
        }
        return roots.Where(handle => handle != IntPtr.Zero).Distinct().ToArray();
    }

    private static AutomationElement? FindProfileButtonInRoots(
        UIA3Automation automation,
        IEnumerable<IntPtr> roots,
        HashSet<int> zoomPids)
    {
        foreach (var handle in roots)
        {
            try
            {
                var root = automation.FromHandle(handle);
                if (root == null) continue;
                var profile = FindProfileButton(root, zoomPids);
                if (profile != null) return profile;
            }
            catch { }
        }
        return null;
    }

    /// <summary>One bounded depth-first walk that matches any of the given exact names.</summary>
    private static (AutomationElement Element, string Name)? FindFirstNamedElement(
        AutomationElement element,
        IReadOnlyList<string> names,
        DateTimeOffset deadline)
    {
        try
        {
            if (DateTimeOffset.UtcNow >= deadline) return null;
            string candidate = (element.Properties.Name.ValueOrDefault ?? string.Empty).Trim();
            foreach (var name in names)
                if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) return (element, name);
            foreach (var child in element.FindAllChildren())
            {
                var match = FindFirstNamedElement(child, names, deadline);
                if (match != null) return match;
                if (DateTimeOffset.UtcNow >= deadline) return null;
            }
        }
        catch { }
        return null;
    }

    private static bool Invoke(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return true;
            }
            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return true;
            }
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
                return true;
            }
            if (element.Patterns.LegacyIAccessible.IsSupported)
            {
                element.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool ExpandOrInvoke(AutomationElement element)
    {
        try
        {
            if (element.Patterns.ExpandCollapse.IsSupported)
            {
                element.Patterns.ExpandCollapse.Pattern.Expand();
                return true;
            }
        }
        catch { }
        return Invoke(element);
    }

    private static void TryStartZoomDesktop()
    {
        try
        {
            string[] candidatePaths =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zoom", "bin", "Zoom.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zoom", "bin", "Zoom.exe")
            ];

            string? zoomExe = candidatePaths.FirstOrDefault(File.Exists);
            if (zoomExe != null)
            {
                Process.Start(new ProcessStartInfo(zoomExe) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Debug($"Failed to launch Zoom Desktop: {ex.Message}");
        }
    }

    private static void EnsureZoomWindowActive(ZoomProcessCandidate candidate, UIA3Automation automation)
    {
        try
        {
            var visibleOrIconic = candidate.Windows
                .Where(w => w.IsVisible || (candidate.MainWindowHandle != IntPtr.Zero && w.Handle == candidate.MainWindowHandle))
                .Select(w => w.Handle)
                .Append(candidate.MainWindowHandle)
                .Where(h => h != IntPtr.Zero)
                .Distinct();

            foreach (var handle in visibleOrIconic)
            {
                NativeMethods.ForceForegroundWindow(handle);
            }
        }
        catch { }
    }

    private static AutomationElement? FindSwitchAccountMenuItem(AutomationElement element, HashSet<int> zoomPids)
    {
        try
        {
            if (!zoomPids.Contains(element.Properties.ProcessId.ValueOrDefault)) return null;
            string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            var controlType = element.Properties.ControlType.ValueOrDefault;
            if (controlType == ControlType.MenuItem &&
                (name.IndexOf("switch account", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("switch to another account", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return element;
            }

            foreach (var child in element.FindAllChildren())
            {
                var match = FindSwitchAccountMenuItem(child, zoomPids);
                if (match != null) return match;
            }
        }
        catch { }
        return null;
    }

    private static AutomationElement? FindSwitchAccountMenuItemInRoots(
        IEnumerable<AutomationElement> roots,
        HashSet<int> zoomPids)
    {
        foreach (var root in roots)
        {
            var item = FindSwitchAccountMenuItem(root, zoomPids);
            if (item != null) return item;
        }
        return null;
    }

    private static AutomationElement? WaitForSwitchAccountSubmenu(
        UIA3Automation automation,
        HashSet<int> zoomPids,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutomationElement? submenu = null;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (!zoomPids.Contains((int)pid)) return true;
                if (!NativeMethods.GetClassNameSafe(hWnd).Equals(
                        "ZPPTSwitchAccountSubMenuWndClass",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                try { submenu = automation.FromHandle(hWnd); }
                catch { }
                return submenu == null;
            }, IntPtr.Zero);

            if (submenu != null) return submenu;
            Thread.Sleep(100);
        }
        return null;
    }

    private static IReadOnlyList<AutomationElement> EnumerateMenuItems(
        AutomationElement submenu,
        HashSet<int> zoomPids)
    {
        var items = new List<AutomationElement>();
        CollectMenuItems(submenu, zoomPids, items);
        return items;
    }

    private static void CollectMenuItems(
        AutomationElement element,
        HashSet<int> zoomPids,
        List<AutomationElement> items)
    {
        try
        {
            if (!zoomPids.Contains(element.Properties.ProcessId.ValueOrDefault)) return;
            if (element.Properties.ControlType.ValueOrDefault == ControlType.MenuItem)
                items.Add(element);
            foreach (var child in element.FindAllChildren())
                CollectMenuItems(child, zoomPids, items);
        }
        catch { }
    }

    private static string GetElementSearchText(AutomationElement element)
    {
        try
        {
            return string.Join(' ',
                element.Properties.Name.ValueOrDefault ?? string.Empty,
                element.Properties.HelpText.ValueOrDefault ?? string.Empty,
                element.Properties.AutomationId.ValueOrDefault ?? string.Empty);
        }
        catch { return string.Empty; }
    }

    internal static bool TextContainsEmail(string value, string email) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.IsNullOrWhiteSpace(email) &&
        value.Contains(email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ElementContainsEmail(AutomationElement element, string email) =>
        TextContainsEmail(GetElementSearchText(element), email);

    private static bool InvokeMenuItem(AutomationElement item)
    {
        try
        {
            if (item.Properties.ControlType.ValueOrDefault != ControlType.MenuItem) return false;
        }
        catch { return false; }
        return Invoke(item);
    }

    private static bool TreeContainsEmail(AutomationElement element, string email)
    {
        try
        {
            if (ElementContainsEmail(element, email)) return true;
            foreach (var child in element.FindAllChildren())
            {
                if (TreeContainsEmail(child, email)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool TreeContainsEmailAddress(AutomationElement element)
    {
        try
        {
            if (GetElementSearchText(element).Contains('@')) return true;
            foreach (var child in element.FindAllChildren())
            {
                if (TreeContainsEmailAddress(child)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string GetTreeSearchText(AutomationElement element)
    {
        var values = new List<string>();
        CollectTreeSearchText(element, values);
        return string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
    }

    private static void CollectTreeSearchText(AutomationElement element, List<string> values)
    {
        try
        {
            string current = GetElementSearchText(element).Trim();
            if (!string.IsNullOrWhiteSpace(current)) values.Add(current);
            foreach (var child in element.FindAllChildren())
                CollectTreeSearchText(child, values);
        }
        catch { }
    }

    private static bool VerifyActiveAccountByEmail(
        UIA3Automation automation,
        ZoomProcessDiscovery discovery,
        string targetEmail,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        bool profileMenuRequested = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = discovery.FindCandidates(logInfo: false);
            var zoomPids = candidates.Select(candidate => candidate.ProcessId).ToHashSet();
            var popupContainers = GetPopupElements(automation, zoomPids);

            foreach (var popup in popupContainers)
            {
                string className;
                try { className = popup.Properties.ClassName.ValueOrDefault ?? string.Empty; }
                catch { continue; }
                if (className.Equals("ZPPTSwitchAccountSubMenuWndClass", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TreeContainsEmail(popup, targetEmail)) return true;
            }

            var roots = GetZoomRootHandles(candidates, zoomPids);
            var profileButton = FindProfileButtonInRoots(automation, roots, zoomPids);
            if (profileButton != null && ElementContainsEmail(profileButton, targetEmail)) return true;

            bool submenuStillOpen = popupContainers.Any(popup =>
            {
                try
                {
                    return popup.Properties.ClassName.ValueOrDefault.Equals(
                        "ZPPTSwitchAccountSubMenuWndClass",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });

            if (!profileMenuRequested && !submenuStillOpen && profileButton != null)
            {
                profileMenuRequested = ExpandOrInvoke(profileButton);
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static List<AutomationElement> GetPopupElements(UIA3Automation automation, HashSet<int> zoomPids)
    {
        var list = new List<AutomationElement>();
        try
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                    if (zoomPids.Contains((int)pid))
                    {
                        var cls = NativeMethods.GetClassNameSafe(hWnd);
                        if (cls.StartsWith("zCustomized", StringComparison.OrdinalIgnoreCase) ||
                            cls.StartsWith("ZPMenu", StringComparison.OrdinalIgnoreCase) ||
                            cls.Equals("ZPPTSwitchAccountSubMenuWndClass", StringComparison.OrdinalIgnoreCase) ||
                            cls.StartsWith("PopupContainer", StringComparison.OrdinalIgnoreCase) ||
                            cls.StartsWith("ZAic", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var elem = automation.FromHandle(hWnd);
                                if (elem != null) list.Add(elem);
                            }
                            catch { }
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);

            foreach (var child in automation.GetDesktop().FindAllChildren())
            {
                try
                {
                    if (zoomPids.Contains(child.Properties.ProcessId.ValueOrDefault))
                    {
                        var cls = child.Properties.ClassName.ValueOrDefault ?? "";
                        if (cls.StartsWith("zCustomized", StringComparison.OrdinalIgnoreCase) ||
                            cls.Equals("ZPPTSwitchAccountSubMenuWndClass", StringComparison.OrdinalIgnoreCase) ||
                            cls.StartsWith("ZPMenu", StringComparison.OrdinalIgnoreCase) ||
                            cls.StartsWith("PopupContainer", StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(child);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}
