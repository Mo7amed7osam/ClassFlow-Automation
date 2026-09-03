using ZoomAutoAdmit.Core.Models;

namespace ZoomAutoAdmit.Core.Matching;

public sealed record NotificationSurfaceDecision(bool IsAllowed, string Reason);

public static class NotificationSurfacePolicy
{
    private static readonly string[] NotificationProcesses =
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost"
    };

    private static readonly string[] BrowserOrIdeProcesses =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "code", "devenv", "notepad", "chatgpt"
    };

    public static NotificationSurfaceDecision Evaluate(
        WaitingRoomNotificationLayout layout,
        string? processName,
        string? windowClass,
        string? windowTitle,
        bool hasZoomParentOwnerChain = false)
    {
        string process = processName?.Trim() ?? string.Empty;

        // 1. Explicitly disallow browser / IDE / document hosts from claiming parent Zoom ownership for in-meeting toasts (e.g. Zoom screenshot/video playback)
        if (BrowserOrIdeProcesses.Any(b => process.Equals(b, StringComparison.OrdinalIgnoreCase) || process.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
            return new NotificationSurfaceDecision(false, $"Non-Zoom application surface '{process}' at target point. Positive Zoom ownership required.");
        }

        // 2. Positive Zoom Process Verification
        if (process.Contains("zoom", StringComparison.OrdinalIgnoreCase) ||
            process.Equals("cptHost", StringComparison.OrdinalIgnoreCase))
        {
            return new NotificationSurfaceDecision(true, $"Candidate belongs to verified live Zoom surface '{process}'.");
        }

        // 3. Verified Zoom Parent/Owner Window Chain (e.g. dwm or GPU child)
        if (layout == WaitingRoomNotificationLayout.InMeetingToast && hasZoomParentOwnerChain)
        {
            return new NotificationSurfaceDecision(
                true,
                $"Candidate target has a verified Zoom HWND in its parent/owner chain (root process '{process}').");
        }

        // 4. Positive Windows Notification Host Verification.
        //
        // Deliberately independent of the layout: the layout is only a guess made from the shape
        // of the OCR text, while the host process is direct evidence of what is being clicked.
        // A real Zoom waiting-room alert arriving as a Windows notification reads
        //     "<participant> entered the"  /  "waiting room"
        // which the classifier labels InMeetingToast, because it only calls something a
        // WindowsNotification when the break falls after "waiting" or a bare "Zoom" line is
        // recognised above it - and the Zoom logo on that card is an image, not text. Gating this
        // rule on the guess rejected genuine notifications that rule 2 could never accept either,
        // so nothing was ever admitted from them. Requiring a verified notification host keeps the
        // guarantee that matters: no click lands on some unrelated application.
        if (NotificationProcesses.Any(allowed => process.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            return new NotificationSurfaceDecision(true, $"Candidate belongs to verified Windows notification host '{process}'.");
        }

        // 5. Any other application is strictly non-actionable
        return new NotificationSurfaceDecision(
            false,
            $"Non-Zoom application surface '{process}' at target point (class='{windowClass}', title='{windowTitle}'). Positive Zoom ownership required.");
    }
}

public sealed class HandledNotificationCache
{
    private readonly TimeSpan _ttl;
    private readonly List<Entry> _entries = new();

    public HandledNotificationCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromSeconds(20);
    }

    public bool IsSuppressed(WaitingRoomToastCandidate candidate, DateTimeOffset now)
    {
        _entries.RemoveAll(entry => now - entry.HandledAt >= _ttl);
        return _entries.Any(entry => entry.ParticipantOnly
            ? entry.Participant.Equals(NormalizeName(candidate), StringComparison.OrdinalIgnoreCase)
            : SameNeighborhood(entry, candidate));
    }

    public bool IsParticipantSuppressed(string participantName, DateTimeOffset now)
    {
        _entries.RemoveAll(entry => now - entry.HandledAt >= _ttl);
        string normalized = NormalizeName(participantName);
        return _entries.Any(entry => entry.Participant.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void MarkHandled(WaitingRoomToastCandidate candidate, DateTimeOffset now)
    {
        _entries.RemoveAll(entry => now - entry.HandledAt >= _ttl);
        _entries.Add(new Entry(
            NormalizeName(candidate),
            candidate.LayoutType,
            Center(candidate.ToastBounds),
            now,
            ParticipantOnly: false));
    }

    public void MarkParticipantHandled(string participantName, DateTimeOffset now)
    {
        _entries.RemoveAll(entry => now - entry.HandledAt >= _ttl);
        _entries.Add(new Entry(
            NormalizeName(participantName),
            WaitingRoomNotificationLayout.Unknown,
            (0, 0),
            now,
            ParticipantOnly: true));
    }

    private static bool SameNeighborhood(Entry entry, WaitingRoomToastCandidate candidate)
    {
        var center = Center(candidate.ToastBounds);
        double distance = Math.Sqrt(Math.Pow(entry.Center.X - center.X, 2) + Math.Pow(entry.Center.Y - center.Y, 2));
        return entry.Participant.Equals(NormalizeName(candidate), StringComparison.OrdinalIgnoreCase) &&
               entry.Layout == candidate.LayoutType &&
               distance <= 100;
    }

    private static string NormalizeName(WaitingRoomToastCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.ParticipantNormalizedName)
            ? candidate.ParticipantNormalizedName.Trim()
            : WaitingRoomParticipantIdentity.FromAcceptedCandidateText(candidate.ParticipantName).NormalizedName;

    private static string NormalizeName(string participantName) =>
        WaitingRoomParticipantIdentity.FromAcceptedCandidateText(participantName).NormalizedName;

    private static (double X, double Y) Center(BoundingRectangleInfo bounds) =>
        (bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);

    private sealed record Entry(
        string Participant,
        WaitingRoomNotificationLayout Layout,
        (double X, double Y) Center,
        DateTimeOffset HandledAt,
        bool ParticipantOnly);
}

public static class ContinuousNotificationSelector
{
    public static IReadOnlyList<WaitingRoomToastCandidate> EligibleCandidates(
        IEnumerable<WaitingRoomToastCandidate> candidates,
        HandledNotificationCache handledCache,
        DateTimeOffset now) =>
        candidates
            .Where(candidate => candidate.IsAccepted &&
                                candidate.Confidence >= AdmitOnceSafetyGate.HighConfidence &&
                                candidate.LayoutType != WaitingRoomNotificationLayout.Unknown &&
                                !string.IsNullOrWhiteSpace(candidate.ParticipantNormalizedName) &&
                                !handledCache.IsSuppressed(candidate, now))
            .OrderBy(candidate => candidate.LayoutType == WaitingRoomNotificationLayout.InMeetingToast ? 0 : 1)
            .ThenBy(candidate => candidate.ToastBounds.Y)
            .ThenBy(candidate => candidate.ToastBounds.X)
            .ToList();

    public static WaitingRoomToastCandidate? FindSameNotification(
        WaitingRoomToastCandidate expected,
        IEnumerable<WaitingRoomToastCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.IsAccepted &&
                                candidate.Confidence >= AdmitOnceSafetyGate.HighConfidence &&
                                candidate.LayoutType == expected.LayoutType &&
                                candidate.ParticipantNormalizedName.Equals(
                                    expected.ParticipantNormalizedName,
                                    StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new
            {
                Candidate = candidate,
                Overlap = IntersectionOverUnion(expected.ToastBounds, candidate.ToastBounds),
                CenterDistance = Distance(expected.AdmitCenter, candidate.AdmitCenter)
            })
            .Where(match => match.Overlap >= AdmitOnceSafetyGate.MinimumToastOverlap &&
                            match.CenterDistance <= AdmitOnceSafetyGate.CenterMovementTolerance)
            .OrderByDescending(match => match.Overlap)
            .ThenBy(match => match.CenterDistance)
            .Select(match => match.Candidate)
            .FirstOrDefault();
    }

    private static double Distance((double X, double Y) first, (double X, double Y) second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static double IntersectionOverUnion(BoundingRectangleInfo first, BoundingRectangleInfo second)
    {
        double left = Math.Max(first.X, second.X);
        double top = Math.Max(first.Y, second.Y);
        double right = Math.Min(first.X + first.Width, second.X + second.Width);
        double bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        double intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double union = first.Width * first.Height + second.Width * second.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

public enum AutoAdmitPathKind
{
    None,
    InMeetingToast,
    WindowsNotification,
    MultiPersonNotification,
    PanelAdmitAll,
    ParticipantsPanel
}

public static class AutoAdmitPrioritySelector
{
    public static AutoAdmitPathKind Choose(
        IReadOnlyList<WaitingRoomToastCandidate> eligibleToasts,
        MultiPersonWaitingNotificationCandidate? multiPersonNotification,
        PanelAdmitAllCandidate? panelAdmitAll,
        WaitingParticipantRowCandidate? eligiblePanelRow)
    {
        if (eligibleToasts.Any(candidate => candidate.LayoutType == WaitingRoomNotificationLayout.InMeetingToast))
            return AutoAdmitPathKind.InMeetingToast;
        if (eligibleToasts.Any(candidate => candidate.LayoutType == WaitingRoomNotificationLayout.WindowsNotification))
            return AutoAdmitPathKind.WindowsNotification;
        if (multiPersonNotification?.IsAccepted == true)
            return AutoAdmitPathKind.MultiPersonNotification;
        if (panelAdmitAll?.IsAccepted == true)
            return AutoAdmitPathKind.PanelAdmitAll;
        return eligiblePanelRow != null ? AutoAdmitPathKind.ParticipantsPanel : AutoAdmitPathKind.None;
    }
}
