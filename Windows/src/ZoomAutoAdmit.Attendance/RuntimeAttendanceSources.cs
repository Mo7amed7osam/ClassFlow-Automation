using System.Text.RegularExpressions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Playwright;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Discovery;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Window;

namespace ZoomAutoAdmit.Attendance;

/// <summary>Read-only production bindings. Ambiguous/unexposed Joined lists fail closed.</summary>
public sealed class RuntimeAttendanceSources(Func<IPage?> primaryPage)
{
    // Confirmed from a live Windows meeting: the panel exposes one list named
    // "Participant list, use arrow key to navigate…" whose ListItems are section headers
    // ("Waiting room (1), Expanded" / "Joined (2), Expanded") followed by the people in them.
    // The Zoom app names it "Participant list, use arrow key to navigate…"; the web client names it
    // "Participants list" (verified live, 2026-09-16) - the missing "s" cost every web read.
    private static readonly Regex JoinedListName = new(
        @"^participants? list\b|^(joined|in[- ]meeting)(\s+participants)?(\s+list)?(\s*\(\d+\))?$", RegexOptions.IgnoreCase);
    private static readonly Regex SectionHeader = new(
        @"^(waiting room|joined|in[- ]meeting)\s*\(\d+\)", RegexOptions.IgnoreCase);
    private static readonly Regex WaitingHeader = new(@"^waiting room", RegexOptions.IgnoreCase);

    public IAttendanceParticipantSource Create(MeetingLaunchContext context) => Create(context, mayOpenPanel: true);

    /// <summary>
    /// mayOpenPanel=false gives a strictly passive reader: it never invokes anything in Zoom and
    /// only reads a participants list that is already on screen. Used by observers that must not
    /// touch the meeting while admission is working.
    /// </summary>
    public IAttendanceParticipantSource Create(MeetingLaunchContext context, bool mayOpenPanel) =>
        context.EngineType == SessionEngineType.Web ? new WebSource(primaryPage) : new DesktopSource(mayOpenPanel);

    private sealed class WebSource(Func<IPage?> getPage) : IAttendanceParticipantSource
    {
        private IPage? _page;
        public AttendanceSource Source => AttendanceSource.Web;

        public async Task<ParticipantReadResult> ReadAsync(CancellationToken token)
        {
            _page ??= getPage() ?? throw new InvalidOperationException("Attendance primary meeting page unavailable.");
            if (_page.IsClosed) throw new InvalidOperationException("Attendance primary meeting page closed.");
            var matches = new List<ILocator>();
            foreach (var frame in _page.Frames)
            {
                token.ThrowIfCancellationRequested();
                foreach (var role in new[] { AriaRole.List, AriaRole.Listbox })
                foreach (var list in await frame.GetByRole(role, new() { NameRegex = JoinedListName }).AllAsync())
                    if (await list.IsVisibleAsync()) matches.Add(list);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException(
                    $"Attendance requires one exposed Joined list; matched {matches.Count} across {_page.Frames.Count} frames. " +
                    "Open the participants panel, or configure the runtime source binding for this Zoom layout.");
            return await new WebAttendanceParticipantSource(_page, _ => matches[0]).ReadAsync(token);
        }
    }

    /// <summary>
    /// A row reads "Mohab Mohamed __Coordinator,(Guest), Computer audio muted,Video off…".
    /// Only the part before the role/status tail is the person's display name.
    /// </summary>
    public static string CleanParticipantName(string label)
    {
        int marker = label.IndexOf(",(", StringComparison.Ordinal);
        if (marker > 0) return label[..marker].Trim();
        // The web client writes the role without that comma - "eyouth coordinator (Host, me),computer
        // audio muted…" - so cutting at the first comma would keep "(Host" in the name.
        if (RoleSuffix.Match(label) is { Success: true } role) return label[..role.Index].Trim();
        int comma = label.IndexOf(',');
        return (comma > 0 ? label[..comma] : label).Trim();
    }

    /// <summary>Where a row's role tail begins. Only Zoom's own role words, never a name's brackets.</summary>
    private static readonly Regex RoleSuffix = new(@"\s*\((host|co-?host|guest|me)\b", RegexOptions.IgnoreCase);

    private static string SafeName(FlaUI.Core.AutomationElements.AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    // Short, bounded description of the candidate lists for the capture-issue record.
    private static string Describe(IEnumerable<FlaUI.Core.AutomationElements.AutomationElement> elements)
    {
        var names = elements.Select(element =>
        {
            var name = SafeName(element);
            if (name.Length > 40) name = name[..40];
            bool offscreen = false;
            try { offscreen = element.Properties.IsOffscreen.ValueOrDefault; } catch { }
            return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name + (offscreen ? " [offscreen]" : "");
        }).Distinct().Take(8).ToArray();
        return names.Length == 0 ? "(none)" : string.Join(" | ", names);
    }

    private sealed class DesktopSource(bool mayOpenPanel = true) : IAttendanceParticipantSource
    {
        private int? _processId;
        public AttendanceSource Source => AttendanceSource.Desktop;

        /// <summary>
        /// Zoom runs several processes named Zoom/CptHost, so "exactly one" is the wrong test.
        /// Bind to whichever process owns the live meeting or participants window — the same
        /// windows the admission path already works with — and only then fall back.
        /// </summary>
        private ZoomProcessCandidate Resolve(IReadOnlyList<ZoomProcessCandidate> candidates)
        {
            if (candidates.Count == 0)
                throw new InvalidOperationException("Attendance Desktop: no Zoom process is running.");
            if (_processId is { } known && candidates.FirstOrDefault(c => c.ProcessId == known) is { } cached)
                return cached;
            foreach (var handle in new[] { ZoomWindowManager.FindParticipantsWindow(), ZoomWindowManager.FindMainZoomMeetingWindow() })
            {
                if (handle == IntPtr.Zero) continue;
                NativeMethods.GetWindowThreadProcessId(handle, out var owner);
                if (candidates.FirstOrDefault(c => c.ProcessId == (int)owner) is { } match)
                {
                    _processId = match.ProcessId;
                    return match;
                }
            }
            var visible = candidates.Where(c => c.Windows.Any(w => w.IsVisible)).ToArray();
            if (visible.Length == 1)
            {
                _processId = visible[0].ProcessId;
                return visible[0];
            }
            throw new InvalidOperationException(
                "Attendance Desktop process is ambiguous: no meeting/participants window matched a Zoom process. Candidates: " +
                string.Join(" | ", candidates.Take(6).Select(c =>
                    $"pid={c.ProcessId} {c.ProcessName} visibleWindows={c.Windows.Count(w => w.IsVisible)} main=\"{Trim(c.MainWindowTitle)}\"")));
        }

        private static string Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "" : value.Length <= 40 ? value : value[..40];

        private static (FlaUI.Core.AutomationElements.AutomationElement[] Matched, FlaUI.Core.AutomationElements.AutomationElement[] AllVisible)
            FindJoinedLists(UIA3Automation automation, ZoomProcessCandidate process)
        {
            // The participants window alone is a far smaller tree than every Zoom window, and this
            // read runs while admission is working: keep it as cheap as possible.
            var participantsWindow = ZoomWindowManager.FindParticipantsWindow();
            FlaUI.Core.AutomationElements.AutomationElement[] roots = participantsWindow != IntPtr.Zero
                ? [automation.FromHandle(participantsWindow)]
                : process.Windows.Where(w => w.IsVisible).Select(w => automation.FromHandle(w.Handle)).ToArray();
            var visible = roots.Where(root => root != null)
                .SelectMany(root => root.FindAllDescendants(cf => cf.ByControlType(ControlType.List)))
                .ToArray();
            if (visible.Length == 0 && participantsWindow != IntPtr.Zero)
                visible = process.Windows.Where(w => w.IsVisible)
                    .Select(w => automation.FromHandle(w.Handle))
                    .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.List)))
                    .ToArray();
            var matched = visible
                .Where(e => JoinedListName.IsMatch(SafeName(e)) && !e.Properties.IsOffscreen.ValueOrDefault)
                .ToArray();
            return (matched, visible);
        }

        /// <summary>
        /// Every row label of a virtualized list, in order: scrolled to the top, then a page at a
        /// time to the bottom, then back where it was. Without a scroll pattern, the rows on show.
        /// </summary>
        private static (List<string> Labels, bool ReachedEnd) ReadWholeList(FlaUI.Core.AutomationElements.AutomationElement list, CancellationToken token)
        {
            var labels = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int Take()
            {
                int added = 0;
                foreach (var row in list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                {
                    string label = SafeName(row);
                    if (label.Length > 0 && seen.Add(label)) { labels.Add(label); added++; }
                }
                return added;
            }
            FlaUI.Core.Patterns.IScrollPattern? scroll = null;
            try { if (list.Patterns.Scroll.IsSupported) scroll = list.Patterns.Scroll.Pattern; } catch { }
            if (scroll == null || !scroll.VerticallyScrollable.ValueOrDefault) { Take(); return (labels, true); }

            double original = scroll.VerticalScrollPercent.ValueOrDefault;
            bool end = false;
            try
            {
                scroll.SetScrollPercent(-1, 0);
                Thread.Sleep(150);
                Take();
                for (int page = 0; page < 80 && !token.IsCancellationRequested; page++)
                {
                    if (scroll.VerticalScrollPercent.ValueOrDefault >= 99.5) { end = true; break; }
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    Thread.Sleep(150);
                    Take();
                }
                if (!end) end = scroll.VerticalScrollPercent.ValueOrDefault >= 99.5;
            }
            catch { }
            finally { try { scroll.SetScrollPercent(-1, original); } catch { } }
            return (labels, end);
        }

        private static DateTime _lastAltU = DateTime.MinValue;
        private static readonly TimeSpan AltUEvery = TimeSpan.FromMinutes(3);

        /// <summary>
        /// The toolbar hides itself when the mouse is still, and a hidden toolbar exposes no
        /// Participants button to UI Automation - the case of a class nobody is watching. The
        /// fallback is Zoom's own Alt+U, sent to the meeting window with the window that was in front
        /// put back straight after (as the admission path does). At most every few minutes, so a
        /// panel someone keeps closing is not fought over.
        /// </summary>
        private static bool TryAltUOpen()
        {
            if (DateTime.UtcNow - _lastAltU < AltUEvery) return false;
            _lastAltU = DateTime.UtcNow;
            IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
            if (meeting == IntPtr.Zero) return false;
            try
            {
                using var preserver = new ForegroundWindowPreserver(meeting);
                if (!preserver.ActivateZoomTemporarily()) return false;
                NativeMethods.SendAltKey(0x55);        // Alt+U: Participants
                Thread.Sleep(500);
                // A docked panel is part of the meeting window, so the lists are simply read again.
                return true;
            }
            catch { return false; }
        }

        /// <summary>Invokes Zoom's own "Participants, open panel" toolbar button when the panel is closed.</summary>
        private static bool TryOpenParticipantsPanel(UIA3Automation automation, ZoomProcessCandidate process)
        {
            try
            {
                var button = process.Windows.Where(w => w.IsVisible)
                    .Select(w => automation.FromHandle(w.Handle))
                    .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                    .FirstOrDefault(element =>
                    {
                        var name = SafeName(element);
                        return name.StartsWith("Participants", StringComparison.OrdinalIgnoreCase) &&
                               name.Contains("open panel", StringComparison.OrdinalIgnoreCase);
                    });
                if (button == null || !button.Patterns.Invoke.IsSupported) return false;
                button.Patterns.Invoke.Pattern.Invoke();
                return true;
            }
            catch { return false; }
        }

        public Task<ParticipantReadResult> ReadAsync(CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var candidates = new ZoomProcessDiscovery().FindCandidates(logInfo: false)
                .Where(p => p.ProcessName.Equals("Zoom", StringComparison.OrdinalIgnoreCase) ||
                            p.ProcessName.Equals("CptHost", StringComparison.OrdinalIgnoreCase)).ToArray();
            var process = Resolve(candidates);
            ParticipantReadResult? result = null;
            // Reuse the existing interactive-desktop UIA infrastructure; never touch input.
            DesktopThread.RunOnInteractiveDesktop(() =>
            {
                using var automation = new UIA3Automation();
                var (lists, visibleLists) = FindJoinedLists(automation, process);
                if (lists.Length == 0 && mayOpenPanel && TryOpenParticipantsPanel(automation, process))
                {
                    // The list only exists while the panel is open; opening it is a UIA Invoke on
                    // Zoom's own toolbar button — no coordinates, no keystrokes, no cursor movement.
                    Thread.Sleep(700);
                    (lists, visibleLists) = FindJoinedLists(automation, process);
                }
                if (lists.Length == 0 && mayOpenPanel && TryAltUOpen())
                {
                    Thread.Sleep(700);
                    (lists, visibleLists) = FindJoinedLists(automation, process);
                }
                // The failure names what was actually on screen, so one live run is enough to adapt the binding.
                if (lists.Length != 1)
                    throw new InvalidOperationException(
                        $"Attendance requires one exposed Joined UIA list; matched {lists.Length} of {visibleLists.Length} lists. " +
                        $"Open the participants panel. Lists seen: {Describe(visibleLists)}");
                // The whole list, like the extension reads it: Zoom only exposes the rows on screen,
                // so the list is scrolled top to bottom and every page is read, then put back.
                var (labels, reachedEnd) = ReadWholeList(lists[0], token);
                var names = new List<ParticipantPresence>();
                // Rows arrive in visual order; a section header switches which list the rows below belong to.
                // With no header at all (no waiting room) every row is a joined participant. When the
                // "Waiting room (n)" header is out of view, the people above "Joined (n)" are still
                // waiting: rows before a Joined header are never attendance.
                bool hasJoinedHeader = labels.Any(l => SectionHeader.IsMatch(l) && !WaitingHeader.IsMatch(l));
                bool inJoinedSection = !hasJoinedHeader;
                int? joinedCount = null;
                foreach (var label in labels)
                {
                    token.ThrowIfCancellationRequested();
                    if (SectionHeader.IsMatch(label))
                    {
                        inJoinedSection = !WaitingHeader.IsMatch(label);
                        if (inJoinedSection && System.Text.RegularExpressions.Regex.Match(label, @"\((\d+)\)") is { Success: true } m)
                            joinedCount = int.Parse(m.Groups[1].Value);
                        continue;
                    }
                    if (!inJoinedSection) continue;   // Waiting room is never attendance.
                    names.Add(new(CleanParticipantName(label)) { RowLabel = label });
                }
                if (names.Count == 0) throw new InvalidOperationException("Attendance UIA rows unavailable; not proof of empty attendance.");
                // Complete when the list was read to its end and holds as many people as Zoom counts.
                // Without a waiting room Zoom shows no section headers at all: the whole list is joined.
                bool complete = reachedEnd && (joinedCount is not { } n || names.Count >= n);
                result = new(names, complete, complete
                    ? $"The whole Joined list ({names.Count} of {joinedCount}), read page by page."
                    : $"Joined rows read: {names.Count}{(joinedCount is { } c ? $" of {c}" : "")}; the list may not have been fully exposed.");
            }, uint.MaxValue);
            return result ?? throw new InvalidOperationException("Attendance UIA read did not complete.");
        }, token);
    }
}
