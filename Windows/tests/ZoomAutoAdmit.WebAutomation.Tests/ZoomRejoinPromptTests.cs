using ZoomAutoAdmit.WebAutomation;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// Zoom's own "could not join" panel. It covers the meeting, and the participants list goes with
/// it - so while it is up nobody is admitted and no attendance is counted. Seen live on 2026-09-24:
/// a class sat behind it from 18:54 until the class was over.
/// </summary>
public class ZoomRejoinPromptTests
{
    private const string TheRealOne =
        "Joining Meeting Timeout or Browser restriction\n" +
        "Your network connection has timed out or your organization has disabled access to Zoom " +
        "from the browser. Please verify your network connection or organizational policy.\n" +
        "Report Problem  Retry  Leave";

    [Theory]
    [InlineData(TheRealOne)]
    [InlineData("Unable to join the meeting. Please check your connection.")]
    [InlineData("Connection failed")]
    [InlineData("Reconnecting…")]
    public void ZoomSayingItCouldNotJoinIsRecognised(string pageText) =>
        Assert.True(ZoomLauncherPage.LooksLikeJoinFailed(pageText));

    [Theory]
    // An ordinary meeting, including the words that appear in it, is never taken for a failure.
    [InlineData("Participants (16)  Chat  Share  Record  End")]
    [InlineData("Waiting Room (1) Amal Abdelrazek (Guest) Admit")]
    [InlineData("")]
    [InlineData(null)]
    public void AMeetingThatIsRunningIsNot(string? pageText) =>
        Assert.False(ZoomLauncherPage.LooksLikeJoinFailed(pageText));
}
