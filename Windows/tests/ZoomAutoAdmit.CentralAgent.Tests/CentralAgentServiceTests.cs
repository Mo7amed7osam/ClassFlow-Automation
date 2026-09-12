using System.Text.Json.Nodes;
using ZoomAutoAdmit.WebAutomation.Recordings;
using Xunit;

namespace ZoomAutoAdmit.CentralAgent.Tests;

/// <summary>The agent against a real WebSocket server on loopback. No LMS, Zoom or browser involved.</summary>
public sealed class CentralAgentServiceTests
{
    private static string NewJobId() => Guid.NewGuid().ToString();

    // ------------------------------------------------------------------ connection & heartbeat

    [Fact]
    public async Task ItConnectsOutWithTheDeviceTokenInAHeaderAndSaysHello()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();

        var connection = await backend.NextConnectionAsync();
        var hello = await connection.NextAsync("hello");

        Assert.Equal($"Bearer {AgentUnderTest.DeviceToken}", Assert.Single(backend.AuthorizationHeaders));
        Assert.DoesNotContain(AgentUnderTest.DeviceToken, Assert.Single(backend.RequestUrls));   // never in the URL
        Assert.EndsWith("/ws/agent", Assert.Single(backend.RequestUrls));
        Assert.Equal("1.2.3", hello["version"]!.GetValue<string>());
        Assert.Equal("idle", hello["agentState"]!.GetValue<string>());
        Assert.Equal(["recording_processing", "lms"], hello["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Contains("event=connected", agent.AllLog);
    }

    [Fact]
    public async Task ItSendsAHeartbeatEveryInterval()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend, heartbeat: TimeSpan.FromMilliseconds(150)).Start();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");

        var first = await connection.NextAsync("heartbeat");
        var second = await connection.NextAsync("heartbeat");
        Assert.Equal(AgentUnderTest.DeviceId.ToString(), first["deviceId"]!.GetValue<string>());
        Assert.Equal("idle", first["status"]!.GetValue<string>());
        Assert.Equal("1.2.3", second["version"]!.GetValue<string>());
        Assert.Equal(2, second["capabilities"]!.AsArray().Count);
        Assert.Contains("event=heartbeat status=idle", agent.AllLog);
    }

    [Fact]
    public async Task ItReconnectsWhenTheBackendClosesTheConnection()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var first = await backend.NextConnectionAsync();
        await first.NextAsync("hello");
        await first.CloseAsync();

        var second = await backend.NextConnectionAsync();
        await second.NextAsync("hello");
        Assert.Contains("event=disconnected", agent.AllLog);
        Assert.Contains("event=reconnect_scheduled", agent.AllLog);
    }

    [Fact]
    public async Task ItReconnectsWhenTheLinkDiesSilently()
    {
        // The backend never answers anything - as after sleep or a network change, where the TCP
        // connection is dead but nobody said so.
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend, heartbeat: TimeSpan.FromSeconds(10), silence: TimeSpan.FromMilliseconds(400)).Start();
        var first = await backend.NextConnectionAsync();
        await first.NextAsync("hello");

        var second = await backend.NextConnectionAsync();
        await second.NextAsync("hello");
        Assert.Contains("reason=server_silent", agent.AllLog);
    }

    [Fact]
    public async Task ARefusedDeviceTokenStopsTheAgentInsteadOfRetrying()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken) { RefuseEveryone = true };
        await using var agent = new AgentUnderTest(backend).Start();

        var reason = await agent.Running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentStopReason.Unauthorized, reason);
        Assert.Equal(1, backend.Refused);
        Assert.Contains("event=unauthorized", agent.AllLog);
    }

    [Fact]
    public async Task WithoutADeviceTokenItDoesNotConnectAtAll()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend, tokens: new MemoryTokenStore()).Start();
        Assert.Equal(AgentStopReason.NotRegistered, await agent.Running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(backend.AuthorizationHeaders);
    }

    // ------------------------------------------------------------------ jobs

    [Fact]
    public async Task AnAssignedJobIsAcceptedButNotRunUntilTheBackendSaysStart()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();

        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        var accepted = await connection.NextAsync("job.accepted");
        Assert.Equal(jobId, accepted["jobId"]!.GetValue<string>());
        Assert.Empty(await connection.DrainAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Empty(agent.Processor.Calls);
        Assert.Contains($"event=job_received jobId={jobId}", agent.AllLog);
    }

    [Fact]
    public async Task AStartedJobRunsTheExistingRecordingWorkflowAndReportsItsResult()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();

        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        Assert.Equal(jobId, (await connection.NextAsync("job.started"))["jobId"]!.GetValue<string>());
        var done = await connection.NextAsync("job.succeeded");

        var call = Assert.Single(agent.Processor.Calls);
        Assert.Equal("AST5_DAT1_S1", call.Group);
        Assert.Equal($"https://drive.google.com/file/d/{AgentUnderTest.DriveId}/view?usp=sharing", call.RecordLink);
        Assert.Equal(new DateOnly(2026, 9, 3), call.Date);
        Assert.False(call.ReplaceExisting);
        Assert.False(call.DryRun);
        Assert.False(call.Headed);

        Assert.Equal(jobId, done["jobId"]!.GetValue<string>());
        Assert.False(done["result"]!["alreadyExists"]!.GetValue<bool>());
        Assert.Equal("Recording link attached successfully.", done["result"]!["message"]!.GetValue<string>());
        Assert.Contains($"event=job_succeeded jobId={jobId}", agent.AllLog);

        await connection.AckAsync(jobId, "job.succeeded");
        await connection.AckAsync(jobId, "job.started");
        await WaitUntil(() => agent.Journal.PendingMessages().Count == 0);
    }

    [Fact]
    public async Task AnExistingLinkIsASuccessWithAlreadyExists()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        agent.Processor.Answer = r => Task.FromResult(FakeRecordingProcessor.Outcome(r, RecordingLinkStatus.AlreadyExists, "already has a link"));
        var result = await RunOneJobAsync(backend, "job.succeeded");
        Assert.True(result["result"]!["alreadyExists"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(RecordingLinkStatus.LmsFailed, "sessionNotFinished", "sessionNotFinished", false)]
    [InlineData(RecordingLinkStatus.LmsFailed, null, "lmsFailed", false)]
    [InlineData(RecordingLinkStatus.Busy, null, "busy", true)]
    public async Task AFailureIsReportedWithItsCode(RecordingLinkStatus status, string? reason, string code, bool retryable)
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        agent.Processor.Answer = r => Task.FromResult(FakeRecordingProcessor.Outcome(r, status, "it did not work", reason));

        var failed = await RunOneJobAsync(backend, "job.failed");
        Assert.Equal(code, failed["error"]!["code"]!.GetValue<string>());
        Assert.Equal(retryable, failed["error"]!["retryable"]!.GetValue<bool>());
        if (retryable) Assert.Equal(60, failed["error"]!["retryAfterSeconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnInvalidPayloadFailsWithoutTouchingTheLms()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var bad = AgentUnderTest.Payload();
        bad["recordLink"] = "C:\\Recordings\\x.mp4";

        var failed = await RunOneJobAsync(backend, "job.failed", bad);
        Assert.Equal("invalidPayload", failed["error"]!["code"]!.GetValue<string>());
        Assert.Empty(agent.Processor.Calls);
    }

    [Fact]
    public async Task AJobDeliveredTwiceRunsOnceAndARepeatAfterwardsGetsTheSameResult()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        agent.Processor.Gate = new TaskCompletionSource();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();

        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(jobId, AgentUnderTest.Payload());            // repeated assignment
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        await connection.NextAsync("job.started");
        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start"); // repeated start while running
        agent.Processor.Gate.SetResult();
        var first = await connection.NextAsync("job.succeeded");

        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start"); // and again once finished
        var repeat = await connection.NextAsync("job.succeeded");
        Assert.Equal(first.ToJsonString(), repeat.ToJsonString());
        Assert.Single(agent.Processor.Calls);
        Assert.Contains("event=duplicate", agent.AllLog);
    }

    [Fact]
    public async Task AResultFinishedWhileDisconnectedIsDeliveredAfterReconnecting()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        agent.Processor.Gate = new TaskCompletionSource();
        var first = await backend.NextConnectionAsync();
        await first.NextAsync("hello");
        string jobId = NewJobId();
        await first.AssignAsync(jobId, AgentUnderTest.Payload());
        await first.NextAsync("job.accepted");
        await first.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        await first.NextAsync("job.started");

        first.Abort();                                   // the connection drops mid-job
        var second = await backend.NextConnectionAsync();
        var hello = await second.NextAsync("hello");
        Assert.Equal(jobId, hello["activeJobId"]!.GetValue<string>());
        Assert.Equal("busy", hello["agentState"]!.GetValue<string>());
        await second.NextAsync("job.started");           // unacknowledged, so sent again

        agent.Processor.Gate.SetResult();                // the job finishes on the new connection
        var done = await second.NextAsync("job.succeeded");
        Assert.Equal(jobId, done["jobId"]!.GetValue<string>());
        Assert.Single(agent.Processor.Calls);
    }

    [Fact]
    public async Task AResultNotYetAcknowledgedIsSentAgainOnTheNextConnection()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var first = await backend.NextConnectionAsync();
        await first.NextAsync("hello");
        string jobId = NewJobId();
        await first.AssignAsync(jobId, AgentUnderTest.Payload());
        await first.NextAsync("job.accepted");
        await first.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        await first.NextAsync("job.started");
        await first.NextAsync("job.succeeded");
        await first.CloseAsync();                        // closed before the backend acknowledged it

        var second = await backend.NextConnectionAsync();
        await second.NextAsync("hello");
        Assert.Equal(jobId, (await second.NextAsync("job.succeeded"))["jobId"]!.GetValue<string>());
        await second.AckAsync(jobId, "job.succeeded");
        await WaitUntil(() => agent.Journal.PendingMessages().All(p => p.Type != "job.succeeded"));
        Assert.Single(agent.Processor.Calls);
    }

    [Fact]
    public async Task ASecondJobWhileBusyIsTurnedDown()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        agent.Processor.Gate = new TaskCompletionSource();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string first = NewJobId(), second = NewJobId();
        await connection.AssignAsync(first, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(first, AgentUnderTest.Payload(), "job.start");
        await connection.NextAsync("job.started");

        await connection.AssignAsync(second, AgentUnderTest.Payload());
        var rejected = await connection.NextAsync("job.rejected");
        Assert.Equal(second, rejected["jobId"]!.GetValue<string>());
        Assert.Equal("busy", rejected["reason"]!.GetValue<string>());
        agent.Processor.Gate.SetResult();
        await connection.NextAsync("job.succeeded");
        Assert.Single(agent.Processor.Calls);
    }

    [Fact]
    public async Task ARevokedJobIsNotRunAndCanBeOfferedAgainLater()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend).Start();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();
        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.SendAsync(new JsonObject { ["type"] = "job.revoke", ["jobId"] = jobId });
        await WaitUntil(() => agent.Journal.StateOf(jobId) == JobState.Released);
        Assert.Empty(agent.Processor.Calls);

        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
    }

    [Fact]
    public async Task AfterARestartAFinishedJobIsNotRunAgain()
    {
        string journalPath = Path.Combine(Path.GetTempPath(), $"central-journal-{Guid.NewGuid():N}.json");
        try
        {
            await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
            string jobId = NewJobId();
            await using (var before = new AgentUnderTest(backend, new JobJournal(journalPath)).Start())
            {
                var c = await backend.NextConnectionAsync();
                await c.NextAsync("hello");
                await c.AssignAsync(jobId, AgentUnderTest.Payload());
                await c.NextAsync("job.accepted");
                await c.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
                await c.NextAsync("job.started");
                await c.NextAsync("job.succeeded");
                await c.AckAsync(jobId, "job.succeeded");
                await WaitUntil(() => before.Journal.PendingMessages().All(p => p.Type != "job.succeeded"));
            }

            await using var after = new AgentUnderTest(backend, new JobJournal(journalPath)).Start();
            var connection = await backend.NextConnectionAsync();
            await connection.NextAsync("hello");
            await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
            Assert.Equal(jobId, (await connection.NextAsync("job.succeeded"))["jobId"]!.GetValue<string>());
            Assert.Empty(after.Processor.Calls);
            Assert.DoesNotContain(AgentUnderTest.DriveId, File.ReadAllText(journalPath));   // no payload is kept
        }
        finally { File.Delete(journalPath); }
    }

    [Fact]
    public async Task AJobThatWasRunningWhenTheAgentStoppedIsReportedAsInterrupted()
    {
        var journal = new JobJournal(path: null);
        string jobId = NewJobId();
        journal.TryAccept(jobId);
        journal.TryStart(jobId);                          // as if the process died here

        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend, journal).Start();
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        var failed = await connection.NextAsync("job.failed");
        Assert.Equal(jobId, failed["jobId"]!.GetValue<string>());
        Assert.Equal("agentRestarted", failed["error"]!["code"]!.GetValue<string>());

        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        await connection.NextAsync("job.failed");         // answered from the journal, not re-run
        Assert.Empty(agent.Processor.Calls);
    }

    [Fact]
    public async Task NoSecretOrFullLinkReachesTheLog()
    {
        await using var backend = new LoopbackBackend(AgentUnderTest.DeviceToken);
        await using var agent = new AgentUnderTest(backend, heartbeat: TimeSpan.FromMilliseconds(100)).Start();
        agent.Processor.Answer = r => Task.FromResult(FakeRecordingProcessor.Outcome(r, RecordingLinkStatus.LmsFailed, "failed", "lmsFailed"));
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();
        await connection.AssignAsync(jobId, AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(jobId, AgentUnderTest.Payload(), "job.start");
        await connection.NextAsync("job.started");
        await connection.NextAsync("job.failed");
        await connection.CloseAsync();
        await backend.NextConnectionAsync();

        string log = agent.AllLog;
        Assert.Contains("event=job_failed", log);
        Assert.DoesNotContain(AgentUnderTest.DeviceToken, log);
        Assert.DoesNotContain("TEST-device-secret", log);
        Assert.DoesNotContain(AgentUnderTest.DriveId, log);
        Assert.DoesNotContain("drive.google.com", log);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<JsonObject> RunOneJobAsync(LoopbackBackend backend, string finalType, JsonObject? payload = null)
    {
        var connection = await backend.NextConnectionAsync();
        await connection.NextAsync("hello");
        string jobId = NewJobId();
        await connection.AssignAsync(jobId, payload ?? AgentUnderTest.Payload());
        await connection.NextAsync("job.accepted");
        await connection.AssignAsync(jobId, payload ?? AgentUnderTest.Payload(), "job.start");
        await connection.NextAsync("job.started");
        return await connection.NextAsync(finalType);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }
}
