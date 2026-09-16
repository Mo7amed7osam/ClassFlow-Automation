using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.UIAutomation.WaitingRoom;

/// <summary>
/// Production UIA adapter. It never uses screen coordinates, OCR, image matching,
/// keyboard input or mouse input to execute a Waiting Room action.
/// </summary>
public sealed class FlaUiWaitingRoomAdmitExecutor
{
    public bool TryAdmit(IntPtr meetingOrParticipantsHwnd, CancellationToken cancellationToken = default)
    {
        bool admitted = false;
        // The attendance walk scrolls the same list; admission waits for it to finish (a few seconds).
        // Admitting always matters more: after the wait it goes ahead regardless.
        using var gate = ParticipantsPanelGate.TryEnter(TimeSpan.FromSeconds(15), cancellationToken);
        if (gate == null && !cancellationToken.IsCancellationRequested)
            ConsoleLogger.Warn("[WAITING_ROOM] The participants list stayed busy; admitting anyway.");
        try
        {
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var session = new FlaUiWaitingRoomSession(meetingOrParticipantsHwnd);
                admitted = new WaitingRoomUiaAdmitFlow()
                    .TryAdmitWaitingParticipants(session, cancellationToken);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[WAITING_ROOM] UI Automation action failed: {ex.Message}");
        }

        return admitted;
    }
}

internal sealed class FlaUiWaitingRoomSession : IWaitingRoomUiaSession, IDisposable
{
    private static readonly string[] IgnoredNames =
    [
        "Waiting room", "Waiting Room", "Joined", "Message", "Admit", "View", "More",
        "Mute All", "Invite", "Guest"
    ];

    private readonly UIA3Automation _automation = new();
    private readonly IntPtr _hintHwnd;
    private readonly Dictionary<string, AutomationElement> _rows = new(StringComparer.Ordinal);

    public FlaUiWaitingRoomSession(IntPtr hintHwnd)
    {
        _hintHwnd = hintHwnd;
    }

    public IReadOnlyList<WaitingRoomUiaParticipant> ReadWaitingParticipants()
    {
        _rows.Clear();
        var result = new List<WaitingRoomUiaParticipant>();
        foreach (var root in GetRoots())
        {
            foreach (var scope in FindWaitingRoomScopes(root))
            {
                foreach (var row in FindRows(scope))
                {
                    string name = ExtractParticipantName(row);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string key = BuildKey(row, name, result.Count);
                    if (_rows.ContainsKey(key)) continue;
                    _rows[key] = row;
                    result.Add(new WaitingRoomUiaParticipant(key, name));
                }
            }
        }

        // Scrolled, the list can show waiting people above "Joined (n)" with their own header out of
        // view: rows before the Joined header are the waiting room. Only a row's own Admit is ever
        // pressed, which a joined row does not have.
        if (result.Count == 0)
        {
            foreach (var root in GetRoots())
            {
                foreach (var list in FindDescendants(root).Where(e => e.Properties.ControlType.ValueOrDefault == ControlType.List))
                {
                    AutomationElement[] items;
                    try { items = list.FindAllChildren(); } catch { continue; }
                    if (!items.Any(i => GetName(i).StartsWith("Joined", StringComparison.OrdinalIgnoreCase))) continue;
                    foreach (var item in items)
                    {
                        string label = GetName(item);
                        if (label.StartsWith("Joined", StringComparison.OrdinalIgnoreCase)) break;
                        string name = ExtractParticipantName(item);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string key = BuildKey(item, name, result.Count);
                        if (_rows.ContainsKey(key)) continue;
                        _rows[key] = item;
                        result.Add(new WaitingRoomUiaParticipant(key, name));
                    }
                }
            }
        }

        return result;
    }

    public bool RevealRowActions(WaitingRoomUiaParticipant participant)
    {
        if (!TryResolveRow(participant, out var row)) return false;
        try
        {
            if (row.Patterns.ScrollItem.IsSupported)
                row.Patterns.ScrollItem.Pattern.ScrollIntoView();

            // UI Automation has no pointer-hover primitive. Selecting the row is the coordinate-free
            // equivalent Zoom exposes for revealing its actions. Keyboard focus is never set: that
            // would bring Zoom to the front and take the keyboard from whatever the user is typing in.
            if (row.Patterns.SelectionItem.IsSupported)
                row.Patterns.SelectionItem.Pattern.Select();
            return true;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[WAITING_ROOM] Could not expose row actions through UIA: {ex.Message}");
            return false;
        }
    }

    public bool TryInvokeRowAction(WaitingRoomUiaParticipant participant, string actionName)
    {
        if (!TryResolveRow(participant, out var row)) return false;
        var action = FindDescendants(row)
            .FirstOrDefault(element => IsRowAction(element, actionName));
        if (action == null) return false;

        try
        {
            if (!action.Properties.IsEnabled.ValueOrDefault) return false;
            if (action.Patterns.Invoke.IsSupported)
            {
                action.Patterns.Invoke.Pattern.Invoke();
                return true;
            }

            if (action.Patterns.LegacyIAccessible.IsSupported)
            {
                action.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                return true;
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warn($"[WAITING_ROOM] {actionName} UIA invoke failed for {participant.DisplayName}: {ex.Message}");
        }

        return false;
    }

    private bool TryResolveRow(WaitingRoomUiaParticipant participant, out AutomationElement row)
    {
        if (_rows.TryGetValue(participant.Key, out row!)) return true;
        ReadWaitingParticipants();
        if (_rows.TryGetValue(participant.Key, out row!)) return true;
        row = _rows
            .Where(pair => ExtractParticipantName(pair.Value)
                .Equals(participant.DisplayName, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault()!;
        return row != null;
    }

    private IEnumerable<AutomationElement> GetRoots()
    {
        var handles = new HashSet<IntPtr>();
        if (_hintHwnd != IntPtr.Zero && NativeMethods.IsWindow(_hintHwnd)) handles.Add(_hintHwnd);
        var participants = ZoomWindowManager.FindParticipantsWindow();
        if (participants != IntPtr.Zero) handles.Add(participants);

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            string process = NativeMethods.GetProcessNameSafe(hWnd);
            string title = NativeMethods.GetWindowTitleSafe(hWnd);
            if (process.Contains("zoom", StringComparison.OrdinalIgnoreCase) &&
                title.Contains("Participants", StringComparison.OrdinalIgnoreCase))
                handles.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        foreach (var handle in handles)
        {
            AutomationElement? root = null;
            try { root = _automation.FromHandle(handle); }
            catch { }
            if (root != null) yield return root;
        }
    }

    private static IEnumerable<AutomationElement> FindWaitingRoomScopes(AutomationElement root)
    {
        foreach (var element in FindDescendantsAndSelf(root))
        {
            string name = GetName(element);
            if (!name.Contains("Waiting room", StringComparison.OrdinalIgnoreCase)) continue;
            yield return element.Parent ?? element;
        }
    }

    private static IEnumerable<AutomationElement> FindRows(AutomationElement waitingScope)
    {
        var all = FindDescendants(waitingScope).ToArray();
        foreach (var candidate in all)
        {
            string type = candidate.Properties.ControlType.ValueOrDefault.ToString();
            bool rowType = type.Equals(nameof(ControlType.ListItem), StringComparison.OrdinalIgnoreCase) ||
                           type.Equals(nameof(ControlType.DataItem), StringComparison.OrdinalIgnoreCase) ||
                           type.Equals(nameof(ControlType.TreeItem), StringComparison.OrdinalIgnoreCase) ||
                           type.Equals(nameof(ControlType.Group), StringComparison.OrdinalIgnoreCase);
            bool hasScopedAction = FindDescendants(candidate).Any(IsAnyRowAction);
            if (!rowType && !hasScopedAction) continue;

            string name = ExtractParticipantName(candidate);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (FindDescendants(candidate).Any(element =>
                    GetName(element).StartsWith("Joined", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return candidate;
        }
    }

    private static string ExtractParticipantName(AutomationElement row)
    {
        var names = FindDescendantsAndSelf(row)
            .Where(element => !IsButton(element))
            .Select(GetName)
            .Where(IsParticipantName)
            .OrderByDescending(name => name.Length)
            .ToArray();
        return names.FirstOrDefault() ?? string.Empty;
    }

    private static bool IsParticipantName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return false;
        if (IgnoredNames.Any(ignored => name.Equals(ignored, StringComparison.OrdinalIgnoreCase))) return false;
        if (name.Contains("Waiting room", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Joined", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Any(char.IsLetter);
    }

    private static bool IsAnyRowAction(AutomationElement element) =>
        IsRowAction(element, "Admit") || IsRowAction(element, "View") || IsRowAction(element, "More");

    private static bool IsRowAction(AutomationElement element, string expected)
    {
        if (!IsButton(element)) return false;
        string name = GetName(element);
        return name.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsButton(AutomationElement element) =>
        element.Properties.ControlType.ValueOrDefault == ControlType.Button ||
        element.Properties.ControlType.ValueOrDefault == ControlType.MenuItem;

    private static string GetName(AutomationElement element)
    {
        try
        {
            string name = element.Properties.Name.ValueOrDefault ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            if (element.Patterns.LegacyIAccessible.IsSupported)
                return (element.Patterns.LegacyIAccessible.Pattern.Name.ValueOrDefault ?? string.Empty).Trim();
        }
        catch { }
        return string.Empty;
    }

    private static string BuildKey(AutomationElement row, string name, int ordinal)
    {
        try
        {
            var rect = row.Properties.BoundingRectangle.ValueOrDefault;
            return $"{name}|{rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0}";
        }
        catch { return $"{name}|{ordinal}"; }
    }

    private static IEnumerable<AutomationElement> FindDescendantsAndSelf(AutomationElement root)
    {
        yield return root;
        foreach (var child in FindDescendants(root)) yield return child;
    }

    private static IEnumerable<AutomationElement> FindDescendants(AutomationElement root)
    {
        AutomationElement[] children;
        try { children = root.FindAllChildren(); }
        catch { yield break; }
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in FindDescendants(child)) yield return descendant;
        }
    }

    public void Dispose() => _automation.Dispose();
}
