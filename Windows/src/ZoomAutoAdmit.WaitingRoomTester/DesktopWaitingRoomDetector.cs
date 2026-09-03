using System.Drawing;
using System.Drawing.Imaging;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.UIAutomation.Input;
using ZoomAutoAdmit.UIAutomation.Interop;
using ZoomAutoAdmit.UIAutomation.Ocr;
using ZoomAutoAdmit.UIAutomation.Screen;

namespace ZoomAutoAdmit.WaitingRoomTester;

public sealed class DesktopWaitingRoomDetector
{
    private readonly WindowsNativeOcrEngine _ocrEngine = new();
    private readonly WindowsCursorController _cursorController = new();
    private readonly WindowsMouseInput _mouseInput = new();
    private readonly HandledNotificationCache _handledCache = new(TimeSpan.FromSeconds(3));
    private readonly HandledBatchCache _handledBatchCache = new(TimeSpan.FromSeconds(3));
    private readonly DesktopWaitingRoomUiaAdmitter _uiaAdmitter = new();
    private int _scanCount = 0;

    /// <summary>
    /// Clicks once for this admit attempt, and says so when it does not.
    /// </summary>
    /// <remarks>
    /// SingleClickExecutor refuses every call after its first one, so it has to be built per
    /// attempt. Holding one in a field made the watcher admit exactly one person per run: from
    /// the second participant onwards TryClick returned false without clicking and without
    /// logging, while the toast kept being detected every few seconds forever.
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

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        _scanCount++;
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

        // Step 2: Check floating Waiting Room Toast (single user popup)
        var toastResult = WaitingRoomToastDetector.Detect(effectiveOcr);
        var toastCandidate = toastResult.BestCandidate;
        if (toastCandidate != null && toastCandidate.IsAccepted && !_handledCache.IsSuppressed(toastCandidate, DateTimeOffset.UtcNow))
        {
            TesterLogger.Desktop($"Toast detected for participant: '{toastCandidate.ParticipantName}' (Confidence: {toastCandidate.Confidence:P0})");
            _handledCache.MarkHandled(toastCandidate, DateTimeOffset.UtcNow);

            int clickX = checked((int)Math.Round(toastCandidate.AdmitCenter.X));
            int clickY = checked((int)Math.Round(toastCandidate.AdmitCenter.Y));

            if (TryClick(clickX, clickY))
            {
                TesterLogger.Action($"Admitted participant from toast: '{toastCandidate.ParticipantName}'");
                return;
            }
        }

        // Step 3: Check Participants Panel (docked or floating)
        var panel = WaitingRoomParticipantRowDetector.Detect(effectiveOcr);
        if (panel.IsPanelVisible && panel.WaitingRoomHeader != null)
        {
            int waitingCount = panel.DeclaredWaitingCount ?? panel.Rows.Count;

            // 3A. Check "Admit all" if 2+ waiting participants
            if (waitingCount >= 2 || panel.Rows.Count >= 2)
            {
                var admitAll = PanelAdmitAllDetector.Detect(effectiveOcr);
                if (admitAll.IsAccepted && !_handledBatchCache.IsSuppressed(admitAll, DateTimeOffset.UtcNow))
                {
                    TesterLogger.Desktop($"Panel 'Admit all' detected ({waitingCount} participants waiting)");
                    _handledBatchCache.MarkHandled(admitAll, DateTimeOffset.UtcNow);

                    int clickX = checked((int)Math.Round(admitAll.AdmitAllCenter.X));
                    int clickY = checked((int)Math.Round(admitAll.AdmitAllCenter.Y));

                    if (TryClick(clickX, clickY))
                    {
                        TesterLogger.Action($"Clicked 'Admit all' ({waitingCount} participants)");
                        return;
                    }
                }
            }

            // 3B. Individual Participant Row Hover and Admit
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

                            if (TryClick(admitClickX, admitClickY))
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

                                if (TryClick(admitClickX, admitClickY))
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
