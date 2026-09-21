using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Tests;

/// <summary>
/// Who is in the meeting, collected while class.run holds it. What needs a live Zoom meeting - the
/// list on the page - is behind a function here; what is tested is what the worker does with it.
/// </summary>
public sealed class MeetingAttendanceTests
{
    private static readonly ClassStage Stage = ClassStageOf("""
        {"classPlanId":"11111111-2222-3333-4444-555555555555","group":"CAI5_AIS4_S7","date":"2026-09-20",
         "startTime":"19:00","coordinatorId":"66666666-7777-8888-9999-000000000000"}
        """);

    private static ClassStage ClassStageOf(string json)
    {
        Assert.True(ClassStage.TryParse(JsonDocument.Parse(json).RootElement, out var stage, out var error), error);
        return stage!;
    }

    private sealed class Sink : IAttendanceSnapshots
    {
        public readonly List<(string Trigger, int Names, bool Ended, bool Complete)> Sent = [];
        public bool Fail;

        public Task SendAsync(ClassStage stage, IReadOnlyList<string> names, string trigger, bool complete, bool ended,
                              DateTimeOffset capturedAt, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("the server is not there");
            Sent.Add((trigger, names.Count, ended, complete));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task It_reads_at_the_start_on_an_interval_and_once_more_at_the_end()
    {
        var sink = new Sink();
        int reads = 0;
        var attendance = new MeetingAttendance(
            _ => Task.FromResult(new MeetingRead([.. Enumerable.Range(0, ++reads).Select(i => $"Person {i}")], true)),
            sink, Stage, first: TimeSpan.FromMilliseconds(1), every: TimeSpan.FromMilliseconds(20));

        using var classOver = new CancellationTokenSource();
        var running = attendance.RunAsync(classOver.Token);
        while (sink.Sent.Count < 3) await Task.Delay(5);
        await classOver.CancelAsync();
        await running;                                  // stopping is not an error
        await attendance.FinalAsync(default);

        Assert.Equal("meetingStart", sink.Sent[0].Trigger);
        Assert.Equal("interval", sink.Sent[1].Trigger);
        Assert.Equal(("meetingEnd", true), (sink.Sent[^1].Trigger, sink.Sent[^1].Ended));
        // Only the last read tells the server the meeting is over.
        Assert.All(sink.Sent.SkipLast(1), s => Assert.False(s.Ended));
        Assert.Equal(sink.Sent.Count, attendance.Sent);
        Assert.Equal(reads, attendance.MostSeen);
    }

    [Fact]
    public async Task A_read_that_fails_is_skipped_and_never_ends_the_class()
    {
        // Attendance must not be able to close the meeting it reads: a class held with one read
        // missing is far better than a class that is closed.
        var sink = new Sink();
        var log = new List<string>();
        var attendance = new MeetingAttendance(
            _ => throw new InvalidOperationException("no participants list on the page"),
            sink, Stage, log.Add, first: TimeSpan.FromMilliseconds(1), every: TimeSpan.FromMilliseconds(5));

        using var classOver = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await attendance.RunAsync(classOver.Token);
        await attendance.FinalAsync(default);

        Assert.Empty(sink.Sent);
        Assert.Contains(log, line => line.Contains("no participants list"));
    }

    [Fact]
    public async Task A_server_that_is_away_for_a_moment_costs_one_read_not_the_class()
    {
        var sink = new Sink { Fail = true };
        var attendance = new MeetingAttendance(_ => Task.FromResult(new MeetingRead(["Mona"], true)), sink, Stage);

        await attendance.FinalAsync(default);          // does not throw

        Assert.Equal(0, attendance.Sent);
        Assert.Equal(1, attendance.MostSeen);
    }
}

/// <summary>The two stages that write attendance, before any browser is opened.</summary>
public sealed class AttendanceWritingTests
{
    private static readonly Func<DateTimeOffset> DuringTheClass =
        () => new DateTimeOffset(2026, 9, 20, 19, 5, 0, TimeSpan.FromHours(3));

    private static JsonElement Payload() => JsonDocument.Parse("""
        {"classPlanId":"11111111-2222-3333-4444-555555555555","group":"CAI5_AIS4_S7","date":"2026-09-20",
         "startTime":"19:00","coordinatorId":"66666666-7777-8888-9999-000000000000",
         "lmsAccountId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}
        """).RootElement;

    private sealed class Accounts : ILmsAccounts
    {
        public Task<ILmsCredentialStore?> ForAsync(Guid lmsAccountId, CancellationToken cancellationToken) =>
            Task.FromResult<ILmsCredentialStore?>(new LmsCredentialStore("t"));
    }

    private sealed class Unavailable(string reason) : IAttendanceNames
    {
        public Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken) =>
            throw new AttendanceUnavailableException(reason);
    }

    [Theory]
    [InlineData("lms.attendance")]
    [InlineData("lms.late_joiners")]
    public async Task The_servers_reason_for_having_nothing_reaches_the_class_card(string jobType)
    {
        var store = new InMemoryLmsCredentialBackend();
        store.Save("t", new LmsAccount("omar@lms.example.com", "pw"));
        LmsCredentialBackend.Current = store;
        try
        {
            var names = new Unavailable("CAI5_AIS4_S7 has no students on the server.");
            ClassStageHandler stage = jobType == "lms.attendance"
                ? new AttendanceStage(new Accounts(), names, now: DuringTheClass)
                : new LateJoinersStage(new Accounts(), names, now: DuringTheClass);

            var outcome = await stage.ExecuteAsync(Payload(), default);

            Assert.Equal(jobType, stage.JobType);
            Assert.Equal("noAttendanceCollected", outcome.Error!.Code);
            Assert.Equal("CAI5_AIS4_S7 has no students on the server.", outcome.Error.Message);
            Assert.False(outcome.Error.Retryable);
        }
        finally { LmsCredentialBackend.Reset(); }
    }
}
