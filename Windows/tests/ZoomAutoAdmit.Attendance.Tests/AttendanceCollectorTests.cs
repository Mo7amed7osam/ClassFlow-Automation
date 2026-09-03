using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using ZoomAutoAdmit.Core.Meetings;
using Xunit;

namespace ZoomAutoAdmit.Attendance.Tests;

public class AttendanceCollectorTests
{
    private sealed class FakeSource : IAttendanceParticipantSource
    {
        public AttendanceSource SourceType = AttendanceSource.Desktop;
        public AttendanceSource Source => SourceType;
        public bool Fail;
        public Task<ParticipantReadResult> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException("panel unavailable");
            return Task.FromResult(new ParticipantReadResult([new("Same name"), new("Same name")], true));
        }
    }

    private sealed class Store : IAttendanceSnapshotStore
    {
        public ConcurrentQueue<AttendanceSnapshot> Snapshots = new();
        public ConcurrentQueue<AttendanceCaptureIssue> Issues = new();
        public bool Fail;
        public Task SaveAsync(AttendanceSnapshot snapshot, CancellationToken token)
        {
            if (Fail) throw new IOException("disk unavailable");
            Snapshots.Enqueue(snapshot);
            return Task.CompletedTask;
        }
        public Task SaveIssueAsync(AttendanceCaptureIssue issue, CancellationToken token)
        {
            Issues.Enqueue(issue);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CapturesEveryTriggerAndPreservesDuplicateNames()
    {
        var id = Guid.NewGuid();
        var store = new Store();
        var clock = new FakeTimeProvider();
        var logs = new ConcurrentQueue<string>();
        await using var collector = new AttendanceCollector(id, new FakeSource(), store, clock, log: logs.Enqueue);
        await collector.OnMeetingStateChangedAsync(id, MeetingState.Joining);
        Assert.Empty(store.Snapshots);
        await collector.OnMeetingStateChangedAsync(id, MeetingState.Active);
        await collector.OnMeetingStateChangedAsync(id, MeetingState.Monitoring);
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Single(store.Snapshots);
        clock.Advance(TimeSpan.FromMinutes(1));
        await EventuallyAsync(() => store.Snapshots.Count == 2);
        await collector.OnAdmissionAsync(id);
        await collector.CaptureManualAsync();
        await collector.OnMeetingStateChangedAsync(id, MeetingState.Ended);
        Assert.Equal(new[] { SnapshotTrigger.MeetingStart, SnapshotTrigger.Interval, SnapshotTrigger.Admission,
            SnapshotTrigger.Manual, SnapshotTrigger.MeetingEnd }, store.Snapshots.Select(s => s.Trigger));
        Assert.All(store.Snapshots, snapshot =>
        {
            Assert.Equal(id, snapshot.SessionId);
            Assert.Equal(2, snapshot.Participants.Count);
            Assert.Equal(snapshot.Participants[0], snapshot.Participants[1]);
        });
        Assert.Contains(logs, line => line.Contains("[ATTENDANCE] Collector started"));
        Assert.Contains(logs, line => line.Contains("[ATTENDANCE] Collector stopped"));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await collector.CaptureManualAsync());
        Assert.Equal(5, store.Snapshots.Count);
    }

    [Fact]
    public async Task ReadAndStorageFailuresDoNotSaveFalseEmptySnapshotsOrStopCollector()
    {
        var source = new FakeSource { Fail = true };
        var store = new Store();
        await using var collector = new AttendanceCollector(Guid.NewGuid(), source, store);
        await collector.StartAsync();
        Assert.Empty(store.Snapshots);
        source.Fail = false;
        store.Fail = true;
        Assert.Null(await collector.CaptureManualAsync());
        store.Fail = false;
        Assert.NotNull(await collector.CaptureManualAsync());
        Assert.Single(store.Snapshots);
        // Both failures are recorded as issues so the interface can show that a capture was attempted.
        Assert.Equal(new[] { SnapshotTrigger.MeetingStart, SnapshotTrigger.Manual }, store.Issues.Select(issue => issue.Trigger));
        Assert.Contains("panel unavailable", store.Issues.First().Reason);
        Assert.DoesNotContain(store.Snapshots, snapshot => snapshot.Participants.Count == 0);
    }

    [Fact]
    public async Task SessionsAreIsolatedAndForeignEventsRejected()
    {
        var store = new Store();
        await using var desktop = new AttendanceCollector(Guid.NewGuid(), new FakeSource(), store);
        await using var web = new AttendanceCollector(Guid.NewGuid(), new FakeSource { SourceType = AttendanceSource.Web }, store);
        await Task.WhenAll(desktop.StartAsync(), web.StartAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => desktop.OnAdmissionAsync(web.SessionId));
        Assert.Equal(2, store.Snapshots.Select(s => s.SessionId).Distinct().Count());
        Assert.Equal(2, store.Snapshots.Select(s => s.Source).Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentCapturesAndStopProduceOnlyOneEndSnapshot()
    {
        var store = new Store();
        await using var collector = new AttendanceCollector(Guid.NewGuid(), new FakeSource(), store);
        await collector.StartAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => collector.CaptureManualAsync()));
        await Task.WhenAll(collector.StopAsync(), collector.StopAsync());
        Assert.Equal(22, store.Snapshots.Count);
        Assert.Single(store.Snapshots.Where(s => s.Trigger == SnapshotTrigger.MeetingEnd));
    }

    [Fact]
    public async Task CancelledRequestDoesNotCapture()
    {
        var store = new Store();
        await using var collector = new AttendanceCollector(Guid.NewGuid(), new FakeSource(), store);
        await collector.StartAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            collector.CaptureManualAsync(new CancellationToken(true)));
        Assert.Single(store.Snapshots);
    }

    [Fact]
    public async Task DisposeBeforeActiveDoesNotReadOrCaptureEnd()
    {
        var store = new Store();
        var collector = new AttendanceCollector(Guid.NewGuid(), new FakeSource(), store);
        await collector.DisposeAsync();
        await collector.StartAsync();
        Assert.Empty(store.Snapshots);
    }

    [Fact]
    public async Task LogSinkFailureCannotBreakCollection()
    {
        var store = new Store();
        await using var collector = new AttendanceCollector(Guid.NewGuid(), new FakeSource(), store,
            log: _ => throw new Exception("bad sink"));
        await collector.StartAsync();
        Assert.Single(store.Snapshots);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
