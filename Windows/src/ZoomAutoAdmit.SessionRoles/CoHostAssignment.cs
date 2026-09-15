using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using System.Text.RegularExpressions;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.SessionRoles;

public sealed record CoHostOutcome(bool Success, string Message, bool AlreadyCoHost = false);

public interface ICoHostAssigner
{
    CoHostOutcome Assign(string observedDisplayName, CancellationToken token = default);
}

/// <summary>
/// Grants co-host through Zoom's own accessibility tree: open the participants panel, find the
/// person's row, invoke that row's "More options for …" split button, invoke "Make co-host", then
/// verify from the row label. No OCR, no screen coordinates, no cursor movement, no keystrokes.
/// The panel may be docked or floating; only element names are used.
/// </summary>
public sealed class ZoomCoHostAssigner : ICoHostAssigner
{
    private static readonly Regex ParticipantListName = new(
        @"^participant list\b|^(joined|in[- ]meeting)(\s+participants)?(\s+list)?(\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SectionHeader = new(@"^(waiting room|joined|in[- ]meeting)\s*\(\d+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WaitingHeader = new(@"^waiting room", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CoHostLabel = new(@"co-?host", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MakeCoHostItem = new(@"^make\s+co-?host$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowMoreButton = new(@"^more options for\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TimeSpan _menuWait;
    private readonly TimeSpan _verifyWait;

    public ZoomCoHostAssigner(TimeSpan? menuWait = null, TimeSpan? verifyWait = null)
    {
        _menuWait = menuWait ?? TimeSpan.FromMilliseconds(600);
        _verifyWait = verifyWait ?? TimeSpan.FromMilliseconds(900);
    }

    /// <summary>A row reads "Name,(Guest), Computer audio muted…"; only the leading part is the name.</summary>
    public static string CleanRowName(string label)
    {
        int marker = label.IndexOf(",(", StringComparison.Ordinal);
        if (marker > 0) return label[..marker].Trim();
        int comma = label.IndexOf(',');
        return (comma > 0 ? label[..comma] : label).Trim();
    }

    public CoHostOutcome Assign(string observedDisplayName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(observedDisplayName)) return new(false, "No participant name supplied.");
        CoHostOutcome outcome = new(false, "The assignment did not run.");
        DesktopThread.RunOnInteractiveDesktop(() =>
        {
            using var automation = new UIA3Automation();
            var process = ResolveMeetingProcess();
            if (process == null) { outcome = new(false, "No Zoom meeting window is available."); return; }

            var list = FindParticipantList(automation, process);
            if (list == null && TryOpenParticipantsPanel(automation, process))
            {
                Thread.Sleep(_menuWait);
                list = FindParticipantList(automation, process);
            }
            if (list == null) { outcome = new(false, "The participants panel is not exposed; open it and retry."); return; }

            var row = FindJoinedRow(list, observedDisplayName);
            if (row == null) { outcome = new(false, $"\"{observedDisplayName}\" is not in the joined list right now."); return; }
            if (CoHostLabel.IsMatch(SafeName(row))) { outcome = new(true, $"{observedDisplayName} is already a co-host.", AlreadyCoHost: true); return; }

            var more = row.FindAllDescendants(cf => cf.ByControlType(ControlType.SplitButton))
                .Concat(row.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                .FirstOrDefault(element => RowMoreButton.IsMatch(SafeName(element)));
            if (more == null)
            { outcome = new(false, "That participant's More menu is not exposed."); return; }

            // "More options for …" is a split button: its main half is the participant's mic
            // (Mute / Ask to unmute) and only its arrow opens the menu. Invoking it pressed the mic
            // and muted the instructor, so the menu is only ever opened (Expand), never invoked.
            token.ThrowIfCancellationRequested();
            if (!OpenRowMenu(more, out var opened))
            { outcome = new(false, "That participant's More menu could not be opened without pressing the mic, so nothing was pressed."); return; }
            Thread.Sleep(_menuWait);

            var makeCoHost = FindMakeCoHostItem(automation, process.ProcessId);
            if (makeCoHost == null)
            {
                // Leave Zoom as it was found: close the menu that was opened (never press the mic).
                CloseRowMenu(opened);
                outcome = new(false, "The \"Make co-host\" item did not appear in the row menu.");
                return;
            }
            makeCoHost.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(_menuWait);
            // Zoom asks "Make <name> a co-host" with Confirm / Cancel; only Confirm is pressed.
            PressCoHostConfirm(automation, process.ProcessId, observedDisplayName);
            Thread.Sleep(_verifyWait);

            // Verify from Zoom itself rather than assuming the click worked.
            var refreshedList = FindParticipantList(automation, process);
            var refreshedRow = refreshedList == null ? null : FindJoinedRow(refreshedList, observedDisplayName);
            bool confirmed = refreshedRow != null && CoHostLabel.IsMatch(SafeName(refreshedRow));
            if (!confirmed) confirmed = AnnouncementConfirms(automation, process, observedDisplayName);
            outcome = confirmed
                ? new(true, $"{observedDisplayName} is now a co-host.")
                : new(false, $"Zoom did not confirm co-host for {observedDisplayName}; nothing else was changed.");
        }, uint.MaxValue);
        return outcome;
    }

    /// <summary>
    /// Opens a row's More menu without its main action. A split button is expanded (its arrow);
    /// a split button that cannot be expanded is opened through its own arrow/"more" child; a plain
    /// button (older Zoom) only opens a menu, so it may be invoked. Anything else is left alone.
    /// </summary>
    internal static bool OpenRowMenu(AutomationElement more, out AutomationElement opened)
    {
        opened = more;
        try
        {
            if (more.Patterns.ExpandCollapse.IsSupported)
            {
                more.Patterns.ExpandCollapse.Pattern.Expand();
                return true;
            }
            bool split = more.Properties.ControlType.ValueOrDefault == ControlType.SplitButton;
            if (split)
            {
                var arrow = more.FindAllChildren().FirstOrDefault(child =>
                {
                    string name = SafeName(child);
                    return child.Patterns.ExpandCollapse.IsSupported ||
                           (name.Length > 0 && !MicAction.IsMatch(name) &&
                            (name.Contains("more", StringComparison.OrdinalIgnoreCase) || name.Contains("open", StringComparison.OrdinalIgnoreCase)));
                });
                if (arrow == null) return false;
                opened = arrow;
                if (arrow.Patterns.ExpandCollapse.IsSupported) arrow.Patterns.ExpandCollapse.Pattern.Expand();
                else if (arrow.Patterns.Invoke.IsSupported) arrow.Patterns.Invoke.Pattern.Invoke();
                else return false;
                return true;
            }
            if (MicAction.IsMatch(SafeName(more)) || !more.Patterns.Invoke.IsSupported) return false;
            more.Patterns.Invoke.Pattern.Invoke();
            return true;
        }
        catch { return false; }
    }

    private static void CloseRowMenu(AutomationElement opened)
    {
        try
        {
            if (opened.Patterns.ExpandCollapse.IsSupported) opened.Patterns.ExpandCollapse.Pattern.Collapse();
        }
        catch { }
    }

    private static readonly Regex MicAction = new(@"\b(mute|unmute|ask to unmute|audio|microphone|mic)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ZoomProcessCandidate? ResolveMeetingProcess()
    {
        var candidates = new ZoomProcessDiscovery().FindCandidates(logInfo: false)
            .Where(candidate => candidate.ProcessName.Equals("Zoom", StringComparison.OrdinalIgnoreCase) ||
                                candidate.ProcessName.Equals("CptHost", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0) return null;
        foreach (var handle in new[] { ZoomWindowManager.FindParticipantsWindow(), ZoomWindowManager.FindMainZoomMeetingWindow() })
        {
            if (handle == IntPtr.Zero) continue;
            NativeMethods.GetWindowThreadProcessId(handle, out var owner);
            var match = candidates.FirstOrDefault(candidate => candidate.ProcessId == (int)owner);
            if (match != null) return match;
        }
        var visible = candidates.Where(candidate => candidate.Windows.Any(window => window.IsVisible)).ToArray();
        return visible.Length == 1 ? visible[0] : null;
    }

    private static AutomationElement[] Windows(UIA3Automation automation, ZoomProcessCandidate process) =>
        process.Windows.Where(window => window.IsVisible)
            .Select(window => automation.FromHandle(window.Handle))
            .Where(element => element != null)
            .ToArray()!;

    private static AutomationElement? FindParticipantList(UIA3Automation automation, ZoomProcessCandidate process) =>
        Windows(automation, process)
            .SelectMany(window => window.FindAllDescendants(cf => cf.ByControlType(ControlType.List)))
            .FirstOrDefault(element => ParticipantListName.IsMatch(SafeName(element)) && !element.Properties.IsOffscreen.ValueOrDefault);

    /// <summary>Only rows under the Joined header are eligible; a waiting-room row is never assigned a role.</summary>
    private static AutomationElement? FindJoinedRow(AutomationElement list, string displayName)
    {
        var rows = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
        // With the list scrolled, waiting people can show above "Joined (n)" without their own header.
        bool joined = !rows.Any(r => SectionHeader.IsMatch(SafeName(r)) && !WaitingHeader.IsMatch(SafeName(r)));
        foreach (var row in rows)
        {
            string label = SafeName(row);
            if (label.Length == 0) continue;
            if (SectionHeader.IsMatch(label)) { joined = !WaitingHeader.IsMatch(label); continue; }
            if (!joined) continue;
            if (string.Equals(CleanRowName(label), displayName.Trim(), StringComparison.OrdinalIgnoreCase)) return row;
        }
        return null;
    }

    private static AutomationElement? FindMakeCoHostItem(UIA3Automation automation, int processId)
    {
        // The menu is a separate popup window, so the process windows are enumerated again.
        var process = new ZoomProcessDiscovery().FindCandidates(logInfo: false)
            .FirstOrDefault(candidate => candidate.ProcessId == processId);
        if (process == null) return null;
        return Windows(automation, process)
            .SelectMany(window => window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
            .FirstOrDefault(element => MakeCoHostItem.IsMatch(SafeName(element)) &&
                                       element.Patterns.Invoke.IsSupported &&
                                       !element.Properties.IsOffscreen.ValueOrDefault);
    }

    private static readonly Regex ConfirmButton = new(@"^\s*(confirm|yes|make co-?host)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The confirmation Zoom shows after "Make co-host": a small window reading "Make … a co-host"
    /// with Confirm and Cancel. It is a new window, so the process's windows are read again.
    /// Waits up to two seconds for it; no dialog (older Zoom) is not a failure.
    /// </summary>
    public static bool PressCoHostConfirm(UIA3Automation automation, int processId, string displayName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var process = new ZoomProcessDiscovery().FindCandidates(logInfo: false).FirstOrDefault(c => c.ProcessId == processId);
                if (process == null) return false;
                foreach (var window in Windows(automation, process))
                {
                    var all = window.FindAllDescendants();
                    bool isCoHostDialog = all.Any(e => SafeName(e).Contains("co-host", StringComparison.OrdinalIgnoreCase) &&
                                                       SafeName(e).StartsWith("Make", StringComparison.OrdinalIgnoreCase)) ||
                                          SafeName(window).Contains("co-host", StringComparison.OrdinalIgnoreCase);
                    if (!isCoHostDialog) continue;
                    var confirm = all.FirstOrDefault(e => e.Properties.ControlType.ValueOrDefault == ControlType.Button &&
                                                          ConfirmButton.IsMatch(SafeName(e)) && e.Patterns.Invoke.IsSupported);
                    if (confirm == null) continue;
                    confirm.Patterns.Invoke.Pattern.Invoke();
                    return true;
                }
            }
            catch { }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool AnnouncementConfirms(UIA3Automation automation, ZoomProcessCandidate process, string displayName)
    {
        try
        {
            return Windows(automation, process)
                .SelectMany(window => window.FindAllDescendants())
                .Any(element =>
                {
                    string name = SafeName(element);
                    return name.Contains("co-host permission granted", StringComparison.OrdinalIgnoreCase) &&
                           name.Contains(displayName.Trim(), StringComparison.OrdinalIgnoreCase);
                });
        }
        catch { return false; }
    }

    private static bool TryOpenParticipantsPanel(UIA3Automation automation, ZoomProcessCandidate process)
    {
        try
        {
            var button = Windows(automation, process)
                .SelectMany(window => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                .FirstOrDefault(element =>
                {
                    string name = SafeName(element);
                    return name.StartsWith("Participants", StringComparison.OrdinalIgnoreCase) &&
                           name.Contains("open panel", StringComparison.OrdinalIgnoreCase);
                });
            if (button == null || !button.Patterns.Invoke.IsSupported) return false;
            button.Patterns.Invoke.Pattern.Invoke();
            return true;
        }
        catch { return false; }
    }

    private static string SafeName(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}
