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
/// browser, Zoom or the dashboard. Drive file ids are made up.
/// </summary>
public sealed class RecordingApiServerTests : IAsyncLifetime
{
    private const string Key = "test-key-0123456789-abcdefghij";
    private const string DriveLink = "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing";

    private readonly FakeProcessor _processor = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private RecordingApiServer _server = null!;
    private HttpClient _http = null!;

    private sealed class FakeProcessor : IRecordingLinkProcessor
    {
        public readonly ConcurrentQueue<ProvidedRecordLinkRequest> Given = new();
        public int ZoomSearches;
        public Func<ProvidedRecordLinkRequest, Task<RecordingLinkOutcome>> Answer =
            request => Task.FromResult(Outcome(request, RecordingLinkStatus.Attached, "saved"));

        public Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ZoomSearches);
            throw new InvalidOperationException("the API must not search Zoom");
        }

        public Task<RecordingLinkOutcome> AttachProvidedLinkAsync(ProvidedRecordLinkRequest request, CancellationToken cancellationToken)
        {
            Given.Enqueue(request);
            return Answer(request);
        }
    }

    private static RecordingLinkOutcome Outcome(ProvidedRecordLinkRequest request, RecordingLinkStatus status, string message,
        string? reason = null) =>
        new(status, message)
        {
            Group = request.Group,
            Date = request.Date ?? new DateOnly(2026, 9, 3),
            StartTime = request.StartTime,
            Profile = RecordingLinkProcessor.DashboardProfile,
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
        _server = new RecordingApiServer(RecordingApiOptions.ForTesting(FreePort(), Key), _processor, _logs.Enqueue);
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

    private static string Body(string group = "AST5_DAT1_S1", string link = DriveLink, string? date = "2026-09-03", string extra = "") =>
        $$"""{"group":"{{group}}","recordLink":"{{link}}"{{(date == null ? "" : $",\"date\":\"{date}\"")}}{{extra}}}""";

    // ------------------------------------------------------------------------------ health

    [Fact]
    public async Task HealthAnswersWithoutAKey()
    {
        using var response = await _http.GetAsync(RecordingApiServer.HealthPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        Assert.Empty(_processor.Given);
    }

    // ------------------------------------------------------------------------------ authentication

    [Fact]
    public async Task AMissingKeyIsRefused()
    {
        var (status, body) = await PostAsync(Body(), key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("Unauthorized", body.GetProperty("error").GetString());
        Assert.Empty(_processor.Given);
    }

    [Fact]
    public async Task AWrongKeyIsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Body(), key: Key + "x")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Body(), key: "short")).Status);
        Assert.Empty(_processor.Given);
    }

    [Fact]
    public async Task TheKeyIsCheckedBeforeTheBodyIsRead()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("not json at all", key: null)).Status);
    }

    // ------------------------------------------------------------------------------ the happy path

    [Fact]
    public async Task AGoogleDriveLinkIsAttachedExactlyAsSentAndZoomIsNeverSearched()
    {
        // The body n8n sends: group, the sheet's Drive link, the date, replaceExisting.
        var (status, body) = await PostAsync(
            $$"""{"group":"  AST5_DAT1_S1 ","recordLink":"{{DriveLink}}","date":"2026-09-03","replaceExisting":false}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("Recording link attached successfully.", body.GetProperty("message").GetString());
        Assert.False(body.GetProperty("alreadyExists").GetBoolean());
        Assert.Equal("AST5_DAT1_S1", body.GetProperty("group").GetString());
        Assert.Equal("2026-09-03", body.GetProperty("date").GetString());
        Assert.False(body.TryGetProperty("startTime", out _));      // not sent, so not answered

        var given = Assert.Single(_processor.Given);
        Assert.Equal("AST5_DAT1_S1", given.Group);                 // trimmed
        Assert.Equal(DriveLink, given.RecordLink);                  // character for character
        Assert.Equal(new DateOnly(2026, 9, 3), given.Date);
        Assert.Null(given.StartTime);                               // never worked out
        Assert.False(given.ReplaceExisting);
        Assert.Equal(0, _processor.ZoomSearches);
    }

    [Fact]
    public async Task SurroundingSpacesAreTheOnlyThingRemovedFromTheLink()
    {
        await PostAsync($$"""{"group":"AST5_DAT1_S1","recordLink":"  {{DriveLink}}  "}""");
        Assert.Equal(DriveLink, Assert.Single(_processor.Given).RecordLink);
    }

    [Theory]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing")]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view")]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/preview")]
    [InlineData("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("https://DRIVE.GOOGLE.COM/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view")]
    public async Task EveryShapeOfADriveFileLinkIsAccepted(string link)
    {
        var (status, _) = await PostAsync(Body(link: link));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(link, Assert.Single(_processor.Given).RecordLink);
    }

    [Fact]
    public async Task DateReplaceExistingAndDryRunAreOptional()
    {
        var (status, _) = await PostAsync(Body(date: null));
        Assert.Equal(HttpStatusCode.OK, status);
        var given = Assert.Single(_processor.Given);
        Assert.Null(given.Date);            // today, decided by the workflow
        Assert.False(given.ReplaceExisting);
        Assert.False(given.DryRun);
    }

    [Fact]
    public async Task AnOlderNodeThatStillSendsProfileKeepsWorking()
    {
        var (status, _) = await PostAsync(Body(extra: ",\"profile\":\"default\""));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ------------------------------------------------------------------------------ validation

    [Theory]
    [InlineData("""{"recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "'group' is required")]
    [InlineData("""{"group":"  ","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "'group' is required")]
    [InlineData("""{"group":"AST5<script>","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "'group' may contain only")]
    [InlineData("""{"group":"AST5_DAT1_S1"}""", "'recordLink' is required")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":""}""", "'recordLink' is required")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"   "}""", "'recordLink' is required")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":null}""", "'recordLink' is required")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":42}""", "'recordLink' must be a string")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"not a url"}""", "spaces")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"}""", "not a valid URL")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"http://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "must use https")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"C:\\Recordings\\AST5_DAT1_S1.mp4"}""", "file path")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"\\\\server\\share\\AST5.mp4"}""", "file path")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"file:///C:/Recordings/AST5.mp4"}""", "file path")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz012345"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/uc?id=1AbCdEfGhIjKlMnOpQrStUvWxYz012345&export=download"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com.evil.example/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://docs.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/short/view"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://user:pw@drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com:8443/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://zoom.us/rec/share/abc.def?startTime=1788278291000"}""", "Google Drive link to one file")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view","date":"03/09/2026"}""", "yyyy-MM-dd")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view","date":""}""", "'date' is empty")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view","replaceExisting":"no"}""", "must be true or false")]
    [InlineData("""{"group":"AST5_DAT1_S1","link":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "as 'recordLink'")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view","fileName":"AST5_DAT1_S1_2026-09-03_1550.mp4"}""", "never downloaded or uploaded")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordLink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view","timeZone":"utc"}""", "no longer used")]
    [InlineData("""{"group":"AST5_DAT1_S1","recordlink":"https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view"}""", "'recordlink' is not a known field")]
    [InlineData("""["AST5_DAT1_S1"]""", "JSON object")]
    [InlineData("""{"group":""", "JSON object")]
    public async Task ABadRequestSaysWhatIsWrongAndReachesNothing(string json, string expected)
    {
        var (status, body) = await PostAsync(json);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid request", body.GetProperty("error").GetString());
        Assert.Contains(expected, body.GetProperty("details").GetString());
        Assert.Empty(_processor.Given);
        Assert.Equal(0, _processor.ZoomSearches);
    }

    // ------------------------------------------------------------------------------ outcomes

    [Fact]
    public async Task AnExistingLinkIsASuccessThatSaysSo()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.AlreadyExists,
            "AST5_DAT1_S1: the session already has a recording link, so it was left as it is."));
        var (status, body) = await PostAsync(Body());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.True(body.GetProperty("alreadyExists").GetBoolean());
        Assert.Contains("already has a recording link", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReplaceExistingIsPassedOnExactlyAsSent()
    {
        await PostAsync(Body(extra: ",\"replaceExisting\":true"));
        await PostAsync(Body(extra: ",\"replaceExisting\":false"));
        await PostAsync(Body());
        Assert.Equal([true, false, false], _processor.Given.Select(request => request.ReplaceExisting));
    }

    [Fact]
    public async Task ADashboardFailureIs500WithAReasonToActOn()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.LmsFailed,
            "The session page offers no Add Record Link; it reads \"running\".", "sessionNotFinished"));
        var (status, body) = await PostAsync(Body());
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("LMS operation failed", body.GetProperty("error").GetString());
        Assert.Equal("sessionNotFinished", body.GetProperty("reason").GetString());
        Assert.Contains("\"running\"", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ABusyDashboardProfileIs409()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.Busy, "in use"));
        var (status, body) = await PostAsync(Body());
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("Busy", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ADryRunIsReportedAsOne()
    {
        _processor.Answer = request => Task.FromResult(Outcome(request, RecordingLinkStatus.DryRun, "box open, nothing saved"));
        var (status, body) = await PostAsync(Body(extra: ",\"dryRun\":true"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.True(Assert.Single(_processor.Given).DryRun);
    }

    [Fact]
    public async Task AnUnexpectedErrorIs500WithoutItsDetails()
    {
        _processor.Answer = _ => throw new InvalidOperationException(@"C:\Users\someone\secret-path and a cookie=abc");
        var (status, body) = await PostAsync(Body());
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("Internal error", body.GetProperty("error").GetString());
        Assert.DoesNotContain("secret-path", body.GetRawText());
        Assert.DoesNotContain("secret-path", string.Join("\n", _logs));
    }

    [Fact]
    public async Task UnknownPathsAndWrongMethodsAreRefused()
    {
        using var unknown = await _http.GetAsync("/api/recordings/other");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrongMethod = await _http.GetAsync(RecordingApiServer.ProcessPath);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Empty(_processor.Given);
    }

    // ------------------------------------------------------------------------------ concurrency

    [Fact]
    public async Task RequestsArriveTogetherAndAreAllAnswered()
    {
        // The server takes them in parallel; the workflow's dashboard lock is what serialises the
        // browser work (RecordingLinkProcessorTests). None may be dropped or mixed up.
        int running = 0, peak = 0;
        _processor.Answer = async request =>
        {
            int now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
            return Outcome(request, RecordingLinkStatus.Attached, "saved");
        };
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => PostAsync(Body(group: $"GROUP_{i}"))));

        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.Status));
        Assert.Equal(["GROUP_1", "GROUP_2", "GROUP_3", "GROUP_4"],
            results.Select(result => result.Body.GetProperty("group").GetString()).Order());
        Assert.True(peak > 1, "the server handled requests one at a time");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    // ------------------------------------------------------------------------------ logs

    [Fact]
    public async Task NeitherTheKeyNorTheWholeDriveLinkAppearsInTheLog()
    {
        await PostAsync(Body());                      // right key
        await PostAsync(Body(), key: Key + "zz");     // wrong key
        await PostAsync(Body(date: "bad"));
        string log = string.Join("\n", _logs);

        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain(RecordingApiServer.KeyHeader, log, StringComparison.OrdinalIgnoreCase);
        // A Drive share link opens the recording to anyone who has it: the log keeps a preview.
        Assert.DoesNotContain("1AbCdEfGhIjKlMnOpQrStUvWxYz012345", log);
        Assert.Contains("recordLink=drive.google.com/file/d/1AbCdE...", log);
        Assert.Contains("group=AST5_DAT1_S1", log);
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
