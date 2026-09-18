using ZoomAutoAdmit.WindowsRuntime;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

/// <summary>The toolbar names Zoom really uses, so the host never joins a class with the mic on.</summary>
public sealed class HostMediaButtonTests
{
    [Theory]
    [InlineData("Mute", true)]
    [InlineData("Mute my microphone (Alt+A)", true)]                       // Zoom Workplace, 2026-09
    [InlineData("Mute my audio (Alt+A)", true)]
    [InlineData("Mute all (Alt+M)", false)]                                // the participants panel's button
    [InlineData("Unmute my audio (Alt+A). Or you can simply press and hold the Space bar. ", false)]
    public void TheMuteButtonIsTheHostsOwnMicrophone(string name, bool matches) =>
        Assert.Equal(matches, WindowsDesktopAutoAdmitPreparation.MuteButton.IsMatch(name));

    [Theory]
    [InlineData("Unmute", true)]
    [InlineData("Unmute my audio (Alt+A). Or you can simply press and hold the Space bar. ", true)]
    [InlineData("Mute my microphone (Alt+A)", false)]
    public void TheUnmuteButtonMeansItIsAlreadyOff(string name, bool matches) =>
        Assert.Equal(matches, WindowsDesktopAutoAdmitPreparation.UnmuteButton.IsMatch(name));

    [Theory]
    [InlineData("Stop Video", true)]
    [InlineData("Stop my video, Alt+V", true)]
    [InlineData("Start my video, Alt+V", false)]
    public void TheVideoButtonIsFoundHoweverZoomWordsIt(string name, bool matches) =>
        Assert.Equal(matches, WindowsDesktopAutoAdmitPreparation.StopVideoButton.IsMatch(name));

    [Theory]
    [InlineData("Start my video, Alt+V", true)]
    [InlineData("Start Video", true)]
    [InlineData("Stop my video, Alt+V", false)]
    public void TheStartVideoButtonMeansTheCameraIsAlreadyOff(string name, bool matches) =>
        Assert.Equal(matches, WindowsDesktopAutoAdmitPreparation.StartVideoButton.IsMatch(name));
}

public sealed class HostOwnControlTests
{
    [Theory]
    [InlineData("Mute my microphone (Alt+A)", true)]
    [InlineData("Unmute my audio (Alt+A). Or you can simply press and hold the Space bar. ", true)]
    [InlineData("Start my video, Alt+V", true)]
    [InlineData("Start video (Alt+V)", true)]
    [InlineData("Mute", false)]                     // a participants row's button says nothing about whose it is
    [InlineData("Start video", false)]
    public void AControlThatNamesItselfAsTheHostsIsPreferred(string name, bool mine) =>
        Assert.Equal(mine, WindowsDesktopAutoAdmitPreparation.MyOwnControl.IsMatch(name));
}
