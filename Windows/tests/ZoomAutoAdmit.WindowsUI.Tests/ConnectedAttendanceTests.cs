using ZoomAutoAdmit.Attendance;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class ConnectedAttendanceTests
{
    [Fact]
    public async Task BadSnapshotsAreReportedWithoutHidingHealthySnapshots()
    {
        var path = Path.Combine(Path.GetTempPath(), "attendance-ui-" + Guid.NewGuid());
        try
        {
            var store = new JsonAttendanceSnapshotStore(path);
            await store.SaveAsync(Snapshot(Guid.NewGuid(), "good", "Known Name"), default);
            await store.SaveAsync(Snapshot(Guid.NewGuid(), "bad") with { Participants = null! }, default);
            var history = await new AttendanceHistoryReader(path).ReadAsync();
            Assert.Single(history.Snapshots); Assert.Equal(1, history.Unreadable);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Fact]
    public async Task ReadsActualCollectorFilesPreservingSessionMetadataAndDuplicateNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "attendance-ui-" + Guid.NewGuid());
        try
        {
            var store = new JsonAttendanceSnapshotStore(path);
            var a = Snapshot(Guid.NewGuid(), "A", "Same Name", "Same Name");
            var b = Snapshot(Guid.NewGuid(), "B", "Other Name");
            await store.SaveAsync(a, default); await store.SaveAsync(b, default);
            var reader = new AttendanceHistoryReader(path);
            var history = await reader.ReadAsync();
            Assert.Equal(2, history.Snapshots.Count);
            var savedA = history.Snapshots.Single(s => s.Snapshot.SessionId == a.SessionId);
            Assert.Equal("A", savedA.Snapshot.Meeting!.AccountId);
            Assert.Equal(2, savedA.Snapshot.Participants.Count);
            Assert.Equal(a.Participants, savedA.Snapshot.Participants);
            Assert.Equal(0, history.Unreadable);
            Assert.Same(savedA, (await reader.ReadAsync()).Snapshots.Single(s => s.Id == savedA.Id));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task MatchingUsesSelectedSnapshotOnlyAndRefreshKeepsResultsAndSelection()
    {
        var a = new SnapshotDisplay("a", Snapshot(Guid.NewGuid(), "A", "First Student"));
        var b = new SnapshotDisplay("b", Snapshot(Guid.NewGuid(), "B", "Other Student"));
        var reader = new Reader { Items = [a, b] };
        var service = new RecordingAi();
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), service);
        using var vm = new AttendanceViewModel(reader, ai) { MergeWholeSession = false };
        await ai.TestAndSaveAsync("synthetic-key"); ai.AllowExternalMatching = true;
        ai.SelectedGroup = new("A", "Group A", DateTimeOffset.Now, [new("s", "A", 1, "First Student", [])]);
        await vm.RefreshAsync(); vm.SelectedSnapshot = a;
        await vm.MatchSelectedAsync();
        Assert.Equal(new[] { "First Student" }, service.Names);
        Assert.Single(vm.Results);
        reader.Items = [new("new-b", Snapshot(b.Snapshot.SessionId, "B", "New Arrival")), a, b];
        await vm.RefreshAsync();
        Assert.Same(a, vm.SelectedSnapshot); Assert.Single(vm.Results);
        vm.SelectedSnapshot = b;
        Assert.Empty(vm.Results); Assert.Equal("Other Student", Assert.Single(vm.Participants).Name);
        ai.SelectedGroup = new("B", "Group B", DateTimeOffset.Now, []);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task WholeSessionMergeCoversLateJoinersAndKeepsRepeatedNames()
    {
        var session = Guid.NewGuid();
        var early = new SnapshotDisplay("early", Snapshot(session, "A", "First Student", "Same Name", "Same Name"));
        var later = new SnapshotDisplay("later", Snapshot(session, "A", "First Student", "Late Joiner"));
        var other = new SnapshotDisplay("other", Snapshot(Guid.NewGuid(), "B", "Someone Else"));
        var reader = new Reader { Items = [later, early, other] };
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
        using var vm = new AttendanceViewModel(reader, ai);
        await vm.RefreshAsync();
        vm.SelectedSnapshot = early;

        Assert.Equal(new[] { "First Student", "Late Joiner", "Same Name", "Same Name" },
            vm.Participants.Select(participant => participant.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Contains("2 snapshots merged", vm.SnapshotDetails);
        // The other meeting's names never leak into this session.
        Assert.DoesNotContain("Someone Else", vm.Participants.Select(participant => participant.Name));

        vm.MergeWholeSession = false;
        Assert.Equal(3, vm.Participants.Count);
        Assert.DoesNotContain("Late Joiner", vm.Participants.Select(participant => participant.Name));
    }

    [Fact]
    public async Task FailedCapturesAreVisibleAndAddNoNames()
    {
        var path = Path.Combine(Path.GetTempPath(), "attendance-ui-" + Guid.NewGuid());
        try
        {
            var store = new JsonAttendanceSnapshotStore(path);
            var session = Guid.NewGuid();
            await store.SaveAsync(Snapshot(session, "A", "First Student"), default);
            await store.SaveIssueAsync(new(session, DateTimeOffset.Now, SnapshotTrigger.Interval,
                "InvalidOperationException: Attendance requires one exposed Joined list."), default);

            var history = await new AttendanceHistoryReader(path).ReadAsync();
            Assert.Single(history.Snapshots);
            Assert.Equal(0, history.Unreadable);   // A recorded failure is not a corrupt snapshot.
            Assert.Equal(session, Assert.Single(history.CaptureIssues).SessionId);

            using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
            using var vm = new AttendanceViewModel(new AttendanceHistoryReader(path), ai);
            await vm.RefreshAsync();
            Assert.True(vm.HasCaptureIssues);
            Assert.Contains("capture attempt", vm.CaptureWarning);
            Assert.Contains("Joined list", vm.CaptureWarning);
            Assert.Single(vm.Participants);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task DeletingASessionRemovesItsReadingsFromDiskAndLeavesTheOthersAlone()
    {
        var path = Path.Combine(Path.GetTempPath(), "attendance-ui-" + Guid.NewGuid());
        try
        {
            var store = new JsonAttendanceSnapshotStore(path);
            var doomed = Guid.NewGuid();
            var kept = Guid.NewGuid();
            await store.SaveAsync(Snapshot(doomed, "A", "First Student"), default);
            await store.SaveAsync(Snapshot(doomed, "A", "First Student", "Second Student"), default);
            await store.SaveAsync(Snapshot(kept, "B", "Other Student"), default);
            var reader = new AttendanceHistoryReader(path);
            Assert.Equal(3, (await reader.ReadAsync()).Snapshots.Count);

            Assert.Equal(1, await reader.DeleteSessionsAsync([doomed]));

            // The whole session goes: no orphaned readings, and nothing else is touched.
            Assert.False(Directory.Exists(Path.Combine(path, doomed.ToString())));
            Assert.True(Directory.Exists(Path.Combine(path, kept.ToString())));
            var left = await reader.ReadAsync();
            Assert.Equal(kept, Assert.Single(left.Snapshots).Snapshot.SessionId);
            Assert.Equal(0, left.Unreadable);
            // A session that is already gone is not an error, and deletes nothing else.
            Assert.Equal(0, await reader.DeleteSessionsAsync([doomed, Guid.Empty]));
            Assert.True(Directory.Exists(Path.Combine(path, kept.ToString())));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task EmptyOrUnavailableHistoryDoesNotInventAttendance()
    {
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
        using var vm = new AttendanceViewModel(new Reader(), ai);
        await vm.RefreshAsync();
        Assert.Empty(vm.Results); Assert.Empty(vm.Participants);
        Assert.Empty(vm.Sessions); Assert.Null(vm.SelectedSession);
        Assert.Equal("No session yet", vm.SessionTitle);
        Assert.Contains("No session has been recorded yet", vm.Status);
        await vm.MatchSelectedAsync(); Assert.Contains("nothing was matched", vm.Status);
    }

    [Fact]
    public async Task ARunSessionPicksItsOwnGroupAndIsMatchedWithoutBeingAsked()
    {
        var session = Guid.NewGuid();
        var reader = new Reader
        {
            Items =
            [
                new SnapshotDisplay("a", Snapshot(session, "CAI5_AIS4_S7", "First Student")),
                new SnapshotDisplay("b", Snapshot(session, "CAI5_AIS4_S7", "First Student", "Second Student")),
            ]
        };
        var group = new RosterGroup("CAI5_AIS4_S7", "CAI5_AIS4_S7", DateTimeOffset.Now,
            [new("s1", "CAI5_AIS4_S7", 1, "First Student", []), new("s2", "CAI5_AIS4_S7", 2, "Second Student", [])]);
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
        using var vm = new AttendanceViewModel(reader, ai, rosterGroups: () => [group]);

        await vm.RefreshAsync();

        // One session, not two readings of it, and its group was never chosen by hand.
        var only = Assert.Single(vm.Sessions);
        Assert.Same(only, vm.SelectedSession);
        Assert.Contains("CAI5_AIS4_S7", vm.SessionTitle);
        Assert.Equal(group, ai.SelectedGroup);
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal(2, vm.PresentCount);
        Assert.Equal(0, vm.NotSeenCount);
    }

    [Fact]
    public async Task ASessionWhoseGroupIsNotOnTheRosterSaysSoAndMatchesNobody()
    {
        var reader = new Reader { Items = [new SnapshotDisplay("a", Snapshot(Guid.NewGuid(), "CAI9_UNKNOWN", "First Student"))] };
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
        using var vm = new AttendanceViewModel(reader, ai, rosterGroups: () => []);

        await vm.RefreshAsync();

        Assert.Single(vm.Sessions);
        Assert.Null(ai.SelectedGroup);
        Assert.Empty(vm.Results);
        Assert.Contains("CAI9_UNKNOWN", vm.Status);
    }

    [Fact]
    public async Task ExistingLifecycleFeedKeepsDesktopAndWebAdmissionsIndependentAndStopsObserving()
    {
        var events = new MeetingLifecycleEvents();
        using var feed = new MeetingActivityFeed(events);
        var a = Context("A", SessionEngineType.Desktop); var b = Context("B", SessionEngineType.Web);
        await events.PublishAsync(a, MeetingLifecycleEventKind.Active);
        await events.PublishAsync(b, MeetingLifecycleEventKind.Active);
        events.PublishAdmission(a.Session.SessionId); events.PublishAdmission(b.Session.SessionId);
        events.PublishAdmission(Guid.NewGuid());
        var vm = new WaitingRoomViewModel(feed); vm.Refresh();
        Assert.Equal(2, vm.VerifiedAdmissions);
        Assert.Single(vm.Activity.Where(e => e.AccountId == "A" && e.Event == "Admission verified"));
        Assert.Single(vm.Activity.Where(e => e.AccountId == "B" && e.Engine == "Web" && e.Event == "Admission verified"));
        await events.PublishAsync(a, MeetingLifecycleEventKind.Ending);
        events.PublishAdmission(a.Session.SessionId);
        Assert.Equal(2, feed.GetMeetingActivity().Count(e => e.Event == "Admission verified"));
        feed.Dispose(); events.PublishAdmission(b.Session.SessionId);
        Assert.Equal(2, feed.GetMeetingActivity().Count(e => e.Event == "Admission verified"));
    }

    private static AttendanceSnapshot Snapshot(Guid session, string account, params string[] names) =>
        new(session, DateTimeOffset.Now, AttendanceSource.Web, names.Select(n => new ParticipantPresence(n)).ToArray(), SnapshotTrigger.Admission, false, null)
        { Meeting = new(account, "Group " + account, "https://zoom.us/j/12345678901", DateTimeOffset.Now, "Web") };
    private static MeetingLaunchContext Context(string account, SessionEngineType engine)
    {
        var scheduled = new ScheduledMeeting(new("https://zoom.us/j/12345678901"), account, DateTimeOffset.Now);
        return new(new(Guid.NewGuid(), scheduled, DateTimeOffset.Now), new(account, account, "reference"), engine, account);
    }
    [Fact]
    public async Task CaptureWorksBeforeTheFirstSnapshotExists()
    {
        var live = Guid.NewGuid();
        var actions = new Actions { Active = [live] };
        using var ai = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiSetupTests.FakeAi());
        using var vm = new AttendanceViewModel(new Reader(), ai, actions);
        await vm.RefreshAsync();
        Assert.Null(vm.SelectedSnapshot);

        await vm.CaptureAsync();

        Assert.Equal(new[] { live }, actions.Captured);
        Assert.Contains("Capture requested", vm.Status);

        actions.Active = [];
        actions.Captured.Clear();
        await vm.CaptureAsync();
        Assert.Empty(actions.Captured);
        Assert.Contains("No meeting is running", vm.Status);
    }

    private sealed class Actions : IAttendanceUiActions
    {
        public IReadOnlyList<Guid> Active = [];
        public List<Guid> Captured { get; } = [];
        public Task<UiOperationResult> CaptureAttendanceAsync(Guid sessionId, CancellationToken token = default)
        {
            Captured.Add(sessionId);
            return Task.FromResult(new UiOperationResult(true, "Capture requested."));
        }
        public Task<IReadOnlyList<Guid>> GetActiveSessionIdsAsync(CancellationToken token = default) => Task.FromResult(Active);
    }

    private sealed class Reader : IAttendanceHistoryReader
    {
        public IReadOnlyList<SnapshotDisplay> Items = [];
        public Task<AttendanceHistory> ReadAsync(CancellationToken token = default) => Task.FromResult(new AttendanceHistory(Items, 0, false));
    }
    private sealed class RecordingAi : IAiMatchingService
    {
        public IReadOnlyList<string>? Names;
        public Task TestAsync(AiConnectionSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<ZoomAutoAdmit.AttendanceMatching.AttendanceMatchResult> MatchAsync(AiConnectionSettings settings, RosterGroup group, IReadOnlyList<string> names, CancellationToken token)
        { Names = names; return new AiSetupTests.FakeAi().MatchAsync(settings, group, names, token); }
        public Task<ZoomAutoAdmit.AttendanceMatching.AttendanceMatchResult> MatchWithRulesOnlyAsync(RosterGroup group, IReadOnlyList<string> names, CancellationToken token)
        { Names = names; return new AiSetupTests.FakeAi().MatchWithRulesOnlyAsync(group, names, token); }
    }
}
