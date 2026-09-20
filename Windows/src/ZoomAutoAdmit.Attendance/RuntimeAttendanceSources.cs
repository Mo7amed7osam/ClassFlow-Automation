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
        public AttendanceSource Source => AttendanceSource.Web;

        public async Task<ParticipantReadResult> ReadAsync(CancellationToken token)
        {
            // The meeting page is asked for on every read: it can be replaced while the class runs.
            var page = getPage() ?? throw new InvalidOperationException("Attendance primary meeting page unavailable.");
            if (page.IsClosed) throw new InvalidOperationException("Attendance primary meeting page closed.");
            var found = await WebParticipantList.FindAsync(page, token);
            if (found.List is not { } list)
                throw new InvalidOperationException(
                    $"Attendance found no participants list across {page.Frames.Count} frames ({found.Seen}). " +
                    "Open the participants panel.");
            if (found.Seen.StartsWith("by its rows", StringComparison.Ordinal))
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info($"[ATTENDANCE] Participants list found {found.Seen}");
            return await new WebAttendanceParticipantSource(page, _ => list).ReadAsync(token);
        }
    }

    /// <summary>
    /// A row reads "Mohab Mohamed __Coordinator,(Guest), Computer audio muted,Video off…".
    /// Only the part before the role/status tail is the person's display name.
    /// </summary>
    /// <summary>Kept under this name for the callers that already use it; the rule itself lives in
    /// <see cref="ParticipantNames"/>, where the web reader can reach it without FlaUI.</summary>
    public static string CleanParticipantName(string label) => ParticipantNames.Clean(label);

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
            // Whoever owns the participants or meeting window now, before anything remembered: Zoom
            // hands a meeting to another of its processes as it goes, and the one remembered from
            // the start of the class stays alive as the launcher. Bound to it, the list was still
            // found (the window is found by its class) but the wheel that walks it is posted to
            // "the window of this process under the list" - none - so only the six rows on show were
            // ever read, a whole evening long (S8, 2026-09-18: 6 of 19 in 238 reads).
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
            if (_processId is { } known && candidates.FirstOrDefault(c => c.ProcessId == known) is { } cached)
                return cached;
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
        /// Every row label of a virtualized list, in order: scrolled to the top, then down to the
        /// bottom, then back where it was. Zoom's desktop list shows only the rows on screen and (on
        /// Zoom Workplace 2026-09) offers no scroll pattern, so without one the list is walked with
        /// mouse-wheel messages posted to the Zoom window itself: the pointer does not move, nothing
        /// is brought to the front and nothing is pressed (verified live: 18 of 18 read, foreground
        /// and cursor unchanged).
        /// </summary>
        private static (List<string> Labels, bool ReachedEnd) ReadWholeList(
            FlaUI.Core.AutomationElements.AutomationElement list, ZoomProcessCandidate process, CancellationToken token)
        {
            var labels = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<string> Visible() => list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .Select(SafeName).Where(label => label.Length > 0).ToArray();
            void Take()
            {
                foreach (var label in Visible())
                    if (seen.Add(label)) labels.Add(label);
            }
            FlaUI.Core.Patterns.IScrollPattern? scroll = null;
            try { if (list.Patterns.Scroll.IsSupported) scroll = list.Patterns.Scroll.Pattern; } catch { }
            if (scroll == null) return WheelThroughList(list, process, Visible, Take, labels, token);
            if (!scroll.VerticallyScrollable.ValueOrDefault) { Take(); return (labels, true); }

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

        private const uint WmMouseWheel = 0x020A;
        private const int WheelNotch = 120;
        private const int MaxWheelMoves = 400;

        private static (List<string> Labels, bool ReachedEnd) WheelThroughList(
            FlaUI.Core.AutomationElements.AutomationElement list, ZoomProcessCandidate process,
            Func<IReadOnlyList<string>> visible, Action take, List<string> labels, CancellationToken token)
        {
            take();
            var rect = list.BoundingRectangle;
            int x = rect.Left + rect.Width / 2, y = rect.Top + rect.Height / 2;
            IntPtr window = ZoomWindowUnder(x, y, process);
            // No window of this Zoom process holds the list: only the rows on show, and never a
            // message to anything else.
            if (window == IntPtr.Zero) return (labels, false);
            IntPtr position = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

            // One notch; true when the rows on show changed (the list moved).
            bool Wheel(int delta)
            {
                string before = string.Join("\n", visible());
                NativeMethods.PostMessage(window, WmMouseWheel, (IntPtr)unchecked((int)((delta & 0xFFFF) << 16)), position);
                for (int wait = 0; wait < 6; wait++)
                {
                    Thread.Sleep(80);
                    if (string.Join("\n", visible()) != before) return true;
                }
                return false;
            }

            int up = 0, down = 0;
            bool end = false;
            try
            {
                for (int still = 0; up < MaxWheelMoves && still < 2 && !token.IsCancellationRequested;)
                {
                    if (Wheel(WheelNotch)) { up++; still = 0; take(); } else still++;
                }
                for (int still = 0; down < MaxWheelMoves && !token.IsCancellationRequested;)
                {
                    if (Wheel(-WheelNotch)) { down++; still = 0; take(); }
                    else if (++still >= 2) { end = true; break; }
                }
            }
            catch { end = false; }
            finally
            {
                // Back where the person left it.
                try { for (int i = 0; i < down - up; i++) Wheel(WheelNotch); } catch { }
            }
            return (labels, end);
        }

        /// <summary>The window of this Zoom process that holds the point, or none.</summary>
        private static IntPtr ZoomWindowUnder(int x, int y, ZoomProcessCandidate process)
        {
            var handles = new[] { ZoomWindowManager.FindParticipantsWindow(), ZoomWindowManager.FindMainZoomMeetingWindow() }
                .Concat(process.Windows.Where(w => w.IsVisible).Select(w => w.Handle));
            foreach (var handle in handles.Where(h => h != IntPtr.Zero).Distinct())
            {
                NativeMethods.GetWindowThreadProcessId(handle, out var owner);
                if ((int)owner != process.ProcessId) continue;
                if (!NativeMethods.GetWindowRect(handle, out var bounds)) continue;
                if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return handle;
            }
            return IntPtr.Zero;
        }

        /// <summary>Zoom's own "Participants (18)" above the list: how many people are in the meeting.</summary>
        private static int? PanelCount(UIA3Automation automation, ZoomProcessCandidate process)
        {
            try
            {
                var participantsWindow = ZoomWindowManager.FindParticipantsWindow();
                FlaUI.Core.AutomationElements.AutomationElement[] roots = participantsWindow != IntPtr.Zero
                    ? [automation.FromHandle(participantsWindow)]
                    : process.Windows.Where(w => w.IsVisible).Select(w => automation.FromHandle(w.Handle)).ToArray();
                foreach (var root in roots.Where(r => r != null))
                {
                    if (PanelTitle.Match(SafeName(root)) is { Success: true } title) return int.Parse(title.Groups[1].Value);
                    foreach (var text in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
                        if (PanelTitle.Match(SafeName(text)) is { Success: true } match) return int.Parse(match.Groups[1].Value);
                }
            }
            catch { }
            return null;
        }

        private static readonly Regex PanelTitle = new(@"^participants\s*\((\d+)\)$", RegexOptions.IgnoreCase);

        private static readonly HashSet<int> PoppedOut = [];
        private static DateTime _lastReturn = DateTime.MinValue;
        private static readonly TimeSpan ReturnEvery = TimeSpan.FromMinutes(3);
        private const uint WmSysCommand = 0x0112;
        private static readonly IntPtr ScMinimize = (IntPtr)0xF020;

        /// <summary>
        /// A participants panel docked in the meeting window disappears when the meeting is shrunk to
        /// Zoom's small floating window, and with it admission, attendance and co-host. Popped out
        /// into its own window it stays readable (verified live, 2026-09-16: 17 of 17 read with the
        /// meeting minimized). Zoom's own "Pop out" button, invoked once per Zoom process.
        /// </summary>
        private static bool TryPopOut(UIA3Automation automation, ZoomProcessCandidate process)
        {
            if (ZoomWindowManager.FindParticipantsWindow() != IntPtr.Zero) return false;
            lock (PoppedOut) { if (!PoppedOut.Add(process.ProcessId)) return false; }
            try
            {
                IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
                if (meeting == IntPtr.Zero) return false;
                var button = automation.FromHandle(meeting)
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(element => SafeName(element).Equals("Pop out", StringComparison.OrdinalIgnoreCase));
                if (button == null || !button.Patterns.Invoke.IsSupported) return false;
                button.Patterns.Invoke.Pattern.Invoke();
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info("[ATTENDANCE] Participants panel popped out, so it keeps working when the meeting is minimized.");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The meeting was shrunk to Zoom's small window with the panel docked, so nothing can be read.
        /// Only while the person is away from the keyboard and mouse: "Return to meeting", pop the
        /// panel out, shrink the meeting back and give the front back to whatever had it. At most
        /// every few minutes.
        /// </summary>
        private static bool TryRecoverFromMiniWindow(UIA3Automation automation, ZoomProcessCandidate process)
        {
            if (ZoomWindowManager.FindParticipantsWindow() != IntPtr.Zero) return false;
            if (DateTime.UtcNow - _lastReturn < ReturnEvery) return false;
            if (!ZoomAutoAdmit.UIAutomation.Input.UserActivity.IsIdleFor(ForegroundWindowPreserver.RequiredIdle)) return false;
            _lastReturn = DateTime.UtcNow;
            try
            {
                var returnButton = process.Windows.Where(w => w.IsVisible)
                    .Select(w => automation.FromHandle(w.Handle))
                    .Where(root => root != null)
                    .SelectMany(root => root.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)))
                    .FirstOrDefault(element => SafeName(element).Equals("Return to meeting", StringComparison.OrdinalIgnoreCase));
                if (returnButton == null || !returnButton.Patterns.Invoke.IsSupported) return false;
                IntPtr front = NativeMethods.GetForegroundWindow();
                returnButton.Patterns.Invoke.Pattern.Invoke();
                Thread.Sleep(1500);
                lock (PoppedOut) PoppedOut.Remove(process.ProcessId);
                bool popped = TryPopOut(automation, process);
                Thread.Sleep(800);
                IntPtr meeting = ZoomWindowManager.FindMainZoomMeetingWindow();
                if (meeting != IntPtr.Zero) NativeMethods.PostMessage(meeting, WmSysCommand, ScMinimize, IntPtr.Zero);
                Thread.Sleep(500);
                if (front != IntPtr.Zero) NativeMethods.SetForegroundWindow(front);
                ZoomAutoAdmit.Core.Formatting.ConsoleLogger.Info(
                    $"[ATTENDANCE] The meeting was minimized with the participants panel inside; reopened it briefly{(popped ? " and popped the panel out" : "")}.");
                return true;
            }
            catch { return false; }
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
                if (lists.Length == 0 && mayOpenPanel && TryRecoverFromMiniWindow(automation, process))
                {
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
                // Docked in the meeting window: popped out once, so a minimized meeting still has a list.
                if (mayOpenPanel && TryPopOut(automation, process))
                {
                    Thread.Sleep(900);
                    var (popped, _) = FindJoinedLists(automation, process);
                    if (popped.Length == 1) lists = popped;
                }
                // Admission reads the same list; it is never scrolled under an admission.
                List<string> labels;
                bool reachedEnd;
                using (var gate = ParticipantsPanelGate.TryEnter(TimeSpan.FromSeconds(20), token))
                {
                    if (gate == null)
                        throw new InvalidOperationException("The participants list was busy with an admission; read again next time.");
                    (labels, reachedEnd) = ReadWholeList(lists[0], process, token);
                }
                int? panelCount = PanelCount(automation, process);
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
                // Without section headers, Zoom's "Participants (n)" title is the count to reach.
                joinedCount ??= hasJoinedHeader ? null : panelCount;
                bool complete = reachedEnd && (joinedCount is not { } n || names.Count >= n);
                result = new(names, complete, complete
                    ? $"The whole Joined list ({names.Count} of {joinedCount}), read page by page."
                    : $"Joined rows read: {names.Count}{(joinedCount is { } c ? $" of {c}" : "")}; the list may not have been fully exposed.");
            }, uint.MaxValue);
            return result ?? throw new InvalidOperationException("Attendance UIA read did not complete.");
        }, token);
    }
}
