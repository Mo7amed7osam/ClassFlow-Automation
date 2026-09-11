using System.Collections.Concurrent;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WebAutomation.Recordings;
using ZoomAutoAdmit.WebAutomation.Zoom;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// The one workflow shared by the terminal and the API, with Zoom and the dashboard faked. The
/// profile lock is the real one, on files in a temporary folder.
/// </summary>
public sealed class RecordingLinkProcessorTests : IDisposable
{
    private const string ShareLink = "https://zoom.us/rec/share/abc.def?startTime=1788278291000";
    private static readonly DateOnly Day = new(2026, 9, 1);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zaa-recordings-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ProfileOperationLock Lock() => new(Path.Combine(_root, "locks"), Path.Combine(_root, "profiles"));

    private sealed class FakeZoom : IRecordingLinkSource
    {
        public readonly ConcurrentQueue<(string Group, string Profile, DateOnly Day, TimeOnly? Time)> Calls = new();
        public Func<Task<ZoomRecordingLinkResult>> Answer = () => Task.FromResult(
            ZoomRecordingLinkResult.Success(ShareLink, "copied") with
            {
                Recording = new ZoomRecordingEntry("CAI5_AIS4_S7", "https://zoom.us/recording/detail?meeting_id=1") { Duration = TimeSpan.FromHours(3) },
                StartedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1788278291000),
            });

        public Task<ZoomRecordingLinkResult> ReadAsync(string group, string profile, DateOnly day, TimeOnly? startTime,
            bool headed, CancellationToken cancellationToken)
        {
            Calls.Enqueue((group, profile, day, startTime));
            return Answer();
        }
    }

    private sealed class FakeDashboard : IRecordingLinkTarget
    {
        public readonly ConcurrentQueue<(string Link, bool DryRun, bool Replace, bool KeepOpen)> Calls = new();
        public Func<bool, bool, LmsRunResult> Answer = (dryRun, _) => dryRun
            ? LmsRunResult.Success("box open, nothing saved")
            : LmsRunResult.Success("the recording link was saved on the session.");

        public Task<LmsRunResult> AttachAsync(string group, string recordLink, TimeOnly? startTime, DateOnly day, bool headed,
            bool dryRun, bool keepBrowserOpen, bool replaceExisting, CancellationToken cancellationToken)
        {
            Calls.Enqueue((recordLink, dryRun, replaceExisting, keepBrowserOpen));
            return Task.FromResult(Answer(dryRun, replaceExisting));
        }
    }

    private RecordingLinkProcessor Processor(FakeZoom zoom, FakeDashboard dashboard,
        Func<string, CancellationToken, Task<string?>>? accountProfile = null, TimeSpan? wait = null) =>
        new(zoom, dashboard, Lock(), accountProfile, _log.Add, wait ?? TimeSpan.FromSeconds(10), () => Day);

    private static RecordingLinkRequest Request(bool dryRun = false, bool replace = false, string? profile = null) =>
        new() { Group = "CAI5_AIS4_S7", Date = Day, StartTime = new TimeOnly(18, 58), DryRun = dryRun, ReplaceExisting = replace, Profile = profile };

    [Fact]
    public async Task AFoundRecordingIsAttachedWithTheLinkZoomGave()
    {
        var zoom = new FakeZoom();
        var dashboard = new FakeDashboard();
        var outcome = await Processor(zoom, dashboard).ProcessAsync(Request(), default);

        Assert.Equal(RecordingLinkStatus.Attached, outcome.Status);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(ShareLink, Assert.Single(dashboard.Calls).Link);
        Assert.Equal(TimeSpan.FromHours(3), outcome.RecordingDuration);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 15, 58, 11, TimeSpan.Zero), outcome.RecordingStartedAtUtc);
        // The log carries a preview, never the whole link that opens the recording.
        Assert.DoesNotContain(ShareLink, string.Join("\n", _log));
        Assert.Contains(RecordingLinkProcessor.Preview(ShareLink), string.Join("\n", _log));
    }

    [Fact]
    public async Task ADuplicateRequestLeavesTheExistingLinkAlone()
    {
        var dashboard = new FakeDashboard
        {
            Answer = (_, replace) => replace
                ? LmsRunResult.Success("the recording link was saved on the session.")
                : LmsRunResult.Success("the session already has a recording link, so it was left as it is.") with { AlreadyExists = true },
        };
        var processor = Processor(new FakeZoom(), dashboard);

        var repeat = await processor.ProcessAsync(Request(replace: false), default);
        Assert.Equal(RecordingLinkStatus.AlreadyExists, repeat.Status);
        Assert.True(repeat.IsSuccess);

        var replaced = await processor.ProcessAsync(Request(replace: true), default);
        Assert.Equal(RecordingLinkStatus.Attached, replaced.Status);
        Assert.Equal([false, true], dashboard.Calls.Select(call => call.Replace));
    }

    [Fact]
    public async Task ADryRunIsReportedAsOneAndPassedOn()
    {
        var dashboard = new FakeDashboard();
        var outcome = await Processor(new FakeZoom(), dashboard).ProcessAsync(Request(dryRun: true), default);
        Assert.Equal(RecordingLinkStatus.DryRun, outcome.Status);
        Assert.True(Assert.Single(dashboard.Calls).DryRun);
    }

    [Theory]
    [InlineData(ZoomRecordingFailure.NotFound, RecordingLinkStatus.RecordingNotFound, "notFound")]
    [InlineData(ZoomRecordingFailure.TimeMismatch, RecordingLinkStatus.RecordingNotFound, "timeMismatch")]
    [InlineData(ZoomRecordingFailure.NotSignedIn, RecordingLinkStatus.ZoomFailed, "zoomNotSignedIn")]
    [InlineData(ZoomRecordingFailure.Failed, RecordingLinkStatus.ZoomFailed, "zoomFailed")]
    public async Task AZoomProblemStopsBeforeTheDashboard(ZoomRecordingFailure failure, RecordingLinkStatus status, string reason)
    {
        var zoom = new FakeZoom { Answer = () => Task.FromResult(ZoomRecordingLinkResult.Fail(failure, "no")) };
        var dashboard = new FakeDashboard();
        var outcome = await Processor(zoom, dashboard).ProcessAsync(Request(), default);

        Assert.Equal(status, outcome.Status);
        Assert.Equal(reason, outcome.Reason);
        Assert.Empty(dashboard.Calls);
    }

    [Theory]
    [InlineData(LmsFailure.SessionNotFinished, "sessionNotFinished")]
    [InlineData(LmsFailure.SessionNotFound, "sessionNotFound")]
    [InlineData(LmsFailure.NotSignedIn, "lmsNotSignedIn")]
    [InlineData(LmsFailure.Failed, "lmsFailed")]
    public async Task ADashboardProblemCarriesItsReason(LmsFailure failure, string reason)
    {
        var dashboard = new FakeDashboard { Answer = (_, _) => LmsRunResult.Fail(failure, "no") };
        var outcome = await Processor(new FakeZoom(), dashboard).ProcessAsync(Request(), default);
        Assert.Equal(RecordingLinkStatus.LmsFailed, outcome.Status);
        Assert.Equal(reason, outcome.Reason);
    }

    [Fact]
    public async Task AnExceptionFromABrowserBecomesAnAnswerNotACrash()
    {
        var zoom = new FakeZoom { Answer = () => throw new InvalidOperationException("Chromium said something with a path in it") };
        var outcome = await Processor(zoom, new FakeDashboard()).ProcessAsync(Request(), default);
        Assert.Equal(RecordingLinkStatus.ZoomFailed, outcome.Status);
        Assert.DoesNotContain("path in it", outcome.Message + string.Join("\n", _log));
    }

    // ------------------------------------------------------------------------------ profiles

    [Fact]
    public async Task DefaultMeansTheAccountsOwnProfile()
    {
        var zoom = new FakeZoom();
        var processor = Processor(zoom, new FakeDashboard(),
            accountProfile: (group, _) => Task.FromResult<string?>(group == "CAI5_AIS4_S7" ? "s7" : null));

        await processor.ProcessAsync(Request(profile: null), default);
        await processor.ProcessAsync(Request(profile: "default"), default);
        await processor.ProcessAsync(Request(profile: "Default"), default);
        await processor.ProcessAsync(Request(profile: "depi21"), default);

        Assert.Equal(["s7", "s7", "s7", "depi21"], zoom.Calls.Select(call => call.Profile));
    }

    [Fact]
    public async Task AnAccountWithNoProfileFallsBackToTheGroupName()
    {
        var zoom = new FakeZoom();
        await Processor(zoom, new FakeDashboard(), accountProfile: (_, _) => Task.FromResult<string?>(null))
            .ProcessAsync(Request(), default);
        Assert.Equal("CAI5_AIS4_S7", Assert.Single(zoom.Calls).Profile);
    }

    [Fact]
    public async Task NoDateMeansToday()
    {
        var zoom = new FakeZoom();
        var outcome = await Processor(zoom, new FakeDashboard())
            .ProcessAsync(new RecordingLinkRequest { Group = "CAI5_AIS4_S7" }, default);
        Assert.Equal(Day, Assert.Single(zoom.Calls).Day);
        Assert.Null(zoom.Calls.Single().Time);
        Assert.Equal(Day, outcome.Date);
    }

    // ------------------------------------------------------------------------------ concurrency

    [Fact]
    public async Task TwoRequestsNeverDriveTheSameProfileAtOnce()
    {
        int inside = 0, overlaps = 0;
        var zoom = new FakeZoom
        {
            Answer = async () =>
            {
                if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
                await Task.Delay(300);
                Interlocked.Decrement(ref inside);
                return ZoomRecordingLinkResult.Success(ShareLink, "copied");
            },
        };
        var processor = Processor(zoom, new FakeDashboard());

        var results = await Task.WhenAll(
            processor.ProcessAsync(Request(), default),
            processor.ProcessAsync(Request(), default),
            processor.ProcessAsync(Request(), default));

        Assert.Equal(0, overlaps);
        Assert.All(results, result => Assert.Equal(RecordingLinkStatus.Attached, result.Status));
        Assert.Equal(3, zoom.Calls.Count);
    }

    [Fact]
    public async Task AProfileThatStaysBusyIsAnsweredWithBusyNotAQueueForever()
    {
        var gate = new TaskCompletionSource();
        var zoom = new FakeZoom
        {
            Answer = async () => { await gate.Task; return ZoomRecordingLinkResult.Success(ShareLink, "copied"); },
        };
        var processor = Processor(zoom, new FakeDashboard(), wait: TimeSpan.FromMilliseconds(600));

        var first = processor.ProcessAsync(Request(), default);
        await Task.Delay(200);                                   // the first now holds the profile
        var second = await processor.ProcessAsync(Request(), default);
        Assert.Equal(RecordingLinkStatus.Busy, second.Status);

        gate.SetResult();
        Assert.Equal(RecordingLinkStatus.Attached, (await first).Status);
    }
}

public sealed class ProfileOperationLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zaa-lock-" + Guid.NewGuid().ToString("N"));
    private string Locks => Path.Combine(_root, "locks");
    private string Profiles => Path.Combine(_root, "profiles");

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task OneHolderAtATimeEvenAcrossSeparateLockObjects()
    {
        // Two objects stand in for two processes: the lock is the file, not the object.
        var first = new ProfileOperationLock(Locks, Profiles);
        var second = new ProfileOperationLock(Locks, Profiles);

        var held = await first.TryAcquireAsync("s7", TimeSpan.Zero, default);
        Assert.NotNull(held);
        Assert.Null(await second.TryAcquireAsync("S7", TimeSpan.FromMilliseconds(300), default));   // same profile, any case

        await held!.DisposeAsync();
        var next = await second.TryAcquireAsync("s7", TimeSpan.FromSeconds(2), default);
        Assert.NotNull(next);
        await next!.DisposeAsync();
    }

    [Fact]
    public async Task DifferentProfilesDoNotWaitForEachOther()
    {
        var locks = new ProfileOperationLock(Locks, Profiles);
        var s7 = await locks.TryAcquireAsync("s7", TimeSpan.Zero, default);
        var dashboard = await locks.TryAcquireAsync("lms-dashboard", TimeSpan.Zero, default);
        Assert.NotNull(s7);
        Assert.NotNull(dashboard);
        await s7!.DisposeAsync();
        await dashboard!.DisposeAsync();
    }

    [Fact]
    public async Task AProfileABrowserHasOpenIsBusyAndAStaleLockfileIsNot()
    {
        string profile = Path.Combine(Profiles, "s7");
        Directory.CreateDirectory(profile);
        string chromium = Path.Combine(profile, "lockfile");
        var locks = new ProfileOperationLock(Locks, Profiles);

        // Chromium holds this file exclusively for as long as the profile is open.
        await using (new FileStream(chromium, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(locks.IsHeldByBrowser("s7"));
            Assert.Null(await locks.TryAcquireAsync("s7", TimeSpan.FromMilliseconds(300), default));
        }

        // Left behind by a browser that has gone: not a reason to refuse.
        Assert.True(File.Exists(chromium));
        Assert.False(locks.IsHeldByBrowser("s7"));
        var held = await locks.TryAcquireAsync("s7", TimeSpan.Zero, default);
        Assert.NotNull(held);
        await held!.DisposeAsync();
    }

    [Fact]
    public async Task NothingIsEverWrittenInsideAProfileFolder()
    {
        // An empty profile folder is how the app knows to seed it from a signed-in one.
        string profile = Path.Combine(Profiles, "CAI5_AIS4_S7");
        Directory.CreateDirectory(profile);
        var held = await new ProfileOperationLock(Locks, Profiles).TryAcquireAsync("CAI5_AIS4_S7", TimeSpan.Zero, default);
        Assert.Empty(Directory.GetFileSystemEntries(profile));
        await held!.DisposeAsync();
    }
}
