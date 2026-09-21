using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.WebAutomation.Lms;

namespace ZoomAutoAdmit.CloudWorker.Tests;

/// <summary>
/// The rules an LMS stage keeps before it touches the LMS at all. These are the ones that decide
/// whether a class goes up under the right name and whether a bad run can be told from a good one,
/// so they are tested without a browser: what happens here happens before one is opened.
/// </summary>
public sealed class LmsStageTests
{
    private const string Plan = "11111111-2222-3333-4444-555555555555";
    private const string Coordinator = "66666666-7777-8888-9999-000000000000";
    private const string Account = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    /// <summary>The class's own hour, so these tests answer the same whatever day they are run.</summary>
    private static readonly Func<DateTimeOffset> DuringTheClass =
        () => new DateTimeOffset(2026, 9, 20, 19, 5, 0, TimeSpan.FromHours(3));

    private sealed class Accounts(ILmsCredentialStore? answer = null) : ILmsAccounts
    {
        public readonly List<Guid> Asked = [];

        public Task<ILmsCredentialStore?> ForAsync(Guid lmsAccountId, CancellationToken cancellationToken)
        {
            Asked.Add(lmsAccountId);
            return Task.FromResult(answer);
        }
    }

    private sealed class Names(params string[] present) : IAttendanceNames
    {
        public Task<IReadOnlyCollection<string>> PresentAsync(ClassStage stage, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>>(present);
    }

    private static JsonElement Payload(string? lmsAccountId = Account) =>
        JsonDocument.Parse($$"""
            {
              "classPlanId": "{{Plan}}",
              "group": "CAI5_AIS4_S7",
              "date": "2026-09-20",
              "startTime": "19:00",
              "coordinatorId": "{{Coordinator}}"
              {{(lmsAccountId is null ? "" : $", \"lmsAccountId\": \"{lmsAccountId}\"")}}
            }
            """).RootElement;

    [Fact]
    public async Task A_stage_with_no_account_named_never_reaches_the_LMS()
    {
        var accounts = new Accounts();
        var outcome = await new RunSessionStage(accounts, now: DuringTheClass).ExecuteAsync(Payload(lmsAccountId: null), default);

        Assert.False(outcome.Succeeded);
        Assert.Equal("invalidPayload", outcome.Error!.Code);
        Assert.Contains("lmsAccountId", outcome.Error.Message);
        Assert.False(outcome.Error.Retryable);
        // The important half: it did not ask for somebody's sign-in and then decide.
        Assert.Empty(accounts.Asked);
    }

    [Fact]
    public async Task The_account_asked_for_is_the_one_the_class_names()
    {
        var accounts = new Accounts();
        await new CompleteSessionStage(accounts, now: DuringTheClass).ExecuteAsync(Payload(), default);
        Assert.Equal([Guid.Parse(Account)], accounts.Asked);
    }

    [Fact]
    public async Task A_sign_in_the_server_will_not_give_stops_the_stage_there()
    {
        var outcome = await new RunSessionStage(new Accounts(answer: null), now: DuringTheClass).ExecuteAsync(Payload(), default);

        Assert.False(outcome.Succeeded);
        Assert.Equal("noLmsSignIn", outcome.Error!.Code);
        // Not retryable: a coordinator who is turned off will still be turned off in a minute.
        Assert.False(outcome.Error.Retryable);
    }

    [Fact]
    public async Task An_empty_attendance_list_is_refused_rather_than_marking_a_class_absent()
    {
        var store = new InMemoryLmsCredentialBackend();
        store.Save("t", new LmsAccount("omar@lms.example.com", "pw"));
        LmsCredentialBackend.Current = store;
        try
        {
            var outcome = await new AttendanceStage(
                new Accounts(new LmsCredentialStore("t")), new Names(), now: DuringTheClass).ExecuteAsync(Payload(), default);

            Assert.False(outcome.Succeeded);
            Assert.Equal("noAttendanceCollected", outcome.Error!.Code);
            Assert.False(outcome.Error.Retryable);
            Assert.Contains("absent", outcome.Error.Message);
        }
        finally { LmsCredentialBackend.Reset(); }
    }

    [Fact]
    public async Task A_payload_that_is_not_an_object_is_refused_without_a_crash()
    {
        var outcome = await new RunSessionStage(new Accounts(), now: DuringTheClass)
            .ExecuteAsync(JsonDocument.Parse("[1,2,3]").RootElement, default);
        Assert.Equal("invalidPayload", outcome.Error!.Code);
    }

    [Fact]
    public async Task Draining_puts_the_stage_back_in_the_queue_instead_of_failing_the_class()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        var outcome = await new Stopping().ExecuteAsync(Payload(), stopping.Token);

        Assert.False(outcome.Succeeded);
        Assert.Equal("workerStopping", outcome.Error!.Code);
        Assert.True(outcome.Error.Retryable);
    }

    private sealed class Stopping() : ClassStageHandler(now: DuringTheClass)
    {
        public override string JobType => "lms.run_session";
        protected override Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(JobOutcome.Success([]));
        }
    }

    [Theory]
    [InlineData(LmsFailure.SessionNotFinished, "sessionNotFinished", true)]
    [InlineData(LmsFailure.SessionNotFound, "sessionNotFound", false)]
    [InlineData(LmsFailure.NotSignedIn, "noLmsSignIn", false)]
    [InlineData(LmsFailure.Failed, "lmsFailed", false)]
    public void Only_a_failure_that_time_can_fix_is_tried_again(LmsFailure kind, string code, bool retryable)
    {
        // A session the dashboard has not finished yet will be finished later. One it does not
        // list at all will not appear by being asked twice, and retrying only buries the reason.
        var outcome = Exposed.Failed(kind, "what the LMS said");

        Assert.Equal(code, outcome.Error!.Code);
        Assert.Equal(retryable, outcome.Error.Retryable);
        Assert.Equal("what the LMS said", outcome.Error.Message);
    }

    /// <summary>Reaches the protected mapping without opening a browser to get at it.</summary>
    private sealed class Exposed : LmsStageHandler
    {
        private Exposed() : base(null!) { }
        public override string JobType => "lms.run_session";
        public static new JobOutcome Failed(LmsFailure kind, string message) => LmsStageHandler.Failed(kind, message);
        protected override Task<JobOutcome> RunOnLmsAsync(
            LmsSessionRunner runner, ClassStage stage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void An_attendance_failure_keeps_its_own_kind_on_the_way_out()
    {
        // The attendance result used to carry no kind at all, so every attendance failure reached
        // the server as 'lmsFailed': a session the dashboard has not finished yet, which will be
        // ready in five minutes, could not be told from one it does not list, which never will be.
        var notFinished = new LmsAttendanceResult(false, "not finished", null)
            { FailureKind = LmsFailure.SessionNotFinished };
        var notThere = new LmsAttendanceResult(false, "not listed", null)
            { FailureKind = LmsFailure.SessionNotFound };

        Assert.True(Exposed.Failed(notFinished.FailureKind, notFinished.Message).Error!.Retryable);
        Assert.Equal("sessionNotFound", Exposed.Failed(notThere.FailureKind, notThere.Message).Error!.Code);
    }
}
