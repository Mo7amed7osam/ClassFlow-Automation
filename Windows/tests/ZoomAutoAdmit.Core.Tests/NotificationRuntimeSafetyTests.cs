using ZoomAutoAdmit.Core.Matching;
using ZoomAutoAdmit.Core.Models;
using Xunit;

namespace ZoomAutoAdmit.Core.Tests;

public class NotificationRuntimeSafetyTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("Code")]
    [InlineData("WindowsTerminal")]
    [InlineData("wt")]
    [InlineData("ChatGPT")]
    public void SurfacePolicy_DeniesScreenshotAndDeveloperToolProcesses(string process)
    {
        var decision = NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.WindowsNotification,
            process,
            "window-class",
            "window-title");

        Assert.False(decision.IsAllowed);
    }

    [Theory]
    [InlineData(WaitingRoomNotificationLayout.InMeetingToast, "Zoom")]
    [InlineData(WaitingRoomNotificationLayout.InMeetingToast, "CptHost")]
    [InlineData(WaitingRoomNotificationLayout.WindowsNotification, "explorer")]
    [InlineData(WaitingRoomNotificationLayout.WindowsNotification, "ShellExperienceHost")]
    public void SurfacePolicy_AllowsVerifiedZoomOrWindowsNotificationSurfaces(
        WaitingRoomNotificationLayout layout,
        string process)
    {
        Assert.True(NotificationSurfacePolicy.Evaluate(layout, process, "class", "title").IsAllowed);
    }

    [Fact]
    public void Selector_AfterFirstOfTwoIsHandled_LeavesSecondEligible()
    {
        var first = Candidate("Ahmed", 1400, 700);
        var second = Candidate("Zeyad", 1400, 880);
        var cache = new HandledNotificationCache(TimeSpan.FromSeconds(20));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(new[] { first, second }, ContinuousNotificationSelector.EligibleCandidates([second, first], cache, now));
        cache.MarkHandled(first, now);

        Assert.Equal(second, Assert.Single(ContinuousNotificationSelector.EligibleCandidates([first, second], cache, now)));
    }

    [Fact]
    public void Cache_SuppressesSameNotificationButAllowsSameParticipantLater()
    {
        var original = Candidate("Ahmed", 1400, 800);
        var sameNotification = Candidate("Ahmed", 1408, 806);
        var newPosition = Candidate("Ahmed", 1100, 500);
        var cache = new HandledNotificationCache(TimeSpan.FromSeconds(20));
        var now = DateTimeOffset.UtcNow;
        cache.MarkHandled(original, now);

        Assert.True(cache.IsSuppressed(sameNotification, now.AddSeconds(5)));
        Assert.False(cache.IsSuppressed(newPosition, now.AddSeconds(5)));
        Assert.False(cache.IsSuppressed(sameNotification, now.AddSeconds(21)));
    }

    [Fact]
    public void EligibleCandidates_PrioritizeInMeetingToastBeforeWindowsNotificationRegardlessOfPosition()
    {
        var windows = Candidate("Ahmed", 1400, 100, WaitingRoomNotificationLayout.WindowsNotification);
        var inMeeting = Candidate("Zeyad", 700, 800, WaitingRoomNotificationLayout.InMeetingToast);
        var cache = new HandledNotificationCache();

        var ordered = ContinuousNotificationSelector.EligibleCandidates([windows, inMeeting], cache, DateTimeOffset.UtcNow);

        Assert.Equal(new[] { inMeeting, windows }, ordered);
    }

    [Fact]
    public void PrioritySelector_UsesToastBeforePanelAndPanelWhenNoToastExists()
    {
        var inMeeting = Candidate("Ahmed", 700, 200, WaitingRoomNotificationLayout.InMeetingToast);
        var panelRow = new WaitingParticipantRowCandidate { ParticipantName = "Zeyad", Confidence = 0.99 };

        Assert.Equal(AutoAdmitPathKind.InMeetingToast, AutoAdmitPrioritySelector.Choose([inMeeting], null, null, panelRow));
        Assert.Equal(AutoAdmitPathKind.ParticipantsPanel, AutoAdmitPrioritySelector.Choose([], null, null, panelRow));
    }

    [Fact]
    public void ToastAttemptTemporarilySuppressesPanelFallbackForSameParticipantButNotAnother()
    {
        var cache = new HandledNotificationCache(TimeSpan.FromSeconds(20));
        var now = DateTimeOffset.UtcNow;
        cache.MarkHandled(Candidate("Ahmed", 1400, 800), now);

        Assert.True(cache.IsParticipantSuppressed("Ahmed", now.AddSeconds(5)));
        Assert.False(cache.IsParticipantSuppressed("Zeyad", now.AddSeconds(5)));
        Assert.False(cache.IsParticipantSuppressed("Ahmed", now.AddSeconds(21)));
    }

    [Fact]
    public void InMeetingGpuChildWithVerifiedZoomOwnerIsAllowedButBrowserRemainsDenied()
    {
        Assert.True(NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.InMeetingToast,
            "dwm",
            "GPUComposite",
            string.Empty,
            hasZoomParentOwnerChain: true).IsAllowed);
        Assert.False(NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.InMeetingToast,
            "chrome",
            "Chrome_WidgetWin",
            "Screenshot",
            hasZoomParentOwnerChain: true).IsAllowed);
    }

    [Fact]
    public void WindowsNotificationHostIsAllowedEvenWhenTheLayoutWasGuessedAsInMeetingToast()
    {
        // A real Zoom alert delivered as a Windows notification reads "<name> entered the" /
        // "waiting room", which the layout classifier labels InMeetingToast because the line break
        // falls after "the" and the Zoom logo on the card is an image rather than recognised text.
        // The host process is the evidence that matters; the layout guess must not veto it.
        Assert.True(NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.InMeetingToast,
            "explorer",
            "Windows.UI.Core.CoreWindow",
            "New notification").IsAllowed);

        // An unrelated application at the same point is still refused.
        Assert.False(NotificationSurfacePolicy.Evaluate(
            WaitingRoomNotificationLayout.InMeetingToast,
            "notepad",
            "Notepad",
            "Untitled").IsAllowed);
    }

    private static WaitingRoomToastCandidate Candidate(
        string participant,
        double x,
        double y,
        WaitingRoomNotificationLayout layout = WaitingRoomNotificationLayout.WindowsNotification)
    {
        var admit = new OcrWord("Admit", new(x, y + 60, 37, 10));
        return new WaitingRoomToastCandidate
        {
            ParticipantName = participant,
            ParticipantRawText = participant,
            ParticipantNormalizedName = participant,
            LayoutType = layout,
            ToastBounds = new(x - 15, y, 247, 85),
            AdmitWord = admit,
            ViewWord = new("View", new(x + 173, y + 60, 29, 10)),
            HeaderLine = new($"{participant} entered the waiting room", new(x, y, 220, 14), Array.Empty<OcrWord>()),
            AdmitCenter = admit.Center,
            Confidence = 0.99,
            IsAccepted = true
        };
    }
}
