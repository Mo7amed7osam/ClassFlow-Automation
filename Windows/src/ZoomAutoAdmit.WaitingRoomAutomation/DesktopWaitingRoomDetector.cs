using System.Drawing;
using System.Drawing.Imaging;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.UIAutomation.Input;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Ocr;
using ZoomAutoAdmit.UIAutomation.Screen;

namespace ZoomAutoAdmit.WaitingRoomAutomation;

public interface IDesktopWaitingRoomDetector
{
    Task StartWatcherAsync(CancellationToken cancellationToken);
}

public sealed class DesktopWaitingRoomDetector : IDesktopWaitingRoomDetector
{
    private readonly WindowsNativeOcrEngine _ocrEngine = new();
    private readonly WindowsCursorController _cursorController = new();
    private readonly WindowsMouseInput _mouseInput = new();
    private readonly HandledNotificationCache _handledCache = new(TimeSpan.FromSeconds(3));
    private readonly HandledBatchCache _handledBatchCache = new(TimeSpan.FromSeconds(3));
    private readonly HandledMultiNotificationCache _handledMultiCache = new(TimeSpan.FromSeconds(3));
    private readonly DesktopWaitingRoomUiaAdmitter _uiaAdmitter;
    private readonly ToastUiaAdmitter _toastAdmitter;
    private WaitingRoomSessionLog TesterLogger { get; }
    private int _scanCount = 0;
    private bool _pausedLogged;

    public DesktopWaitingRoomDetector(Guid sessionId, IWaitingRoomLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        TesterLogger = new WaitingRoomSessionLog(logger, sessionId, SessionEngineType.Desktop);
        _uiaAdmitter = new DesktopWaitingRoomUiaAdmitter(sessionId, logger);
        _toastAdmitter = new ToastUiaAdmitter(sessionId, logger);
    }

    /// <summary>
    /// Clicks once for this admit attempt, and says so when it does not.
    /// </summary>
    /// <remarks>
    /// The executor is built per attempt on purpose: SingleClickExecutor refuses every call after
    /// its first one, so keeping one in a field would let the watcher admit a single person per
    /// run and then go quiet while still detecting the toast every few seconds.
    /// </remarks>
    private bool TryClick(int x, int y)
    {
        try
        {
            var executor = new SingleClickExecutor(_mouseInput);
            if (executor.TryClick(x, y)) return true;
            TesterLogger.Error($"Click was refused at ({x},{y}); nothing was clicked.");
            return false;
        }
        catch (Exception ex)
        {
            TesterLogger.Error($"Click failed at ({x},{y}): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Records that somebody was let in. The same call feeds the attendance snapshot and wakes the
    /// co-host watcher, so the Desktop engine reports an admission exactly the way Web does;
    /// without it, a whole class admitted from the Zoom app was never counted at all.
    /// </summary>
    private static void CountAdmission(int people = 1, string? name = null) =>
        CountAdmissions(name is null ? [] : [name], people);

    /// <summary>
    /// One line per person let in (Admit all lets in everyone it listed), written down once: outside
    /// an app session NotifyVerified writes the line itself, so it is not written here as well.
    /// </summary>
    private static void CountAdmissions(IReadOnlyList<string> names, int people)
    {
        var scope = ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.IsBound;
        var named = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        if (named.Length == 0)
        {
            people = Math.Max(1, people);
            if (scope)
            {
                ZoomAutoAdmit.Core.Meetings.AdmissionLedger.Record(null, "desktop", people);
                for (int i = 0; i < people; i++) ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.NotifyVerified();
                return;
            }
            // One line for the whole group; today's count still goes up by each person.
            ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.NotifyVerified(null, people);
            for (int i = 1; i < people; i++) ZoomAutoAdmit.Core.Meetings.AdmissionControl.RecordAdmission();
            return;
        }
        foreach (var name in named)
        {
            if (scope) ZoomAutoAdmit.Core.Meetings.AdmissionLedger.Record(name, "desktop");
            ZoomAutoAdmit.Core.Meetings.MeetingAdmissionScope.NotifyVerified(name);
        }
    }

    /// <summary>Admits from a Zoom or Windows arrival notification without touching the mouse.</summary>
    private bool TryAdmitFromNotification(CancellationToken cancellationToken)
    {
        var result = _toastAdmitter.TryAdmit(cancellationToken);
        switch (result.Outcome)
        {
            case ToastUiaAdmitter.ToastOutcome.Admitted:
                if (!_handledCache.IsParticipantSuppressed(result.Participant, DateTimeOffset.UtcNow))
                    TesterLogger.Action($"Admitted participant from the notification without the mouse: '{result.Participant}'");
                _handledCache.MarkParticipantHandled(result.Participant, DateTimeOffset.UtcNow);
                CountAdmission(name: result.Participant);
                return true;

            case ToastUiaAdmitter.ToastOutcome.OpenedParticipants:
                // The panel needs a moment to appear; the next pass admits from the row.
                Thread.Sleep(350);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Admits a waiting person straight from Zoom's Participants list.</summary>
    private bool TryAdmitFromParticipantsPanel(CancellationToken cancellationToken)
    {
        try
        {
            if (!_uiaAdmitter.TryAdmit(cancellationToken)) return false;
            TesterLogger.Action("Admitted from the Participants panel without the mouse");
            CountAdmissions(_uiaAdmitter.LastAdmittedNames, Math.Max(1, _uiaAdmitter.LastAdmittedNames.Count));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            TesterLogger.Error($"Participants panel admit failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        _scanCount++;

        // The operator's switch is read every pass, so turning it off during a live meeting stops
        // the very next admission instead of taking effect on the next meeting. Paused means no
        // screen is read and no control is pressed; the monitor stays alive, ready to resume.
        if (!ZoomAutoAdmit.Core.Meetings.AdmissionControl.IsAdmitting)
        {
            if (!_pausedLogged)
            {
                _pausedLogged = true;
                TesterLogger.WaitingRoom("Admitting is paused; nobody will be let in until it is switched back on.");
            }
            return;
        }
        if (_pausedLogged)
        {
            _pausedLogged = false;
            TesterLogger.WaitingRoom("Admitting resumed.");
        }

        // Ask Windows what notifications exist before reading any pixels. This finds the arrival
        // even when it is on a monitor nobody is looking at, when Zoom's own controls cover its
        // Admit button, or while the person is using the mouse - and it costs a fraction of a
        // full-screen OCR pass, so people get in sooner.
        if (TryAdmitFromNotification(cancellationToken)) return;

        // Then the Participants panel itself. Zoom keeps that list accurate whatever is on screen,
        // and UI Automation reads it without a screenshot and presses Admit without the cursor, so
        // this admits from another monitor and while the person is using the mouse. The screen
        // scan below stays for the cases the accessibility tree does not expose.
        if (TryAdmitFromParticipantsPanel(cancellationToken)) return;

        if (!_ocrEngine.IsAvailable) return;

        // Step 1: Multi-screen DPI-aware screen capture and OCR
        var monitors = WindowsScreenCapturer.CaptureAllScreens();
        if (monitors.Count == 0) return;

        OcrResult effectiveOcr;
        try
        {
            var allLines = new List<OcrLine>();
            var allWords = new List<OcrWord>();
            double desktopMinX = double.MaxValue, desktopMinY = double.MaxValue;
            double desktopMaxX = double.MinValue, desktopMaxY = double.MinValue;

            foreach (var monitor in monitors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localOcr = await _ocrEngine.RecognizeBitmapAsync(monitor.Bitmap, 0, 0);
                var mappedOcr = ScreenCropGeometry.MapMonitorOcrToVirtualDesktop(localOcr, monitor.Bounds);
                allLines.AddRange(mappedOcr.Lines);
                allWords.AddRange(mappedOcr.Words);

                desktopMinX = Math.Min(desktopMinX, monitor.Bounds.X);
                desktopMinY = Math.Min(desktopMinY, monitor.Bounds.Y);
                desktopMaxX = Math.Max(desktopMaxX, monitor.Bounds.X + monitor.Bounds.Width);
                desktopMaxY = Math.Max(desktopMaxY, monitor.Bounds.Y + monitor.Bounds.Height);
            }

            var virtualBounds = new BoundingRectangleInfo(
                desktopMinX, desktopMinY,
                Math.Max(1, desktopMaxX - desktopMinX),
                Math.Max(1, desktopMaxY - desktopMinY));

            effectiveOcr = new OcrResult(allLines, allWords, virtualBounds);
        }
        finally
        {
            foreach (var m in monitors) m.Dispose();
        }

        // STEP 1: If "Admit all" is visible in Participants panel -> Click "Admit all" (Priority 1)
        // Skipped entirely when the operator prefers admitting one person at a time.
        bool preferAdmitAll = ZoomAutoAdmit.Core.Meetings.AdmissionControl.PrefersAdmitAll;
        var admitAll = preferAdmitAll
            ? PanelAdmitAllDetector.Detect(effectiveOcr)
            : new PanelAdmitAllCandidate();   // Not accepted, so every batch path is skipped.
        if (admitAll.IsAccepted && !_handledBatchCache.IsSuppressed(admitAll, DateTimeOffset.UtcNow))
        {
            int count = admitAll.WaitingCount ?? 1;
            TesterLogger.Desktop($"[PRIORITY 1] Panel 'Admit all' detected ({count} participants waiting)");
            _handledBatchCache.MarkHandled(admitAll, DateTimeOffset.UtcNow);

            int clickX = checked((int)Math.Round(admitAll.AdmitAllCenter.X));
            int clickY = checked((int)Math.Round(admitAll.AdmitAllCenter.Y));

            if (TryClick(clickX, clickY))
            {
                TesterLogger.Action($"Clicked 'Admit all' ({count} participants)");
                // Everyone the panel listed, by name (read before the click).
                CountAdmissions(admitAll.OriginalParticipants, count);
                return;
            }
        }
        else if (preferAdmitAll)
        {
            var directAdmitAll = FindDirectAdmitAllOcr(effectiveOcr);
            if (directAdmitAll.HasValue)
            {
                int clickX = checked((int)Math.Round(directAdmitAll.Value.X));
                int clickY = checked((int)Math.Round(directAdmitAll.Value.Y));
                TesterLogger.Desktop($"[PRIORITY 1] Direct OCR 'Admit all' detected at ({clickX},{clickY})");

                if (TryClick(clickX, clickY))
                {
                    TesterLogger.Action("Clicked 'Admit all' (Direct OCR)");
                    CountAdmission();
                    return;
                }
            }
        }

        // STEP 2: If multi-person toast appears ("N people entered the waiting room") -> Click "View" to open Participants list
        var multiResult = MultiPersonWaitingNotificationDetector.Detect(effectiveOcr);
        var multiCandidate = multiResult.AllCandidates
            .FirstOrDefault(c => c.IsAccepted && !_handledMultiCache.IsSuppressed(c, DateTimeOffset.UtcNow));

        if (multiCandidate != null)
        {
            TesterLogger.Desktop($"Multi-person toast detected: {multiCandidate.WaitingCount} people waiting. Clicking 'View' to open Participants panel...");
            _handledMultiCache.MarkHandled(multiCandidate, DateTimeOffset.UtcNow);

            int viewX = checked((int)Math.Round(multiCandidate.ViewCenter.X));
            int viewY = checked((int)Math.Round(multiCandidate.ViewCenter.Y));

            if (TryClick(viewX, viewY))
            {
                TesterLogger.Action($"Clicked 'View' on multi-person toast ({multiCandidate.WaitingCount} people)");
                Thread.Sleep(350); // Allow Zoom to open the Participants panel
                return;
            }
        }

        // STEP 3: Check single-person floating Waiting Room Toast -> Click "Admit"
        var toastResult = WaitingRoomToastDetector.Detect(effectiveOcr);
        var toastCandidate = toastResult.BestCandidate;
        if (toastCandidate != null && toastCandidate.IsAccepted && !_handledCache.IsSuppressed(toastCandidate, DateTimeOffset.UtcNow))
        {
            TesterLogger.Desktop($"Toast detected for participant: '{toastCandidate.ParticipantName}' (Confidence: {toastCandidate.Confidence:P0})");
            _handledCache.MarkHandled(toastCandidate, DateTimeOffset.UtcNow);

            // The accessibility path already ran at the top of this pass, so a toast that reaches
            // here is one UI Automation could not act on. The coordinate click is what is left.
            int clickX = checked((int)Math.Round(toastCandidate.AdmitCenter.X));
            int clickY = checked((int)Math.Round(toastCandidate.AdmitCenter.Y));

            if (TryClick(clickX, clickY))
            {
                TesterLogger.Action($"Admitted participant from toast: '{toastCandidate.ParticipantName}'");
                CountAdmission(name: toastCandidate.ParticipantName);
                return;
            }
        }

        // STEP 4: In open Participants Panel: Individual row hover + Admit
        var panel = WaitingRoomParticipantRowDetector.Detect(effectiveOcr);
        if (panel.IsPanelVisible && panel.WaitingRoomHeader != null)
        {
            int waitingCount = panel.DeclaredWaitingCount ?? panel.Rows.Count;

            var selectedRow = panel.Rows
                .Where(row => row.Confidence >= 0.85 && !_handledCache.IsParticipantSuppressed(row.ParticipantName, DateTimeOffset.UtcNow))
                .OrderBy(row => row.RowBounds.Y)
                .FirstOrDefault();

            if (selectedRow != null)
            {
                TesterLogger.Desktop($"[WAITING_ROOM] Passing detected row to UIA hover flow: '{selectedRow.ParticipantName}'");
                if (_uiaAdmitter.TryAdmit(cancellationToken))
                {
                    _handledCache.MarkParticipantHandled(selectedRow.ParticipantName, DateTimeOffset.UtcNow);
                    return;
                }

                // Re-verify participant state: Check if participant was admitted through another path or is no longer in waiting room
                Thread.Sleep(300);
                var recheckMonitors = WindowsScreenCapturer.CaptureAllScreens();
                bool stillWaiting = true;
                bool movedToJoined = false;
                try
                {
                    var postLines = new List<OcrLine>();
                    var postWords = new List<OcrWord>();
                    foreach (var mon in recheckMonitors)
                    {
                        var localOcr = _ocrEngine.RecognizeBitmapAsync(mon.Bitmap, 0, 0).GetAwaiter().GetResult();
                        var mappedOcr = ScreenCropGeometry.MapMonitorOcrToVirtualDesktop(localOcr, mon.Bounds);
                        postLines.AddRange(mappedOcr.Lines);
                        postWords.AddRange(mappedOcr.Words);
                    }
                    var recheckOcr = new OcrResult(postLines, postWords, effectiveOcr.ImageBounds);
                    var recheckPanel = WaitingRoomParticipantRowDetector.Detect(recheckOcr);

                    if (recheckPanel.IsPanelVisible)
                    {
                        bool foundInWaiting = recheckPanel.Rows.Any(r =>
                            r.ParticipantName.Equals(selectedRow.ParticipantName, StringComparison.OrdinalIgnoreCase) ||
                            r.ParticipantName.Contains(selectedRow.ParticipantName, StringComparison.OrdinalIgnoreCase) ||
                            selectedRow.ParticipantName.Contains(r.ParticipantName, StringComparison.OrdinalIgnoreCase));

                        int recheckCount = recheckPanel.DeclaredWaitingCount ?? recheckPanel.Rows.Count;
                        if (!foundInWaiting || recheckCount < waitingCount)
                        {
                            stillWaiting = false;
                        }

                        if (recheckPanel.JoinedHeader != null)
                        {
                            double joinedY = recheckPanel.JoinedHeader.Bounds.Y;
                            movedToJoined = recheckOcr.Lines.Any(l =>
                                l.Bounds.Y > joinedY &&
                                (l.Text.Contains(selectedRow.ParticipantName, StringComparison.OrdinalIgnoreCase) ||
                                 selectedRow.ParticipantName.Contains(l.Text, StringComparison.OrdinalIgnoreCase)));
                        }
                    }
                    else
                    {
                        stillWaiting = false;
                    }
                }
                catch { }
                finally
                {
                    foreach (var m in recheckMonitors) m.Dispose();
                }

                if (!stillWaiting)
                {
                    TesterLogger.Desktop($"[WAITING_ROOM] After action state: '{selectedRow.ParticipantName}' is no longer in Waiting Room");
                    if (movedToJoined)
                    {
                        TesterLogger.Desktop($"[WAITING_ROOM] Participant moved to Joined");
                    }
                    TesterLogger.Desktop($"[WAITING_ROOM] Participant admitted successfully");
                    _handledCache.MarkParticipantHandled(selectedRow.ParticipantName, DateTimeOffset.UtcNow);
                    return;
                }

                TesterLogger.Error($"[WAITING_ROOM] UIA hover/action flow could not invoke Admit for '{selectedRow.ParticipantName}'");
                return;

#if false // Previous coordinate hover retained only for comparison; it is no longer executable.
                int hoverX = checked((int)Math.Round(selectedRow.SafeHoverPoint.X));
                int hoverY = checked((int)Math.Round(selectedRow.SafeHoverPoint.Y));

                TesterLogger.Desktop($"Hovering participant row: '{selectedRow.ParticipantName}' at ({hoverX},{hoverY})");

                var hover = new CursorPreservingHover(_cursorController);
                bool admitClicked = false;

                hover.Run(hoverX, hoverY, () =>
                {
                    Thread.Sleep(200);

                    // Re-capture primary screen or active monitor while hovered
                    var postMonitors = WindowsScreenCapturer.CaptureAllScreens();
                    try
                    {
                        var postLines = new List<OcrLine>();
                        var postWords = new List<OcrWord>();
                        foreach (var mon in postMonitors)
                        {
                            var localOcr = _ocrEngine.RecognizeBitmapAsync(mon.Bitmap, 0, 0).GetAwaiter().GetResult();
                            var mappedOcr = ScreenCropGeometry.MapMonitorOcrToVirtualDesktop(localOcr, mon.Bounds);
                            postLines.AddRange(mappedOcr.Lines);
                            postWords.AddRange(mappedOcr.Words);
                        }

                        var postOcr = new OcrResult(postLines, postWords, effectiveOcr.ImageBounds);

                        // Validate Admit button revealed on the hovered row
                        var validation = WaitingRoomParticipantRowDetector.ValidateIndividualAdmitAfterHover(
                            selectedRow,
                            panel,
                            postOcr);

                        if (validation.IsConfirmed && validation.AdmitWord != null)
                        {
                            int admitClickX = checked((int)Math.Round(validation.AdmitCenter.X));
                            int admitClickY = checked((int)Math.Round(validation.AdmitCenter.Y));

                            TesterLogger.Desktop($"Found revealed Admit button for '{selectedRow.ParticipantName}' at ({admitClickX},{admitClickY})");

                            if (_clickExecutor.TryClick(admitClickX, admitClickY))
                            {
                                _handledCache.MarkParticipantHandled(selectedRow.ParticipantName, DateTimeOffset.UtcNow);
                                TesterLogger.Action($"Admitted participant: '{selectedRow.ParticipantName}'");
                                admitClicked = true;
                            }
                        }
                        else
                        {
                            // Fallback: check if any Admit word appeared within the row bounds
                            var rowAdmit = postOcr.Words.FirstOrDefault(w =>
                                w.Text.Equals("Admit", StringComparison.OrdinalIgnoreCase) &&
                                w.Bounds.Y >= selectedRow.RowBounds.Y - 5 &&
                                w.Bounds.Y <= (selectedRow.RowBounds.Y + selectedRow.RowBounds.Height) + 5);

                            if (rowAdmit != null)
                            {
                                int admitClickX = checked((int)Math.Round(rowAdmit.Bounds.X + rowAdmit.Bounds.Width / 2));
                                int admitClickY = checked((int)Math.Round(rowAdmit.Bounds.Y + rowAdmit.Bounds.Height / 2));

                                if (_clickExecutor.TryClick(admitClickX, admitClickY))
                                {
                                    _handledCache.MarkParticipantHandled(selectedRow.ParticipantName, DateTimeOffset.UtcNow);
                                    TesterLogger.Action($"Admitted participant (row match): '{selectedRow.ParticipantName}'");
                                    admitClicked = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        foreach (var m in postMonitors) m.Dispose();
                    }
                    return admitClicked;
                });

                if (admitClicked) return;
#endif
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CodeOrLogPattern = new(
        @"(?:\[|\]|Session:|Engine:|WAITING_ROOM|DESKTOP|ACTION|PRIORITY|Direct OCR|dotnet|csproj|powershell|cmd\.exe|Console)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static (double X, double Y)? FindDirectAdmitAllOcr(OcrResult ocr)
    {
        foreach (var line in ocr.Lines)
        {
            if (CodeOrLogPattern.IsMatch(line.Text)) continue;

            if (line.Text.IndexOf("Admit all", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.Text.IndexOf("Admit All", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var admitWord = line.Words.FirstOrDefault(w => w.Text.Equals("Admit", StringComparison.OrdinalIgnoreCase));
                var allWord = line.Words.FirstOrDefault(w => w.Text.Equals("all", StringComparison.OrdinalIgnoreCase));
                (double X, double Y) center;
                if (admitWord != null && allWord != null)
                {
                    double minX = Math.Min(admitWord.Bounds.X, allWord.Bounds.X);
                    double maxX = Math.Max(admitWord.Bounds.X + admitWord.Bounds.Width, allWord.Bounds.X + allWord.Bounds.Width);
                    double minY = Math.Min(admitWord.Bounds.Y, allWord.Bounds.Y);
                    double maxY = Math.Max(admitWord.Bounds.Y + admitWord.Bounds.Height, allWord.Bounds.Y + allWord.Bounds.Height);
                    center = (minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);
                }
                else
                {
                    center = (line.Bounds.X + line.Bounds.Width / 2.0, line.Bounds.Y + line.Bounds.Height / 2.0);
                }

                if (IsLiveZoomWindow(center.X, center.Y))
                {
                    return center;
                }
            }
        }

        for (int i = 0; i < ocr.Words.Count - 1; i++)
        {
            var w1 = ocr.Words[i];
            var w2 = ocr.Words[i + 1];
            if (w1.Text.Equals("Admit", StringComparison.OrdinalIgnoreCase) &&
                w2.Text.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                double gap = w2.Bounds.X - (w1.Bounds.X + w1.Bounds.Width);
                double dy = Math.Abs(w2.Bounds.Y - w1.Bounds.Y);
                if (gap >= -5 && gap <= 50 && dy <= 15)
                {
                    double minX = w1.Bounds.X;
                    double maxX = w2.Bounds.X + w2.Bounds.Width;
                    double minY = Math.Min(w1.Bounds.Y, w2.Bounds.Y);
                    double maxY = Math.Max(w1.Bounds.Y + w1.Bounds.Height, w2.Bounds.Y + w2.Bounds.Height);
                    (double X, double Y) center = (minX + (maxX - minX) / 2.0, minY + (maxY - minY) / 2.0);

                    if (IsLiveZoomWindow(center.X, center.Y))
                    {
                        return center;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsLiveZoomWindow(double x, double y)
    {
        try
        {
            var info = NativeMethods.GetRootWindowAtPointInfoSafe(x, y);
            return info.ProcessName.Contains("Zoom", StringComparison.OrdinalIgnoreCase) ||
                   info.ProcessName.Contains("CptHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task StartWatcherAsync(CancellationToken cancellationToken)
    {
        TesterLogger.WaitingRoom($"Desktop watcher started (Repository Core Engine). OCR: {_ocrEngine.IsAvailable}");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                TesterLogger.Error($"Desktop watcher error: {ex.Message}");
            }

            try { await Task.Delay(400, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        TesterLogger.WaitingRoom("Desktop watcher stopped.");
    }
}
