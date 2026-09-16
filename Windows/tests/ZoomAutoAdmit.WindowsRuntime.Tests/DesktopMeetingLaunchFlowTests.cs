using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class DesktopMeetingLaunchFlowTests
{
    private static readonly Uri Url = new("https://zoom.us/j/91310623669");

    [Theory]
    [InlineData("https://zoom.us/j/91310623669", "91310623669")]
    [InlineData("https://zoom.us/j/93181040158?pwd=example", "93181040158")]
    public void ExtractsCorrectGroupMeetingId(string url, string id) =>
        Assert.Equal(id, DesktopMeetingLaunchFlow.ExtractMeetingId(new Uri(url)));

    [Theory]
    [InlineData("https://example.com/j/91310623669")]
    [InlineData("https://zoom.us/j/123")]
    [InlineData("https://zoom.us/j/123456789012")]
    [InlineData("https://zoom.us/j/91310623669/93181040158")]
    public void RejectsInvalidOrAmbiguousIdBeforeAnyAction(string url)
    {
        var actions = new Fake();
        Assert.Throws<ArgumentException>(() => new DesktopMeetingLaunchFlow(actions, 1).Run(new Uri(url), CancellationToken.None));
        Assert.Equal(0, actions.OpenCount);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void PreviewOrPasscodeProgressSuppressesFallback()
    {
        var actions = new Fake { AfterOpen = DesktopLaunchState.Progress };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(1, actions.OpenCount);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void NoResponseOnHomeFallsBackOnceWithCorrectNumber()
    {
        var actions = new Fake();
        Assert.True(new DesktopMeetingLaunchFlow(actions, 2).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(1, actions.OpenCount);
        Assert.Equal(1, actions.JoinCount);
        Assert.Equal("91310623669", actions.JoinId);
    }

    [Fact]
    public void ProtocolExceptionFallsBackOnlyWhenStillOnHome()
    {
        var actions = new Fake { ThrowOnOpen = true };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(1, actions.JoinCount);
    }

    [Fact]
    public void ExceptionAfterProgressNeverSendsSecondLaunch()
    {
        var actions = new Fake { ThrowOnOpen = true, AfterOpen = DesktopLaunchState.Progress };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void UnknownStatePreventsFallback()
    {
        var actions = new Fake { AfterOpen = DesktopLaunchState.Unknown };
        Assert.False(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void ZoomLeftOnAnotherPageIsTakenToHomeAndTheMeetingOpens()
    {
        // After an account switch Zoom can come up on Chat or Meetings, where there is no Join.
        var actions = new Fake { State = DesktopLaunchState.Unknown, AfterOpen = DesktopLaunchState.Progress };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(1, actions.GoHomeCount);
        Assert.Equal(1, actions.OpenCount);
    }

    [Fact]
    public void WhenHomeCannotBeOpenedNothingIsLaunched()
    {
        var actions = new Fake { State = DesktopLaunchState.Unknown, AfterGoHome = null };
        Assert.False(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.OpenCount);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void AZoomAlreadyOnHomeIsLeftAlone()
    {
        var actions = new Fake { AfterOpen = DesktopLaunchState.Progress };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.GoHomeCount);
    }

    [Fact]
    public void ExistingJoinDialogPreventsNewLaunch()
    {
        var actions = new Fake { State = DesktopLaunchState.Progress };
        Assert.False(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.OpenCount);
        Assert.Equal(0, actions.JoinCount);
        Assert.Equal(0, actions.GoHomeCount);            // an open dialog is never navigated away from
    }

    [Fact]
    public void LateProgressAtFinalCheckSuppressesFallback()
    {
        var actions = new Fake { ProgressAtRead = 3 };
        Assert.True(new DesktopMeetingLaunchFlow(actions, 1).Run(Url, CancellationToken.None).IsSuccess);
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void CancellationDuringObservationDoesNotFallBack()
    {
        using var cancel = new CancellationTokenSource();
        var actions = new Fake { OnWait = cancel.Cancel };
        Assert.Throws<OperationCanceledException>(() => new DesktopMeetingLaunchFlow(actions, 1).Run(Url, cancel.Token));
        Assert.Equal(0, actions.JoinCount);
    }

    [Fact]
    public void OriginalProtocolKeepsPasscodeQuery()
    {
        string protocol = WindowsDesktopMeetingPlatform.CreateZoomDesktopProtocolUrl(new Uri("https://zoom.us/j/91310623669?pwd=abc%2Bxyz"));
        Assert.Contains("confno=91310623669", protocol);
        Assert.Contains("pwd=abc%2Bxyz", protocol);
    }

    private sealed class Fake : IDesktopMeetingLaunchActions
    {
        public DesktopLaunchState State = DesktopLaunchState.Home;
        public DesktopLaunchState AfterOpen = DesktopLaunchState.Home;
        public bool ThrowOnOpen;
        public int OpenCount, JoinCount, ReadCount;
        public int ProgressAtRead = int.MaxValue;
        public string? JoinId;
        public Action? OnWait;
        public DesktopLaunchState ReadState() => ++ReadCount >= ProgressAtRead ? DesktopLaunchState.Progress : State;
        /// <summary>What Zoom shows once Home is selected (null: Home cannot be opened).</summary>
        public DesktopLaunchState? AfterGoHome = DesktopLaunchState.Home;
        public int GoHomeCount;
        public void GoHome(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            GoHomeCount++;
            if (AfterGoHome is not { } next) throw new InvalidOperationException("Home tab not found");
            State = next;
        }
        public void OpenLink(Uri url)
        {
            OpenCount++;
            State = AfterOpen;
            if (ThrowOnOpen) throw new InvalidOperationException("Protocol handler failed");
        }
        public void JoinById(string id, CancellationToken token) { token.ThrowIfCancellationRequested(); JoinCount++; JoinId = id; }
        public void Wait(CancellationToken token) { OnWait?.Invoke(); token.ThrowIfCancellationRequested(); }
    }
}
