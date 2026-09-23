using System.Text.Json;
using Xunit;
using ZoomAutoAdmit.CentralAgent;
using ZoomAutoAdmit.CloudWorker.Stages;
using ZoomAutoAdmit.Core.Central;

namespace ZoomAutoAdmit.CloudWorker.Tests;

/// <summary>
/// What a worker writes down about a class, which is what the dashboard's record of every machine
/// is made of. A class run in the cloud has nobody watching it, so a class that left nothing behind
/// is a class nobody can ask about afterwards.
/// </summary>
public class WorkerActivityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "classflow-activity-" + Guid.NewGuid().ToString("N"));
    private readonly ActivityLog _log;

    public WorkerActivityTests()
    {
        _log = new ActivityLog(_folder);
        ClassStageHandler.Activity = _log;
    }

    public void Dispose()
    {
        ClassStageHandler.Activity = null;
        try { Directory.Delete(_folder, recursive: true); } catch (Exception) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A stage that answers whatever the test tells it to.</summary>
    private sealed class Stage(JobOutcome outcome) : ClassStageHandler
    {
        public override string JobType => "lms.attendance";
        protected override Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private static JsonElement Payload() => JsonSerializer.Deserialize<JsonElement>("""
    {
      "classPlanId": "11111111-1111-4111-8111-111111111111",
      "group": "CAI5_IND1_G1",
      "date": "2026-09-23",
      "startTime": "18:00",
      "coordinatorId": "22222222-2222-4222-8222-222222222222"
    }
    """);

    /// <summary>
    /// The notes about this test's own class. The worker keeps one log for everything it does, and
    /// this assembly's other tests run beside these and write their own notes into it - which is the
    /// log working as intended, not a test problem to design away.
    /// </summary>
    private IReadOnlyList<ActivityEvent> Written() =>
        [.. _log.Pending(200).Select(waiting => waiting.Event).Where(note => note.Group == "CAI5_IND1_G1")];

    [Fact]
    public async Task A_step_that_finished_is_written_down_with_its_class()
    {
        var result = new System.Text.Json.Nodes.JsonObject
        {
            ["did"] = "wrote the attendance up",
            ["message"] = "CAI5_IND1_G1: 23 students written up.",
        };
        await new Stage(JobOutcome.Success(result)).ExecuteAsync(Payload(), default);

        var note = Assert.Single(Written());
        Assert.Equal("lms.attendance", note.Kind);
        Assert.Equal("done", note.Outcome);
        Assert.Equal("CAI5_IND1_G1: 23 students written up.", note.Summary);
        Assert.Equal("CAI5_IND1_G1", note.Group);
        Assert.Equal("2026-09-23", note.Date);
    }

    [Fact]
    public async Task A_step_that_failed_says_what_happened()
    {
        await new Stage(JobOutcome.Failure("lmsUnreachable", "The session list did not load.")).ExecuteAsync(Payload(), default);

        var note = Assert.Single(Written());
        Assert.Equal("failed", note.Outcome);
        Assert.Equal("The session list did not load.", note.Summary);
    }

    [Fact]
    public async Task A_step_that_will_be_tried_again_is_not_written_down_as_a_failure()
    {
        // The recording is not published yet, so the job comes back later: a record of the class
        // must not read as though something went wrong each time it waited.
        await new Stage(JobOutcome.Failure("notPublishedYet", "Zoom has not published it yet.", retryable: true))
            .ExecuteAsync(Payload(), default);

        Assert.Empty(Written());
    }

    [Fact]
    public async Task A_step_that_finished_leaving_something_behind_is_not_a_clean_done()
    {
        var result = new System.Text.Json.Nodes.JsonObject
        {
            ["did"] = "held the meeting but could not close it",
            ["message"] = "CAI5_IND1_G1: this account is not the meeting's host, so it was left open.",
            ["warning"] = "this account is not the meeting's host, so it was left open.",
        };
        await new Stage(JobOutcome.Success(result)).ExecuteAsync(Payload(), default);

        Assert.Equal("skipped", Assert.Single(Written()).Outcome);
    }

    [Fact]
    public async Task A_stage_that_threw_is_written_down_rather_than_disappearing()
    {
        var thrown = new ThrowingStage();
        var outcome = await thrown.ExecuteAsync(Payload(), default);

        Assert.False(outcome.Succeeded);
        var note = Assert.Single(Written());
        Assert.Equal("failed", note.Outcome);
        Assert.Contains("the browser was gone", note.Summary);
    }

    private sealed class ThrowingStage : ClassStageHandler
    {
        public override string JobType => "class.run";
        protected override Task<JobOutcome> RunAsync(ClassStage stage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the browser was gone");
    }

    [Fact]
    public async Task A_worker_with_nowhere_to_write_still_runs_the_class()
    {
        ClassStageHandler.Activity = null;

        var outcome = await new Stage(JobOutcome.Success(new System.Text.Json.Nodes.JsonObject { ["did"] = "ran" }))
            .ExecuteAsync(Payload(), default);

        Assert.True(outcome.Succeeded);
    }
}
