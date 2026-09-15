using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

internal sealed class DesktopWaitingRoomUiaAdmitter
{
    private static readonly string[] ReservedNames =
    [
        "Waiting room", "Waiting Room", "Joined", "Message", "Admit", "View", "More",
        "Mute All", "Invite", "Guest"
    ];

    private WaitingRoomSessionLog TesterLogger { get; }
    private ActionExecutor ActionExecutor { get; }

    public DesktopWaitingRoomUiaAdmitter(Guid sessionId, IWaitingRoomLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        TesterLogger = new WaitingRoomSessionLog(logger, sessionId, SessionEngineType.Desktop);
        ActionExecutor = new ActionExecutor(logger, sessionId, SessionEngineType.Desktop);
    }

    /// <summary>Who the last successful Admit let in: one person, or everyone "Admit all" let in (read before pressing it).</summary>
    public IReadOnlyList<string> LastAdmittedNames { get; private set; } = [];
    private string? LastAdmittedName { set => LastAdmittedNames = value is null ? [] : [value]; }

    public bool TryAdmit(CancellationToken cancellationToken)
    {
        LastAdmittedNames = [];
        bool admitted = false;
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            using var automation = new UIA3Automation();
            foreach (IntPtr handle in FindParticipantsHandles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AutomationElement? root = null;
                try { root = automation.FromHandle(handle); }
                catch { }
                if (root == null) continue;

                if (TryAdmitFromRoot(root, cancellationToken))
                {
                    admitted = true;
                    return;
                }
            }
        });
        return admitted;
    }

    private bool TryAdmitFromRoot(AutomationElement root, CancellationToken cancellationToken)
    {
        // PRIORITY 1: Check if "Admit all" button exists anywhere in the UIA tree first
        var globalAdmitAll = Descendants(root)
            .FirstOrDefault(element => IsButton(element) &&
                (NameOf(element).Equals("Admit all", StringComparison.OrdinalIgnoreCase) ||
                 NameOf(element).Equals("Admit All", StringComparison.OrdinalIgnoreCase) ||
                 NameOf(element).Contains("Admit all", StringComparison.OrdinalIgnoreCase)));

        if (globalAdmitAll != null && globalAdmitAll.Properties.IsEnabled.ValueOrDefault)
        {
            TesterLogger.Desktop("[WAITING_ROOM] [PRIORITY 1] Found 'Admit all' button via UIA");
            var waiting = WaitingNames(root);
            if (ActionExecutor.InvokeDesktopElementUiaOnly(globalAdmitAll, "Admit all"))
            {
                LastAdmittedNames = waiting;
                TesterLogger.Action($"[WAITING_ROOM] Clicked 'Admit all' via UIA ({waiting.Count} named)");
                TesterLogger.Desktop("[WAITING_ROOM] Participant admitted successfully");
                return true;
            }
        }

        bool sawWaitingHeader = false;
        foreach (var header in DescendantsAndSelf(root)
                     .Where(element => NameOf(element).Contains("Waiting room", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sawWaitingHeader = true;
            var scope = header.Parent ?? root;

            // Also check within the header scope for Admit all
            var scopeAdmitAll = Descendants(scope)
                .FirstOrDefault(element => IsButton(element) &&
                    (NameOf(element).Equals("Admit all", StringComparison.OrdinalIgnoreCase) ||
                     NameOf(element).Equals("Admit All", StringComparison.OrdinalIgnoreCase)));

            if (scopeAdmitAll != null && scopeAdmitAll.Properties.IsEnabled.ValueOrDefault)
            {
                TesterLogger.Desktop("[WAITING_ROOM] [PRIORITY 1] Found 'Admit all' button in scope via UIA");
                var waiting = FindWaitingRows(scope, header).Select(ParticipantNameOf).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (ActionExecutor.InvokeDesktopElementUiaOnly(scopeAdmitAll, "Admit all"))
                {
                    LastAdmittedNames = waiting;
                    TesterLogger.Action($"[WAITING_ROOM] Clicked 'Admit all' via UIA ({waiting.Length} named)");
                    TesterLogger.Desktop("[WAITING_ROOM] Participant admitted successfully");
                    return true;
                }
            }

            var rows = FindWaitingRows(scope, header).ToArray();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string participant = ParticipantNameOf(row);
                if (string.IsNullOrWhiteSpace(participant)) continue;

                TesterLogger.Desktop($"[WAITING_ROOM] Participant detected: {participant}");

                var beforeState = GetParticipantState(scope, header, participant);
                TesterLogger.Desktop($"[WAITING_ROOM] Before action state: InWaiting={beforeState.InWaiting}, InJoined={beforeState.InJoined}, WaitingCount={beforeState.WaitingCount?.ToString() ?? "unknown"}");

                TesterLogger.Desktop($"[WAITING_ROOM] Action attempted: Direct Admit for {participant}");
                if (TryInvokeRowAction(row, "Admit", participant))
                {
                    TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                    return true;
                }

                TesterLogger.Desktop($"[WAITING_ROOM] Hovering participant row: {participant}");
                TesterLogger.Desktop($"[WAITING_ROOM] Action attempted: RevealRow for {participant}");
                RevealRow(row);
                Thread.Sleep(500);

                var refreshed = FindSameRow(scope, header, participant, row);
                if (refreshed != null && TryInvokeRowAction(refreshed, "Admit", participant))
                {
                    TesterLogger.Desktop($"[WAITING_ROOM] Actions revealed: {participant}");
                    TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                    return true;
                }

                // Check state after hover attempt
                var afterHoverState = GetParticipantState(scope, header, participant);
                TesterLogger.Desktop($"[WAITING_ROOM] After action state: InWaiting={afterHoverState.InWaiting}, InJoined={afterHoverState.InJoined}, WaitingCount={afterHoverState.WaitingCount?.ToString() ?? "unknown"}");

                if (IsAdmittedOrNoLongerWaiting(beforeState, afterHoverState))
                {
                    if (afterHoverState.InJoined)
                    {
                        TesterLogger.Desktop($"[WAITING_ROOM] Participant moved to Joined");
                    }
                    TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                    return true;
                }

                if (refreshed != null)
                {
                    TesterLogger.Desktop($"[WAITING_ROOM] View required: {participant}");
                    TesterLogger.Desktop($"[WAITING_ROOM] Action attempted: View for {participant}");
                    if (TryInvokeRowAction(refreshed, "View", participant))
                    {
                        TesterLogger.Desktop($"[WAITING_ROOM] View clicked, retrying Admit: {participant}");
                        Thread.Sleep(500);

                        refreshed = FindSameRow(scope, header, participant, refreshed);
                        if (refreshed != null && TryInvokeRowAction(refreshed, "Admit", participant))
                        {
                            TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                            return true;
                        }
                    }
                }

                // Final state verification before reporting failure
                var finalState = GetParticipantState(scope, header, participant);
                TesterLogger.Desktop($"[WAITING_ROOM] After action state: InWaiting={finalState.InWaiting}, InJoined={finalState.InJoined}, WaitingCount={finalState.WaitingCount?.ToString() ?? "unknown"}");

                if (IsAdmittedOrNoLongerWaiting(beforeState, finalState))
                {
                    if (finalState.InJoined)
                    {
                        TesterLogger.Desktop($"[WAITING_ROOM] Participant moved to Joined");
                    }
                    TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                    return true;
                }

                // Only report failure when participant is still in Waiting Room AND Admit action was not invoked
                TesterLogger.Error($"[WAITING_ROOM] Admit not found after hover: {participant}");
            }
        }
        return !sawWaitingHeader && TryAdmitAboveJoined(root, cancellationToken);
    }

    /// <summary>Everyone listed as waiting (under "Waiting room", or above "Joined" when that header scrolled away).</summary>
    private static IReadOnlyList<string> WaitingNames(AutomationElement root)
    {
        try
        {
            var names = new List<string>();
            foreach (var header in DescendantsAndSelf(root).Where(e => NameOf(e).Contains("Waiting room", StringComparison.OrdinalIgnoreCase)).ToArray())
                names.AddRange(FindWaitingRows(header.Parent ?? root, header).Select(ParticipantNameOf));
            if (names.Count == 0)
                foreach (var joined in DescendantsAndSelf(root).Where(e => JoinedHeader.IsMatch(NameOf(e))).ToArray())
                {
                    double joinedY = RectOf(joined).Y;
                    names.AddRange(Descendants(joined.Parent ?? root)
                        .Where(e => RectOf(e) is { Height: > 0 } r && r.Y < joinedY &&
                            e.Properties.ControlType.ValueOrDefault is var t && (t == ControlType.ListItem || t == ControlType.DataItem || t == ControlType.TreeItem))
                        .Select(ParticipantNameOf));
                }
            return names.Where(n => n.Length > 0 && !JoinedHeader.IsMatch(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return []; }
    }

    private static readonly System.Text.RegularExpressions.Regex JoinedHeader =
        new(@"^\s*(joined|in[- ]meeting)\s*\(\d+\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The list scrolls: the "Waiting room (n)" header can be out of view (and out of the UIA tree)
    /// while the people waiting still show above "Joined (n)". Everyone above the Joined header is
    /// then in the waiting room, and is admitted like any other waiting row.
    /// </summary>
    private bool TryAdmitAboveJoined(AutomationElement root, CancellationToken cancellationToken)
    {
        foreach (var joined in DescendantsAndSelf(root).Where(element => JoinedHeader.IsMatch(NameOf(element))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = joined.Parent ?? root;
            double joinedY = RectOf(joined).Y;
            AutomationElement[] RowsAbove() => [.. Descendants(scope).Where(element =>
            {
                var rect = RectOf(element);
                if (rect.Height <= 0 || rect.Y >= joinedY) return false;
                var type = element.Properties.ControlType.ValueOrDefault;
                bool rowType = type == ControlType.ListItem || type == ControlType.DataItem || type == ControlType.TreeItem;
                return rowType || Descendants(element).Any(IsSupportedRowAction);
            }).OrderBy(element => RectOf(element).Y)];

            foreach (var row in RowsAbove())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string participant = ParticipantNameOf(row);
                if (string.IsNullOrWhiteSpace(participant) || JoinedHeader.IsMatch(participant)) continue;
                TesterLogger.Desktop($"[WAITING_ROOM] Waiting above Joined (header scrolled away): {participant}");
                if (TryInvokeRowAction(row, "Admit", participant)) return true;
                RevealRow(row);
                Thread.Sleep(400);
                var again = RowsAbove().FirstOrDefault(r => ParticipantNameOf(r).Equals(participant, StringComparison.OrdinalIgnoreCase));
                if (again != null && TryInvokeRowAction(again, "Admit", participant)) return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<IntPtr> FindParticipantsHandles()
    {
        var handles = new List<IntPtr>();
        var direct = ZoomWindowManager.FindParticipantsWindow();
        if (direct != IntPtr.Zero) handles.Add(direct);

        foreach (var candidate in new ZoomProcessDiscovery().FindCandidates())
        {
            foreach (var window in candidate.Windows)
            {
                if (window.Title.Contains("Participants", StringComparison.OrdinalIgnoreCase))
                    handles.Add(window.Handle);
            }
        }

        // A docked Participants panel is part of the meeting HWND rather than a separate window.
        var meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
        if (meeting != IntPtr.Zero) handles.Add(meeting);
        return handles.Where(handle => handle != IntPtr.Zero && NativeMethods.IsWindow(handle)).Distinct().ToArray();
    }

    private static IEnumerable<AutomationElement> FindWaitingRows(AutomationElement scope, AutomationElement header)
    {
        double waitingY = RectOf(header).Y;
        double joinedY = Descendants(scope)
            .Where(element => NameOf(element).StartsWith("Joined", StringComparison.OrdinalIgnoreCase))
            .Select(element => RectOf(element).Y)
            .Where(y => y > waitingY)
            .DefaultIfEmpty(double.MaxValue)
            .Min();

        var candidates = Descendants(scope).Where(element =>
        {
            var rect = RectOf(element);
            if (rect.Height <= 0 || rect.Y <= waitingY || rect.Y >= joinedY) return false;
            var type = element.Properties.ControlType.ValueOrDefault;
            bool rowType = type == ControlType.ListItem || type == ControlType.DataItem || type == ControlType.TreeItem;
            bool ownsAction = Descendants(element).Any(IsSupportedRowAction);
            return rowType || ownsAction;
        });

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.OrderBy(element => RectOf(element).Y))
        {
            string participant = ParticipantNameOf(candidate);
            if (string.IsNullOrWhiteSpace(participant)) continue;
            var rect = RectOf(candidate);
            string key = $"{participant}|{rect.Y:F0}|{rect.Height:F0}";
            if (emitted.Add(key)) yield return candidate;
        }
    }

    private static AutomationElement? FindSameRow(
        AutomationElement scope,
        AutomationElement header,
        string participant,
        AutomationElement previous)
    {
        double previousY = RectOf(previous).Y;
        return FindWaitingRows(scope, header)
            .Where(row => ParticipantNameOf(row).Equals(participant, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => Math.Abs(RectOf(row).Y - previousY))
            .FirstOrDefault();
    }

    private void RevealRow(AutomationElement row)
    {
        try
        {
            if (row.Patterns.ScrollItem.IsSupported)
                row.Patterns.ScrollItem.Pattern.ScrollIntoView();
            // Never Focus(): it brings Zoom to the front and takes the keyboard from the user.
            if (row.Patterns.SelectionItem.IsSupported)
                row.Patterns.SelectionItem.Pattern.Select();
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"[WAITING_ROOM] Row UIA reveal failed: {ex.Message}");
        }
    }

    private bool TryInvokeRowAction(AutomationElement row, string actionName, string participant)
    {
        var action = Descendants(row).FirstOrDefault(element =>
            IsButton(element) && NameOf(element).Equals(actionName, StringComparison.OrdinalIgnoreCase));
        if (action == null || !action.Properties.IsEnabled.ValueOrDefault) return false;

        bool invoked = ActionExecutor.InvokeDesktopElementUiaOnly(action, actionName);
        if (!invoked) return false;
        if (actionName.Equals("Admit", StringComparison.OrdinalIgnoreCase))
        {
            LastAdmittedName = participant;
            TesterLogger.Desktop($"[WAITING_ROOM] Admit button found: {participant}");
            TesterLogger.Action($"[WAITING_ROOM] Admit invoked successfully: {participant}");
        }
        return true;
    }

    private static string ParticipantNameOf(AutomationElement row) => DescendantsAndSelf(row)
        .Where(element => !IsButton(element))
        .Select(NameOf)
        .Where(IsParticipantName)
        .OrderByDescending(name => name.Length)
        .FirstOrDefault() ?? string.Empty;

    private static bool IsParticipantName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Any(char.IsLetter) &&
        !ReservedNames.Any(value => name.Equals(value, StringComparison.OrdinalIgnoreCase)) &&
        !name.Contains("Waiting room", StringComparison.OrdinalIgnoreCase) &&
        !name.StartsWith("Joined", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedRowAction(AutomationElement element) =>
        IsButton(element) && new[] { "Admit", "View", "More" }.Contains(NameOf(element), StringComparer.OrdinalIgnoreCase);

    private static bool IsButton(AutomationElement element) =>
        element.Properties.ControlType.ValueOrDefault == ControlType.Button ||
        element.Properties.ControlType.ValueOrDefault == ControlType.MenuItem;

    private static string NameOf(AutomationElement element)
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

    private static ElementRect RectOf(AutomationElement element)
    {
        try
        {
            var rect = element.Properties.BoundingRectangle.ValueOrDefault;
            return new ElementRect(rect.X, rect.Y, rect.Width, rect.Height);
        }
        catch { return default; }
    }

    private readonly record struct ElementRect(double X, double Y, double Width, double Height);

    private static IEnumerable<AutomationElement> DescendantsAndSelf(AutomationElement root)
    {
        yield return root;
        foreach (var element in Descendants(root)) yield return element;
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root)
    {
        AutomationElement[] children;
        try { children = root.FindAllChildren(); }
        catch { yield break; }
        foreach (var child in children)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private readonly record struct ParticipantState(bool InWaiting, bool InJoined, int? WaitingCount);

    private static ParticipantState GetParticipantState(AutomationElement scope, AutomationElement header, string participant)
    {
        int? count = GetWaitingCount(header);
        bool inWaiting = false;
        try
        {
            inWaiting = FindWaitingRows(scope, header)
                .Any(row =>
                {
                    string name = ParticipantNameOf(row);
                    return IsNameMatch(name, participant);
                });
        }
        catch { }

        bool inJoined = false;
        try
        {
            inJoined = FindJoinedParticipantNames(scope)
                .Any(name => IsNameMatch(name, participant));
        }
        catch { }

        return new ParticipantState(inWaiting, inJoined, count);
    }

    private static bool IsAdmittedOrNoLongerWaiting(ParticipantState before, ParticipantState current)
    {
        if (current.InJoined) return true;
        if (!current.InWaiting) return true;
        if (before.WaitingCount.HasValue && current.WaitingCount.HasValue && current.WaitingCount.Value < before.WaitingCount.Value) return true;
        return false;
    }

    private static bool IsNameMatch(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetWaitingCount(AutomationElement header)
    {
        try
        {
            string name = NameOf(header);
            var match = System.Text.RegularExpressions.Regex.Match(name, @"\((\d+)\)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                return count;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> FindJoinedParticipantNames(AutomationElement scope)
    {
        var joinedHeader = Descendants(scope)
            .FirstOrDefault(element => NameOf(element).StartsWith("Joined", StringComparison.OrdinalIgnoreCase));
        if (joinedHeader == null) yield break;

        double joinedY = RectOf(joinedHeader).Y;
        var candidates = Descendants(scope).Where(element =>
        {
            var rect = RectOf(element);
            return rect.Height > 0 && rect.Y > joinedY;
        });

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            string name = ParticipantNameOf(candidate);
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                yield return name;
            }
        }
    }
}
