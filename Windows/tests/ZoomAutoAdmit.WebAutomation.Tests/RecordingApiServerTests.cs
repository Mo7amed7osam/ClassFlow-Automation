using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ZoomAutoAdmit.WebAutomation.Api;
using ZoomAutoAdmit.WebAutomation.Recordings;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>
/// The real HTTP server on a free loopback port, talking to a fake workflow. Nothing here opens a
/// browser, Zoom or the dashboard.
/// </summary>
public sealed class RecordingApiServerTests : IAsyncLifetime
{
    private const string Key = "test-key-0123456789-abcdefghij";
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Egypt Standard Time");

    private readonly FakeProcessor _processor = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private RecordingApiServer _server = null!;
    private HttpClient _http = null!;

    private sealed class FakeProcessor : IRecordingLinkProcessor
    {
        public readonly ConcurrentQueue<RecordingLinkRequest> Seen = new();
        public Func<RecordingLinkRequest, Task<RecordingLinkOutcome>> Answer =
            request => Task.FromResult(Outcome(request, RecordingLinkStatus.Attached, "saved"));

        public Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken)
        {
            Seen.Enqueue(request);
            return Answer(request);
        }
    }

    private static RecordingLinkOutcome Outcome(RecordingLinkRequest request, RecordingLinkStatus status, string message,
        string? reason = null) =>
        new(status, message)
        {
            Group = request.Group,
            Date = request.Date ?? new DateOnly(2026, 9, 3),
            StartTime = request.StartTime,
            Profile = "s7",
            Reason = reason,
        };

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public Task InitializeAsync()
    {
        _server = new RecordingApiServer(RecordingApiOptions.ForTesting(FreePort(), Key), _processor, _logs.Enqueue, Cairo);
        _server.Start();
        _http = new HttpClient { BaseAddress = _server.BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string json, string? key = Key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, RecordingApiServer.ProcessPath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (key != null) message.Headers.Add(RecordingApiServer.KeyHeader, key);
        using var response = await _http.SendAsync(message);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private const string Valid = """{"group":"CAI5_AIS4_S7","date":"2026-09-03","startTime":"15:50"}""";

    // ------------------------------------------------------------------------------ health

    [Fact]
    public async Task HealthAnswersWithoutAKey()
    {
        using var response = await _http.GetAsync(RecordingApiServer.HealthPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        Assert.Empty(_processor.Seen);
    }

    // ------------------------------------------------------------------------------ authentication

    [Fact]
    public async Task AMissingKeyIsRefused()
    {
        var (status, body) = await PostAsync(Valid, key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
        Assert.Empty(_processor.Seen);
    }

    [Fact]
    public async Task AWrongKeyIsRefused()
    {
        var (status, _) = await PostAsync(Valid, key: Key + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var (again, _) = await PostAsync(Valid, key: "short");
        Assert.Equal(HttpStatusCode.Unauthorized, again);
        Assert.Empty(_processor.Seen);
    }

    [Fact]
    public async Task TheKeyIsCheckedBeforeTheBodyIsRead()
    {
        // An unauthenticated caller gets 401 even for nonsense, and learns nothing about the format.
        var (status, _) = await PostAsync("not json at all", key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task AValidKeyReachesTheWorkflowWithTheRequestAsSent()
    {
        var (status, body) = await PostAsync(
            """{"group":"  CAI5_AIS4_S7 ","date":"2026-09-03","startTime":"15:50","profile":"s7","headed":false,"dryRun":false,"replaceExisting":false}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Recording link attached successfully.", body.GetProperty("message").GetString());
        Assert.False(body.GetProperty("alreadyExists").GetBoolean());
        Assert.Equal("CAI5_AIS4_S7", body.GetProperty("group").GetString());
        Assert.Equal("2026-09-03", body.GetProperty("date").GetString());
        Assert.Equal("15:50", body.GetProperty("startTime").GetString());

        var seen = Assert.Single(_processor.Seen);
        Assert.Equal("CAI5_AIS4_S7", seen.Group);               // trimmed
        Assert.Equal(new DateOnly(2026, 9, 3), seen.Date);
        Assert.Equal(new TimeOnly(15, 50), seen.StartTime);
        Assert.Equal("s7", seen.Profile);
        Assert.False(seen.KeepBrowserOpen);                     // never left open by the API
    }

    // ------------------------------------------------------------------------------ validation

    [Theory]
    [InlineData("""{"date":"2026-09-03"}""", "'group' is required")]
    [InlineData("""{"group":"   "}""", "'group' is required")]
    [InlineData("""{"group":"CAI5<script>"}""", "'group' may contain only")]
    [InlineData("""{"group":"CAI5_AIS4_S7","date":"03/09/2026"}""", "yyyy-MM-dd")]
    [InlineData("""{"group":"CAI5_AIS4_S7","date":"2026-02-30"}""", "yyyy-MM-dd")]
    [InlineData("""{"group":"CAI5_AIS4_S7","date":""}""", "'date' is empty")]
    [InlineData("""{"group":"CAI5_AIS4_S7","startTime":"3:50pm"}""", "HH:mm")]
    [InlineData("""{"group":"CAI5_AIS4_S7","startTime":"25:00"}""", "HH:mm")]
    [InlineData("""{"group":"CAI5_AIS4_S7","startTime":""}""", "'startTime' is empty")]
    [InlineData("""{"group":"CAI5_AIS4_S7","dryRun":"yes"}""", "'dryRun' must be true or false")]
    [InlineData("""{"group":"CAI5_AIS4_S7","profile":"../../evil"}""", "'profile' must be")]
    [InlineData("""{"group":"CAI5_AIS4_S7","starttime":"15:50"}""", "'starttime' is not a known field")]
    [InlineData("""{"group":"CAI5_AIS4_S7","timeZone":"utc","startTime":"15:50"}""", "needs both")]
    [InlineData("""["CAI5_AIS4_S7"]""", "JSON object")]
    [InlineData("""{"group":""", "JSON object")]
    public async Task ABadRequestSaysWhatIsWrongAndReachesNothing(string json, string expected)
    {
        var (status, body) = await PostAsync(json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid request", body.GetProperty("error").GetString());
        Assert.Contains(expected, body.GetProperty("details").GetString());
        Assert.Empty(_processor.Seen);
    }

    [Theory]
    [InlineData("recordLink")]
    [InlineData("driveUrl")]
    [InlineData("googleDriveUrl")]
    [InlineData("fileName")]
    [InlineData("file")]
    [InlineData("link")]
    public async Task ADriveLinkOrFileIsNeverAccepted(string field)
    {
        var (status, body) = await PostAsync(
            $$"""{"group":"CAI5_AIS4_S7","date":"2026-09-03","{{field}}":"https://drive.google.com/file/d/abc/view"}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("finds the Zoom recording itself", body.GetProperty("details").GetString());
        Assert.Empty(_processor.Seen);
    }

    [Fact]
    public async Task ATimeInUtcIsTurnedIntoThisComputersTimeIncludingTheDay()
    {
        // The Drive file names are UTC: 22:30 UTC on the 3rd is 01:30 on the 4th in Cairo (UTC+3).
        var (status, body) = await PostAsync("""{"group":"CAI5_AIS4_S7","date":"2026-09-03","startTime":"22:30","timeZone":"utc"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var seen = Assert.Single(_processor.Seen);
        Assert.Equal(new DateOnly(2026, 9, 4), seen.Date);
        Assert.Equal(new TimeOnly(1, 30), seen.StartTime);
        Assert.Equal("01:30", body.GetProperty("startTime").GetString());
    }

    [Fact]
    public async Task OptionalFieldsCanBeLeftOutOrNull()
    {
        var (status, _) = await PostAsync("""{"group":"CAI5_AIS4_S7","date":null,"startTime":null,"profile":null,"headed":null}""");
        Assert.Equal(HttpStatusCode.OK, status);
        var seen = Assert.Single(_processor.Seen);
        Assert.Null(seen.Date);
        Assert.Null(seen.StartTime);
        Assert.Null(seen.Profile);
        Assert.False(seen.DryRun);
        Assert.False(seen.ReplaceExisting);
    }

    // ------------------------------------------------------------------------------ outcomes

    [Fact]
    public async Task AnExistingLinkIsASuccessThatSaysSo()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.AlreadyExists,
            "CAI5_AIS4_S7: the session already has a recording link, so it was left as it is."));
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("alreadyExists").GetBoolean());
        Assert.Contains("already has a recording link", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ARecordingThatIsNotThereIs404WithTheSessionItWasFor()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.RecordingNotFound,
            "No cloud recording is listed for CAI5_AIS4_S7.", "notFound"));
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Recording not found", body.GetProperty("error").GetString());
        Assert.Equal("CAI5_AIS4_S7", body.GetProperty("group").GetString());
        Assert.Equal("2026-09-03", body.GetProperty("date").GetString());
        Assert.Equal("15:50", body.GetProperty("startTime").GetString());
        Assert.Equal("notFound", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ADashboardFailureIs500WithAReasonToActOn()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.LmsFailed,
            "The session page offers no Add Record Link; it reads \"running\".", "sessionNotFinished"));
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("LMS operation failed", body.GetProperty("error").GetString());
        Assert.Equal("sessionNotFinished", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AZoomFailureIsToldApartFromADashboardOne()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.ZoomFailed,
            "The 's7' browser profile is not signed in to Zoom.", "zoomNotSignedIn"));
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("Zoom operation failed", body.GetProperty("error").GetString());
        Assert.Equal("zoomNotSignedIn", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ABusyProfileIs409()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.Busy, "in use"));
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Busy", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReplaceExistingIsPassedOnExactlyAsSent()
    {
        await PostAsync("""{"group":"CAI5_AIS4_S7","replaceExisting":true}""");
        await PostAsync("""{"group":"CAI5_AIS4_S7","replaceExisting":false}""");
        await PostAsync("""{"group":"CAI5_AIS4_S7"}""");
        Assert.Equal([true, false, false], _processor.Seen.Select(request => request.ReplaceExisting));
    }

    [Fact]
    public async Task AnUnexpectedErrorIs500WithoutItsDetails()
    {
        _processor.Answer = _ => throw new InvalidOperationException(@"C:\Users\someone\secret-path and a cookie=abc");
        var (status, body) = await PostAsync(Valid);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("Internal error", body.GetProperty("error").GetString());
        string raw = body.GetRawText();
        Assert.DoesNotContain("secret-path", raw);
        Assert.DoesNotContain("cookie", raw);
        Assert.DoesNotContain("secret-path", string.Join("\n", _logs));
    }

    [Fact]
    public async Task UnknownPathsAndWrongMethodsAreRefused()
    {
        using var unknown = await _http.GetAsync("/api/recordings/other");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrongMethod = await _http.GetAsync(RecordingApiServer.ProcessPath);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Empty(_processor.Seen);
    }

    // ------------------------------------------------------------------------------ concurrency

    [Fact]
    public async Task RequestsArriveTogetherAndAreAllAnswered()
    {
        // Several at once reach the workflow; its profile lock is what serialises them (tested in
        // RecordingLinkProcessorTests). The server must not drop or mix any of them up.
        int running = 0, peak = 0;
        _processor.Answer = async request =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
            return Outcome(request, RecordingLinkStatus.Attached, "saved");
        };
        var calls = Enumerable.Range(1, 4).Select(i =>
            PostAsync($$"""{"group":"GROUP_{{i}}","date":"2026-09-03"}""")).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        Assert.Equal(["GROUP_1", "GROUP_2", "GROUP_3", "GROUP_4"],
            results.Select(result => result.Body.GetProperty("group").GetString()).Order());
        Assert.True(peak > 1, "the server handled requests one at a time, which would make every caller wait for all others");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    // ------------------------------------------------------------------------------ logs

    [Fact]
    public async Task TheKeyNeverAppearsInTheLog()
    {
        await PostAsync(Valid);                    // right key
        await PostAsync(Valid, key: Key + "zz");   // wrong key
        await PostAsync("""{"group":"CAI5_AIS4_S7","date":"bad"}""");
        string log = string.Join("\n", _logs);

        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain(RecordingApiServer.KeyHeader, log, StringComparison.OrdinalIgnoreCase);
        // What it does record: the request's fields and each answer's status and duration.
        Assert.Contains("group=CAI5_AIS4_S7", log);
        Assert.Contains("-> 200 in", log);
        Assert.Contains("-> 401 in", log);
        Assert.Contains("-> 400 in", log);
    }
}

public sealed class RecordingApiOptionsTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;

    [Fact]
    public void WithoutAKeyTheApiDoesNotStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RecordingApiOptions.FromEnvironment(Env()));
        Assert.Contains(RecordingApiOptions.KeyVariable, ex.Message);
    }

    [Fact]
    public void AShortKeyIsRefusedAndNotRepeatedBack()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecordingApiOptions.FromEnvironment(Env((RecordingApiOptions.KeyVariable, "tooshort123"))));
        Assert.DoesNotContain("tooshort123", ex.Message);
    }

    [Fact]
    public void ThePortDefaultsAndCanBeChangedButNotToNonsense()
    {
        const string key = "a-long-enough-random-key-0001";
        Assert.Equal(RecordingApiOptions.DefaultPort,
            RecordingApiOptions.FromEnvironment(Env((RecordingApiOptions.KeyVariable, key))).Port);
        Assert.Equal(50123, RecordingApiOptions.FromEnvironment(
            Env((RecordingApiOptions.KeyVariable, key), (RecordingApiOptions.PortVariable, "50123"))).Port);
        Assert.Throws<InvalidOperationException>(() => RecordingApiOptions.FromEnvironment(
            Env((RecordingApiOptions.KeyVariable, key), (RecordingApiOptions.PortVariable, "80"))));
        Assert.Throws<InvalidOperationException>(() => RecordingApiOptions.FromEnvironment(
            Env((RecordingApiOptions.KeyVariable, key), (RecordingApiOptions.PortVariable, "abc"))));
    }

    [Fact]
    public void OnlyLoopbackAddressesAreListenedOn()
    {
        var options = RecordingApiOptions.ForTesting(47821, "a-long-enough-random-key-0001");
        Assert.All(options.Prefixes, prefix => Assert.Matches(@"^http://(127\.0\.0\.1|localhost):47821/$", prefix));
    }

    [Fact]
    public void KeysAreComparedExactly()
    {
        var options = RecordingApiOptions.ForTesting(47821, "a-long-enough-random-key-0001");
        Assert.True(options.KeyMatches("a-long-enough-random-key-0001"));
        Assert.False(options.KeyMatches("A-LONG-ENOUGH-RANDOM-KEY-0001"));
        Assert.False(options.KeyMatches("a-long-enough-random-key-000"));
        Assert.False(options.KeyMatches(""));
        Assert.False(options.KeyMatches(null));
    }
}
