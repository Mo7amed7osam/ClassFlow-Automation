using System.Collections.Concurrent;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Recordings;
using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// A link that is handed in - the API's path. Zoom and the dashboard are faked; the profile lock is
/// the real one on temporary files. Drive file ids are made up.
/// </summary>
public sealed class ProvidedRecordLinkTests : IDisposable
{
    private const string DriveLink = "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing";
    private static readonly DateOnly Today = new(2026, 9, 11);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zaa-provided-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class ZoomThatMustNotBeCalled : IRecordingLinkSource
    {
        public int Calls;
        public Task<ZoomRecordingLinkResult> ReadAsync(string group, string profile, DateOnly day, TimeOnly? startTime,
            bool headed, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Zoom was searched");
        }
    }

    private sealed class FakeDashboard : IRecordingLinkTarget
    {
        public readonly ConcurrentQueue<(string Group, string Link, DateOnly Day, TimeOnly? Time, bool DryRun, bool Replace, bool KeepOpen)> Calls = new();
        public Func<bool, LmsRunResult> Answer = _ => LmsRunResult.Success("the recording link was saved on the session.");
        public Func<Task>? During;

        public async Task<LmsRunResult> AttachAsync(string group, string recordLink, TimeOnly? startTime, DateOnly day, bool headed,
            bool dryRun, bool keepBrowserOpen, bool replaceExisting, CancellationToken cancellationToken)
        {
            Calls.Enqueue((group, recordLink, day, startTime, dryRun, replaceExisting, keepBrowserOpen));
            if (During != null) await During();
            return Answer(replaceExisting);
        }
    }

    private RecordingLinkProcessor Processor(ZoomThatMustNotBeCalled zoom, FakeDashboard dashboard, TimeSpan? wait = null) =>
        new(zoom, dashboard, new ProfileOperationLock(Path.Combine(_root, "locks"), Path.Combine(_root, "profiles")),
            accountProfile: null, log: _log.Add, lockWait: wait ?? TimeSpan.FromSeconds(10), today: () => Today);

    private static ProvidedRecordLinkRequest Request(string link = DriveLink, bool replace = false, bool dryRun = false, DateOnly? date = null) =>
        new() { Group = "AST5_DAT1_S1", RecordLink = link, Date = date ?? new DateOnly(2026, 9, 3), ReplaceExisting = replace, DryRun = dryRun };

    [Fact]
    public async Task TheExactDriveLinkGoesToTheDashboardAndZoomIsNeverTouched()
    {
        var zoom = new ZoomThatMustNotBeCalled();
        var dashboard = new FakeDashboard();
        var outcome = await Processor(zoom, dashboard).AttachProvidedLinkAsync(Request(), default);

        Assert.Equal(RecordingLinkStatus.Attached, outcome.Status);
        var call = Assert.Single(dashboard.Calls);
        Assert.Equal(DriveLink, call.Link);
        Assert.Equal("AST5_DAT1_S1", call.Group);
        Assert.Equal(new DateOnly(2026, 9, 3), call.Day);
        Assert.Null(call.Time);
        Assert.False(call.KeepOpen);
        Assert.Equal(0, zoom.Calls);
        Assert.Equal(RecordingLinkProcessor.DashboardProfile, outcome.Profile);
        Assert.Null(outcome.RecordingStartedAtUtc);
    }

    [Fact]
    public async Task NoDateMeansToday()
    {
        var dashboard = new FakeDashboard();
        var outcome = await Processor(new ZoomThatMustNotBeCalled(), dashboard)
            .AttachProvidedLinkAsync(new ProvidedRecordLinkRequest { Group = "AST5_DAT1_S1", RecordLink = DriveLink }, default);
        Assert.Equal(Today, Assert.Single(dashboard.Calls).Day);
        Assert.Equal(Today, outcome.Date);
    }

    [Fact]
    public async Task AZoomLinkHandedInIsStillAccepted()
    {
        var dashboard = new FakeDashboard();
        const string zoomLink = "https://zoom.us/rec/share/FAKE-TOKEN.NotReal?startTime=1788278291000";
        var outcome = await Processor(new ZoomThatMustNotBeCalled(), dashboard).AttachProvidedLinkAsync(Request(zoomLink), default);
        Assert.Equal(RecordingLinkStatus.Attached, outcome.Status);
        Assert.Equal(zoomLink, Assert.Single(dashboard.Calls).Link);
    }

    [Fact]
    public async Task AnUnacceptableLinkNeverReachesTheDashboard()
    {
        var dashboard = new FakeDashboard();
        var outcome = await Processor(new ZoomThatMustNotBeCalled(), dashboard)
            .AttachProvidedLinkAsync(Request("https://example.com/video.mp4"), default);
        Assert.Equal(RecordingLinkStatus.LmsFailed, outcome.Status);
        Assert.Equal("invalidLink", outcome.Reason);
        Assert.Empty(dashboard.Calls);
    }

    [Fact]
    public async Task ARepeatLeavesTheExistingLinkAndReplacingIsOnlyOnRequest()
    {
        var dashboard = new FakeDashboard
        {
            Answer = replace => replace
                ? LmsRunResult.Success("the recording link was saved on the session.")
                : LmsRunResult.Success("the session already has a recording link, so it was left as it is.") with { AlreadyExists = true },
        };
        var processor = Processor(new ZoomThatMustNotBeCalled(), dashboard);

        var repeat = await processor.AttachProvidedLinkAsync(Request(replace: false), default);
        Assert.Equal(RecordingLinkStatus.AlreadyExists, repeat.Status);
        Assert.True(repeat.IsSuccess);

        var replaced = await processor.AttachProvidedLinkAsync(Request(replace: true), default);
        Assert.Equal(RecordingLinkStatus.Attached, replaced.Status);
        Assert.Equal([false, true], dashboard.Calls.Select(call => call.Replace));
    }

    [Theory]
    [InlineData(LmsFailure.SessionNotFinished, "sessionNotFinished")]
    [InlineData(LmsFailure.SessionNotFound, "sessionNotFound")]
    [InlineData(LmsFailure.NotSignedIn, "lmsNotSignedIn")]
    [InlineData(LmsFailure.Failed, "lmsFailed")]
    public async Task ADashboardFailureCarriesItsReason(LmsFailure failure, string reason)
    {
        var dashboard = new FakeDashboard { Answer = _ => LmsRunResult.Fail(failure, "no") };
        var outcome = await Processor(new ZoomThatMustNotBeCalled(), dashboard).AttachProvidedLinkAsync(Request(), default);
        Assert.Equal(RecordingLinkStatus.LmsFailed, outcome.Status);
        Assert.Equal(reason, outcome.Reason);
    }

    [Fact]
    public async Task TheDashboardIsDrivenByOneRequestAtATime()
    {
        int inside = 0, overlaps = 0;
        var dashboard = new FakeDashboard
        {
            During = async () =>
            {
                if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
                await Task.Delay(250);
                Interlocked.Decrement(ref inside);
            },
        };
        var processor = Processor(new ZoomThatMustNotBeCalled(), dashboard);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => processor.AttachProvidedLinkAsync(Request(), default)));

        Assert.Equal(0, overlaps);
        Assert.All(results, result => Assert.Equal(RecordingLinkStatus.Attached, result.Status));
    }

    [Fact]
    public async Task ADashboardThatStaysBusyIsAnsweredWithBusy()
    {
        var gate = new TaskCompletionSource();
        var dashboard = new FakeDashboard { During = () => gate.Task };
        var processor = Processor(new ZoomThatMustNotBeCalled(), dashboard, wait: TimeSpan.FromMilliseconds(500));

        var first = processor.AttachProvidedLinkAsync(Request(), default);
        await Task.Delay(200);
        Assert.Equal(RecordingLinkStatus.Busy, (await processor.AttachProvidedLinkAsync(Request(), default)).Status);

        gate.SetResult();
        Assert.Equal(RecordingLinkStatus.Attached, (await first).Status);
    }

    [Fact]
    public async Task TheLogShowsAPreviewNotTheWholeLink()
    {
        await Processor(new ZoomThatMustNotBeCalled(), new FakeDashboard()).AttachProvidedLinkAsync(Request(), default);
        string log = string.Join("\n", _log);
        Assert.DoesNotContain("1AbCdEfGhIjKlMnOpQrStUvWxYz012345", log);
        Assert.Contains("Google Drive", log);
    }
}

/// <summary>What may be written on a session as its recording.</summary>
public sealed class RecordingLinksTests
{
    [Theory]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing", RecordingLinkKind.GoogleDrive)]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/preview", RecordingLinkKind.GoogleDrive)]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345", RecordingLinkKind.GoogleDrive)]
    [InlineData("https://zoom.us/rec/share/FAKE-TOKEN.NotReal?startTime=1788278291000", RecordingLinkKind.ZoomShare)]
    [InlineData("https://us02web.zoom.us/rec/play/abc123", RecordingLinkKind.ZoomShare)]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345", RecordingLinkKind.None)]
    [InlineData("https://drive.google.com/uc?id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345&export=download", RecordingLinkKind.None)]
    [InlineData("http://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view", RecordingLinkKind.None)]
    [InlineData("https://drive.google.com.evil.example/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view", RecordingLinkKind.None)]
    [InlineData("https://evil.example/?next=https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view", RecordingLinkKind.None)]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view extra", RecordingLinkKind.None)]
    [InlineData("C:\\Recordings\\AST5.mp4", RecordingLinkKind.None)]
    [InlineData("", RecordingLinkKind.None)]
    [InlineData(null, RecordingLinkKind.None)]
    public void EachLinkIsRecognisedForWhatItIs(string? link, RecordingLinkKind expected) =>
        Assert.Equal(expected, RecordingLinks.Classify(link));

    [Fact]
    public void ThePreviewIsEnoughToRecogniseNotToOpen()
    {
        Assert.Equal("drive.google.com/file/d/1AbCdE...",
            RecordingLinks.Preview("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing"));
    }
}

/// <summary>
/// The dashboard's own check, reached without a browser: with no sign-in saved, a link that passes
/// the check stops at "not signed in", and one that does not stops at "invalid link" - both before
/// anything is opened.
/// </summary>
public sealed class LmsRecordLinkAcceptanceTests
{
    private sealed class NoSignIn : ILmsCredentialStore
    {
        public string Profile => LmsAccountDirectory.LegacyProfile;
        public LmsAccount? Read() => null;
        public void Save(LmsAccount account) { }
        public void Delete() { }
    }

    [Theory]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing", LmsFailure.NotSignedIn)]
    [InlineData("https://zoom.us/rec/share/FAKE-TOKEN.NotReal?startTime=1788278291000", LmsFailure.NotSignedIn)]
    [InlineData("https://example.com/video.mp4", LmsFailure.InvalidLink)]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345", LmsFailure.InvalidLink)]
    public async Task DriveAndZoomLinksPassTheDashboardsCheckAndOthersDoNot(string link, LmsFailure expected)
    {
        var result = await new LmsSessionRunner(new NoSignIn()).AttachRecordLinkAsync("AST5_DAT1_S1", link);
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.FailureKind);
    }
}
