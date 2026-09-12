using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ZoomAutoAdmit.CentralAgent.Tests;

/// <summary>Registration, what is kept on this PC, backoff, the journal and the settings.</summary>
public sealed class AgentFoundationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"central-agent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? RequestBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    // ------------------------------------------------------------------ registration

    [Fact]
    public async Task RegistrationSpendsTheEnrollmentTokenAndKeepsTheDeviceToken()
    {
        var deviceId = Guid.NewGuid();
        string deviceToken = $"zaad_{deviceId}.issued-secret";
        var handler = new RecordingHandler(HttpStatusCode.Created,
            JsonSerializer.Serialize(new { deviceId, deviceToken, heartbeatIntervalSeconds = 30 }));
        var identities = new DeviceIdentityStore(Path.Combine(_folder, "device.json"));
        var tokens = new MemoryTokenStore();
        var log = new List<string>();

        var registered = await new AgentRegistrar(new HttpClient(handler), identities, tokens, log.Add)
            .RegisterAsync(new Uri("https://central.example.com"), "zaae_enrollment-secret", "PC-01", "1.2.3",
                CentralAgentSettings.DefaultCapabilities, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://central.example.com/api/v1/agents/register", handler.Request.RequestUri!.ToString());
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("zaae_enrollment-secret", sent.RootElement.GetProperty("enrollmentToken").GetString());
        Assert.Equal(registered.InstallationId, sent.RootElement.GetProperty("installationId").GetGuid());
        Assert.Equal("PC-01", sent.RootElement.GetProperty("name").GetString());
        Assert.Equal("recording_processing", sent.RootElement.GetProperty("capabilities")[0].GetString());

        Assert.Equal(deviceToken, tokens.Token);
        Assert.Equal(deviceId, registered.DeviceId);
        Assert.True(identities.Load()!.IsRegistered);
        string onDisk = File.ReadAllText(identities.Path);
        Assert.DoesNotContain("issued-secret", onDisk);                 // the token is not in the file
        Assert.DoesNotContain("enrollment-secret", onDisk);
        Assert.DoesNotContain("secret", string.Join("\n", log));
    }

    [Fact]
    public async Task ARefusedEnrollmentTokenSavesNothing()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}""");
        var identities = new DeviceIdentityStore(Path.Combine(_folder, "device.json"));
        var tokens = new MemoryTokenStore();

        var ex = await Assert.ThrowsAsync<AgentRegistrationException>(() =>
            new AgentRegistrar(new HttpClient(handler), identities, tokens)
                .RegisterAsync(new Uri("https://central.example.com"), "zaae_used-token", "PC-01", "1.0.0", [], CancellationToken.None));
        Assert.Contains("unknown, already used or expired", ex.Message);
        Assert.DoesNotContain("zaae_used-token", ex.Message);
        Assert.Null(tokens.Token);
        Assert.False(identities.Load()!.IsRegistered);
    }

    [Fact]
    public async Task ATokenThatDoesNotBelongToTheReturnedDeviceIsRejected()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created,
            JsonSerializer.Serialize(new { deviceId = Guid.NewGuid(), deviceToken = $"zaad_{Guid.NewGuid()}.x" }));
        var tokens = new MemoryTokenStore();
        await Assert.ThrowsAsync<AgentRegistrationException>(() =>
            new AgentRegistrar(new HttpClient(handler), new DeviceIdentityStore(Path.Combine(_folder, "d.json")), tokens)
                .RegisterAsync(new Uri("https://central.example.com"), "zaae_x-token", "PC", "1", [], CancellationToken.None));
        Assert.Null(tokens.Token);
    }

    // ------------------------------------------------------------------ what is kept

    [Fact]
    public void TheInstallationIdIsMadeOnceAndNeverAgain()
    {
        var store = new DeviceIdentityStore(Path.Combine(_folder, "device.json"));
        var first = store.LoadOrCreate();
        var again = new DeviceIdentityStore(store.Path).LoadOrCreate();
        Assert.NotEqual(Guid.Empty, first.InstallationId);
        Assert.Equal(first.InstallationId, again.InstallationId);

        store.Save(first with { DeviceId = Guid.NewGuid(), BackendUrl = "https://central.example.com/", Name = "PC-01" });
        var reloaded = new DeviceIdentityStore(store.Path).LoadOrCreate();
        Assert.Equal(first.InstallationId, reloaded.InstallationId);
        Assert.Equal("PC-01", reloaded.Name);
    }

    [Fact]
    public void TheDeviceTokenRoundTripsThroughWindowsCredentialManager()
    {
        // A throwaway entry under a test-only name, removed again at the end.
        var store = new CredentialManagerDeviceTokenStore($"ZoomAutoAdmit/Tests/CentralAgent/{Guid.NewGuid():N}");
        try
        {
            Assert.Null(store.Read());
            store.Save("zaad_00000000-0000-0000-0000-000000000000.test-only-secret");
            Assert.Equal("zaad_00000000-0000-0000-0000-000000000000.test-only-secret", new CredentialManagerDeviceTokenStore(
                GetTarget(store)).Read());
        }
        finally
        {
            store.Delete();
        }
        Assert.Null(store.Read());
    }

    private static string GetTarget(CredentialManagerDeviceTokenStore store) =>
        (string)typeof(CredentialManagerDeviceTokenStore)
            .GetField("<target>P", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(store)!;

    // ------------------------------------------------------------------ backoff

    [Fact]
    public void ReconnectDelaysDoubleUpToThirtySeconds()
    {
        var delays = Enumerable.Range(0, 8)
            .Select(i => ReconnectBackoff.BaseDelay(i, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)).TotalSeconds);
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], delays);
    }

    [Fact]
    public void JitterStaysWithinTwentyPercentAndResetStartsOver()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.2, new Random(7));
        for (int i = 0; i < 7; i++)
        {
            double expected = ReconnectBackoff.BaseDelay(i, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)).TotalMilliseconds;
            double actual = backoff.Next().TotalMilliseconds;
            Assert.InRange(actual, expected * 0.8, expected * 1.2);
        }
        backoff.Reset();
        Assert.InRange(backoff.Next().TotalMilliseconds, 800, 1200);
    }

    [Fact]
    public void ManyAgentsDoNotAllReconnectAtTheSameMoment()
    {
        var firstDelays = Enumerable.Range(0, 50)
            .Select(seed => new ReconnectBackoff(TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), 0.2, new Random(seed)).Next().TotalMilliseconds)
            .ToList();
        Assert.True(firstDelays.Distinct().Count() > 40);
        Assert.True(firstDelays.Max() - firstDelays.Min() > 2000);
    }

    // ------------------------------------------------------------------ journal

    [Fact]
    public void TheJournalAcceptsAJobOnceAndSurvivesARestart()
    {
        string path = Path.Combine(_folder, "jobs.json");
        var journal = new JobJournal(path);
        string jobId = Guid.NewGuid().ToString();
        Assert.True(journal.TryAccept(jobId));
        Assert.False(journal.TryAccept(jobId));
        Assert.True(journal.TryStart(jobId));
        Assert.False(journal.TryStart(jobId));
        journal.Finish(jobId, succeeded: true, """{"type":"job.succeeded"}""");

        var reopened = new JobJournal(path);
        Assert.Equal(JobState.Succeeded, reopened.StateOf(jobId));
        Assert.False(reopened.TryAccept(jobId));
        Assert.Equal("job.succeeded", Assert.Single(reopened.PendingMessages()).Type);
        reopened.Acknowledge(jobId, "job.succeeded");
        Assert.Empty(new JobJournal(path).PendingMessages());
    }

    [Fact]
    public void AfterARestartAcceptedJobsAreReleasedAndRunningOnesReported()
    {
        var journal = new JobJournal(path: null);
        journal.TryAccept("accepted");
        journal.TryAccept("running");
        journal.TryStart("running");
        Assert.Equal(["running"], journal.RecoverAfterRestart());
        Assert.Equal(JobState.Released, journal.StateOf("accepted"));
        Assert.True(journal.TryAccept("accepted"));
    }

    [Fact]
    public void TheJournalForgetsOldFinishedJobsButNeverOnesStillWaitingToBeSent()
    {
        var journal = new JobJournal(path: null, keep: 3);
        journal.TryStart("waiting");
        journal.Finish("waiting", true, "{}");
        for (int i = 0; i < 6; i++)
        {
            journal.TryStart($"done-{i}");
            journal.Finish($"done-{i}", true, "{}");
            journal.Acknowledge($"done-{i}", "job.succeeded");
        }
        Assert.Equal(JobState.Succeeded, journal.StateOf("waiting"));
        Assert.Null(journal.StateOf("done-0"));
        Assert.Equal(JobState.Succeeded, journal.StateOf("done-5"));
    }

    [Fact]
    public void ADamagedJournalIsSetAsideNotTrusted()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "jobs.json");
        File.WriteAllText(path, "{ not json");
        var journal = new JobJournal(path);
        Assert.Empty(journal.PendingMessages());
        Assert.Single(Directory.GetFiles(_folder, "jobs.json.damaged-*"));
    }

    // ------------------------------------------------------------------ settings

    [Theory]
    [InlineData("https://central.example.com", "https://central.example.com/", "wss://central.example.com/ws/agent")]
    [InlineData("https://central.example.com/zaa", "https://central.example.com/zaa/", "wss://central.example.com/zaa/ws/agent")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/", "ws://127.0.0.1:8080/ws/agent")]
    [InlineData("http://localhost:8080/", "http://localhost:8080/", "ws://localhost:8080/ws/agent")]
    public void TheBackendAddressMapsToItsWebSocket(string text, string expected, string socket)
    {
        var url = CentralAgentSettings.ParseBackendUrl(text);
        Assert.Equal(expected, url.ToString());
        Assert.Equal(socket, new CentralAgentSettings { BackendUrl = url, Version = "1" }.WebSocketUri.ToString());
    }

    [Theory]
    [InlineData("http://central.example.com")]          // plain http off this PC
    [InlineData("http://192.168.1.10:8080")]
    [InlineData("ftp://central.example.com")]
    [InlineData("https://user:pass@central.example.com")]
    [InlineData("https://central.example.com/?token=x")]
    [InlineData("central.example.com")]
    [InlineData("")]
    public void AnUnsafeBackendAddressIsRefused(string text) =>
        Assert.Throws<ArgumentException>(() => CentralAgentSettings.ParseBackendUrl(text));

    [Fact]
    public void LogLinesCannotBeSplitOrForged()
    {
        string line = AgentLog.Line("job_failed", ("code", "x\n[AGENT] event=forged"), ("jobId", "a b"));
        Assert.DoesNotContain('\n', line);
        Assert.Equal(1, line.Split("event=").Length - 1);
        Assert.Contains("jobId=a_b", line);
    }
}
