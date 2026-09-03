using System.Text.RegularExpressions;
using ZoomAutoAdmit.Core.Models;

namespace ZoomAutoAdmit.Core.Matching;

/// <summary>
/// Detects both Zoom in-meeting toasts and Windows/Zoom notification cards by
/// pairing each Admit with only its local View and local waiting-room header.
/// </summary>
public static class WaitingRoomToastDetector
{
    private static readonly Regex CodePatternRegex = new(
        @"(?:[\{\}\(\);=\[\]<>]|//|/\*|\*/|\b(?:public|private|static|void|class|record|struct|string|int|var|bool|function|return|import|export|const|let|async|await|Console|WriteLine|def|npm|dotnet|csproj)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FullWaitingPhraseRegex = new(
        @"^(?<name>.+?)\s+(?:has\s+)?entered\s+the\s+waiting\s+room$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WaitingRoomSignalRegex = new(
        @"\bwaiting\s+room\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static WaitingRoomToastDetectionResult Detect(OcrResult ocrResult)
    {
        var result = new WaitingRoomToastDetectionResult { Timestamp = DateTime.UtcNow };
        if (ocrResult.Words.Count == 0 && ocrResult.Lines.Count == 0)
        {
            return result;
        }

        var admitWords = ocrResult.Words.Where(word => CleanWord(word.Text).Equals("Admit", StringComparison.OrdinalIgnoreCase)).ToList();
        var viewWords = ocrResult.Words.Where(word => CleanWord(word.Text).Equals("View", StringComparison.OrdinalIgnoreCase)).ToList();
        var recognizedHeaders = new List<OcrLine>();
        var candidates = new List<WaitingRoomToastCandidate>();

        foreach (var admit in admitWords)
        {
            var candidate = EvaluateLocalCandidate(admit, viewWords, ocrResult, out var recognizedHeader);
            candidates.Add(candidate);
            if (recognizedHeader != null) recognizedHeaders.Add(recognizedHeader);
        }

        result.AllAdmitWordsFound = admitWords;
        result.AllViewWordsFound = viewWords;
        result.AllWaitingRoomLinesFound = recognizedHeaders;
        result.AllCandidates = candidates;
        result.BestCandidate = candidates
            .Where(candidate => candidate.IsAccepted)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.ToastBounds.Y)
            .ThenBy(candidate => candidate.ToastBounds.X)
            .FirstOrDefault();
        return result;
    }

    private static WaitingRoomToastCandidate EvaluateLocalCandidate(
        OcrWord admit,
        IReadOnlyList<OcrWord> allViews,
        OcrResult ocr,
        out OcrLine? recognizedHeader)
    {
        var candidate = new WaitingRoomToastCandidate { AdmitWord = admit, AdmitCenter = admit.Center };
        recognizedHeader = null;

        var containingLine = FindContainingLine(ocr.Lines, admit);
        if (containingLine != null && CodePatternRegex.IsMatch(containingLine.Text))
            candidate.RejectionReasons.Add($"Admit word appears in code/terminal context: '{containingLine.Text.Trim()}'");

        if (admit.Bounds.Height < 5 || admit.Bounds.Height > 60 || admit.Bounds.Width < 15 || admit.Bounds.Width > 180)
            candidate.RejectionReasons.Add($"Admit bounding box dimensions unrealistic for button text: {admit.Bounds.Width:F0}x{admit.Bounds.Height:F0}px");
        else
            candidate.AcceptanceReasons.Add($"Valid Admit button text dimensions: {admit.Bounds.Width:F0}x{admit.Bounds.Height:F0}px");

        var localView = allViews
            .Select(view => new
            {
                View = view,
                Dy = Math.Abs(admit.Bounds.Y - view.Bounds.Y),
                Dx = view.Bounds.X - (admit.Bounds.X + admit.Bounds.Width)
            })
            .Where(item => item.Dy <= Math.Max(15.0, admit.Bounds.Height * 0.8) && item.Dx > 10 && item.Dx < 400)
            .OrderBy(item => item.Dy)
            .ThenBy(item => item.Dx)
            .FirstOrDefault();

        if (localView == null)
        {
            candidate.RejectionReasons.Add("No matching 'View' button found in the local same-row region to the right of this Admit.");
            FinalizeCandidate(candidate);
            return candidate;
        }

        candidate.ViewWord = localView.View;
        candidate.AcceptanceReasons.Add($"Locally paired View on same row (dy={localView.Dy:F1}px, gap={localView.Dx:F1}px).");

        var headerMatch = FindLocalHeader(admit, localView.View, ocr.Lines);
        if (headerMatch == null)
        {
            candidate.RejectionReasons.Add("No local '<participant> entered the waiting room' structure found directly above this button row.");
            candidate.ToastBounds = UnionWithPadding([admit.Bounds, localView.View.Bounds], 10, 10, 20, 15, ocr.ImageBounds);
            FinalizeCandidate(candidate);
            return candidate;
        }

        recognizedHeader = headerMatch.HeaderLine;
        candidate.HeaderLine = headerMatch.HeaderLine;
        candidate.ParticipantRawText = headerMatch.RawParticipantText;
        candidate.ParticipantNormalizedName = headerMatch.NormalizedParticipantName;
        candidate.ParticipantName = headerMatch.CompatibilityParticipantText;
        candidate.LayoutType = headerMatch.Layout;
        candidate.AcceptanceReasons.Add($"Local waiting-room structure: '{headerMatch.HeaderLine.Text}'.");
        candidate.AcceptanceReasons.Add($"Participant normalized as '{headerMatch.NormalizedParticipantName}'.");
        candidate.AcceptanceReasons.Add($"Notification layout classified as {headerMatch.Layout}.");

        candidate.ToastBounds = headerMatch.PreserveOldToastBounds
            ? UnionWithPadding([headerMatch.AnchorLine.Bounds, admit.Bounds, localView.View.Bounds], 15, 15, 30, 15, ocr.ImageBounds)
            : UnionWithPadding(
                headerMatch.SourceLines.Select(line => line.Bounds).Concat([admit.Bounds, localView.View.Bounds]),
                15, 15, 30, 15, ocr.ImageBounds);
        candidate.AcceptanceReasons.Add($"Calculated local notification bounds: {candidate.ToastBounds}.");
        FinalizeCandidate(candidate);
        return candidate;
    }

    private static HeaderMatch? FindLocalHeader(OcrWord admit, OcrWord view, IReadOnlyList<OcrLine> allLines)
    {
        double buttonLeft = admit.Bounds.X;
        double buttonRight = view.Bounds.X + view.Bounds.Width;
        double admitCenterY = admit.Bounds.Y + admit.Bounds.Height / 2.0;

        // 1. Same-Row Horizontal InMeetingToast (Layout B)
        // <participant> entered the waiting room    Admit    View
        var sameRowLines = allLines
            .Where(line =>
                !LineIsOnlyAction(line.Text) &&
                Math.Abs((line.Bounds.Y + line.Bounds.Height / 2.0) - admitCenterY) <= Math.Max(15.0, admit.Bounds.Height * 1.5) &&
                line.Bounds.X + line.Bounds.Width <= admit.Bounds.X + 15 &&
                admit.Bounds.X - (line.Bounds.X + line.Bounds.Width) >= -15 &&
                admit.Bounds.X - (line.Bounds.X + line.Bounds.Width) <= 250)
            .OrderBy(line => line.Bounds.X)
            .ToList();

        foreach (var line in sameRowLines)
        {
            string combined = NormalizeSpaces(line.Text);
            var phrase = FullWaitingPhraseRegex.Match(combined);
            if (!phrase.Success) continue;

            string normalizedName = CleanParticipantName(phrase.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(normalizedName) || CodePatternRegex.IsMatch(combined)) continue;

            string rawText = phrase.Groups["name"].Value.Trim();
            return new HeaderMatch(
                line,
                [line],
                line,
                rawText,
                normalizedName,
                normalizedName,
                WaitingRoomNotificationLayout.InMeetingToast,
                PreserveOldToastBounds: false);
        }

        // 2. Stacked Layout (Layout A)
        // <participant> entered the waiting room
        //            Admit    View
        var localLines = allLines
            .Where(line =>
                line.Bounds.Y + line.Bounds.Height <= admit.Bounds.Y + 5 &&
                admit.Bounds.Y - (line.Bounds.Y + line.Bounds.Height) <= 180 &&
                line.Bounds.X + line.Bounds.Width >= buttonLeft - 140 &&
                line.Bounds.X <= buttonRight + 140 &&
                !LineIsOnlyAction(line.Text))
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToList();

        var possibilities = new List<IReadOnlyList<OcrLine>>();
        possibilities.AddRange(localLines.Select(line => (IReadOnlyList<OcrLine>)[line]));
        for (int firstIndex = 0; firstIndex < localLines.Count; firstIndex++)
        {
            for (int secondIndex = firstIndex + 1; secondIndex < localLines.Count; secondIndex++)
            {
                var first = localLines[firstIndex];
                var second = localLines[secondIndex];
                double gap = second.Bounds.Y - (first.Bounds.Y + first.Bounds.Height);
                if (gap < -5 || gap > 35 || Math.Abs(first.Bounds.X - second.Bounds.X) > 140) continue;
                possibilities.Add([first, second]);
            }
        }

        HeaderMatch? best = null;
        double bestScore = double.MinValue;
        foreach (var sourceLines in possibilities)
        {
            string combined = NormalizeSpaces(string.Join(" ", sourceLines.OrderBy(line => line.Bounds.Y).Select(line => line.Text)));
            var phrase = FullWaitingPhraseRegex.Match(combined);
            if (!phrase.Success) continue;

            string normalizedName = CleanParticipantName(phrase.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(normalizedName) || CodePatternRegex.IsMatch(combined)) continue;

            var ordered = sourceLines.OrderBy(line => line.Bounds.Y).ToList();
            string firstText = NormalizeSpaces(ordered[0].Text);
            bool splitAfterWaiting = ordered.Count > 1 &&
                                     Regex.IsMatch(firstText, @"\bentered\s+the\s+waiting$", RegexOptions.IgnoreCase) &&
                                     Regex.IsMatch(ordered[1].Text.Trim(), @"^room$", RegexOptions.IgnoreCase);
            bool splitAfterEnteredThe = ordered.Count > 1 &&
                                        Regex.IsMatch(firstText, @"\bentered\s+the$", RegexOptions.IgnoreCase) &&
                                        WaitingRoomSignalRegex.IsMatch(ordered[1].Text);
            bool zoomEvidence = localLines.Any(line =>
                line.Text.Trim().Equals("Zoom", StringComparison.OrdinalIgnoreCase) && line.Bounds.Y <= ordered[0].Bounds.Y);
            var layout = splitAfterWaiting || zoomEvidence
                ? WaitingRoomNotificationLayout.WindowsNotification
                : WaitingRoomNotificationLayout.InMeetingToast;
            string rawText = ordered.Count > 1 ? ordered[0].Text.Trim() : phrase.Groups["name"].Value.Trim();
            string compatibilityText = splitAfterEnteredThe ? rawText : normalizedName;
            var compositeBounds = Union(ordered.Select(line => line.Bounds));
            double horizontalOverlap = OverlapLength(
                compositeBounds.X,
                compositeBounds.X + compositeBounds.Width,
                buttonLeft,
                buttonRight);
            if (horizontalOverlap < 10) continue;

            var composite = new OcrLine(combined, compositeBounds, ordered.SelectMany(line => line.Words).ToList());
            var anchor = splitAfterEnteredThe ? ordered[1] : composite;
            double verticalGap = admit.Bounds.Y - (compositeBounds.Y + compositeBounds.Height);
            double score = 1000 - Math.Abs(verticalGap) + Math.Min(horizontalOverlap, 250) + (ordered.Count == 1 ? 5 : 10);
            if (score <= bestScore) continue;

            bestScore = score;
            best = new HeaderMatch(
                composite,
                ordered,
                anchor,
                rawText,
                normalizedName,
                compatibilityText,
                layout,
                splitAfterEnteredThe);
        }
        return best;
    }

    private static void FinalizeCandidate(WaitingRoomToastCandidate candidate)
    {
        double score = 0;
        if (candidate.AdmitWord != null) score += 0.20;
        if (candidate.ViewWord != null) score += 0.30;
        if (candidate.HeaderLine != null) score += 0.35;
        if (!string.IsNullOrWhiteSpace(candidate.ParticipantNormalizedName) || !string.IsNullOrWhiteSpace(candidate.ParticipantName)) score += 0.10;
        if (candidate.RejectionReasons.Count == 0) score += 0.04;
        candidate.Confidence = Math.Clamp(score, 0, 0.99);
        candidate.IsAccepted = candidate.AdmitWord != null &&
                               candidate.ViewWord != null &&
                               candidate.HeaderLine != null &&
                               !string.IsNullOrWhiteSpace(candidate.ParticipantNormalizedName) &&
                               candidate.RejectionReasons.Count == 0 &&
                               candidate.Confidence >= 0.85;
    }

    private static OcrLine? FindContainingLine(IReadOnlyList<OcrLine> lines, OcrWord word) =>
        lines.FirstOrDefault(line =>
            line.Bounds.Y <= word.Bounds.Y + 5 && line.Bounds.Y + line.Bounds.Height >= word.Bounds.Y - 5 &&
            line.Bounds.X <= word.Bounds.X + 5 && line.Bounds.X + line.Bounds.Width >= word.Bounds.X - 5);

    private static BoundingRectangleInfo UnionWithPadding(
        IEnumerable<BoundingRectangleInfo> bounds,
        double leftPadding,
        double topPadding,
        double rightPadding,
        double bottomPadding,
        BoundingRectangleInfo imageBounds)
    {
        var union = Union(bounds);
        double left = Math.Max(imageBounds.X, union.X - leftPadding);
        double top = Math.Max(imageBounds.Y, union.Y - topPadding);
        double right = Math.Min(imageBounds.X + imageBounds.Width, union.X + union.Width + rightPadding);
        double bottom = Math.Min(imageBounds.Y + imageBounds.Height, union.Y + union.Height + bottomPadding);
        return new BoundingRectangleInfo(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static BoundingRectangleInfo Union(IEnumerable<BoundingRectangleInfo> bounds)
    {
        var values = bounds.ToList();
        double left = values.Min(value => value.X);
        double top = values.Min(value => value.Y);
        double right = values.Max(value => value.X + value.Width);
        double bottom = values.Max(value => value.Y + value.Height);
        return new BoundingRectangleInfo(left, top, right - left, bottom - top);
    }

    private static bool LineIsOnlyAction(string text) =>
        Regex.IsMatch(text.Trim(), @"^(?:Admit|View|Admit\s+View)$", RegexOptions.IgnoreCase);
    private static string NormalizeSpaces(string text) => Regex.Replace(text.Trim(), @"\s+", " ");
    private static string CleanWord(string word) => word.Trim(' ', '\t', '\r', '\n', '"', '\'', '`', ':', '.', ',', ';', '(', ')', '[', ']');
    private static string CleanParticipantName(string name) => name.Trim(' ', '\t', '\r', '\n', '"', '\'', '`', ':', '-', '•');
    private static double OverlapLength(double firstLeft, double firstRight, double secondLeft, double secondRight) =>
        Math.Max(0, Math.Min(firstRight, secondRight) - Math.Max(firstLeft, secondLeft));

    private sealed record HeaderMatch(
        OcrLine HeaderLine,
        IReadOnlyList<OcrLine> SourceLines,
        OcrLine AnchorLine,
        string RawParticipantText,
        string NormalizedParticipantName,
        string CompatibilityParticipantText,
        WaitingRoomNotificationLayout Layout,
        bool PreserveOldToastBounds);
}
