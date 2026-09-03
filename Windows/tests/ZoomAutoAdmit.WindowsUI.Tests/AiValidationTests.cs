using System.Net;
using System.Net.Http;
using System.Text.Json;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AiValidationTests
{
    [Fact]
    public async Task ConnectedServiceUsesRulesThenAiThenReusesApprovedAlias()
    {
        var text = JsonSerializer.Serialize(new { match = true, confidence = 96, studentId = "S1", reason = "Consistent shortened name", needsReview = false });
        var body = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
        using var handler = new Handler(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var service = new AiMatchingService(http, new MemoryAliases());
        var settings = new AiConnectionSettings("gpt-5.4-mini", "synthetic-key");
        var roster = new RosterGroup("G", "Group", DateTimeOffset.Now, [new("S1", "G", 1, "Mohab Osama Sayed Mohamed", [])]);
        var exact = await service.MatchAsync(settings, roster, ["Mohab Osama Sayed Mohamed"], default);
        Assert.Equal(MatchSource.Rule, Assert.Single(exact.Students).MatchSource); Assert.Equal(0, handler.Requests);
        var first = await service.MatchAsync(settings, roster, ["Mo7ab Mohamed"], default);
        Assert.Equal(MatchSource.AI, Assert.Single(first.Students).MatchSource); Assert.Equal(1, handler.Requests);
        var second = await service.MatchAsync(settings, roster, ["Mo7ab Mohamed"], default);
        Assert.Equal(MatchSource.Alias, Assert.Single(second.Students).MatchSource); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task BatchAiFailureReachesUiDiagnosticsWithoutMarkingAbsentOrLeakingBody()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.Unauthorized,
            "{\"error\":{\"code\":\"invalid_api_key\",\"message\":\"never-print-this-key\"}}"));
        var roster = new RosterGroup("G", "Group", DateTimeOffset.Now, [new("S1", "G", 1, "Mohab Osama Sayed Mohamed", [])]);
        var result = await new AiMatchingService(http, new MemoryAliases()).MatchAsync(new("gpt-5.4-mini", "synthetic"), roster, ["Mo7ab Mohamed"], default);
        Assert.NotEmpty(result.ReviewQueue);
        Assert.Contains(result.Diagnostics, d => d.Contains("Not valid"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("never-print-this-key"));
        Assert.NotEqual(AttendanceMatchStatus.Present, Assert.Single(result.Students).Status);
    }

    private sealed class MemoryAliases : IAliasMemory
    {
        private readonly List<ApprovedAlias> _aliases = [];
        public Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ApprovedAlias>>(_aliases.ToArray());
        public Task SaveAsync(ApprovedAlias alias, CancellationToken token = default) { _aliases.Add(alias); return Task.CompletedTask; }
    }
    [Theory]
    [InlineData(401, "invalid_api_key", "Not valid")]
    [InlineData(403, "request_failed", "forbidden")]
    [InlineData(404, "model_not_found", "model not found")]
    [InlineData(429, "insufficient_quota", "credit/quota")]
    [InlineData(429, "rate_limit_exceeded", "rate limit")]
    [InlineData(400, "unsupported_parameter", "HTTP 400")]
    [InlineData(500, "server_error", "service error")]
    public async Task RealHttpAdapterReportsDistinctSafeFailures(int status, string code, string expected)
    {
        using var handler = new Handler((HttpStatusCode)status, JsonSerializer.Serialize(new
        { error = new { code, message = "secret-key-must-not-appear" } }));
        using var client = new HttpClient(handler);
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(client)) { Model = "gpt-5.4-mini" };
        await vm.TestAndSaveAsync("secret-key-must-not-appear");
        Assert.Contains(expected, vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-key-must-not-appear", vm.Status);
        Assert.False(vm.IsReady); Assert.Null(store.Value);
        Assert.Equal("gpt-5.4-mini", handler.Model);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"status\":\"incomplete\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"output\":[]}")]
    [InlineData("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\"}]}]}")]
    public async Task Http200WithoutUsableAnswerNeverMarksReady(string body)
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, body));
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http));
        await vm.TestAndSaveAsync("synthetic-key");
        Assert.False(vm.IsReady); Assert.Null(store.Value); Assert.Contains("Response not valid", vm.Status);
    }

    [Fact]
    public async Task CompletedStructuredAnswerProvesAccessEvenWhenSyntheticMatchRequestsReview()
    {
        var text = JsonSerializer.Serialize(new { match = false, confidence = 50, studentId = "connection-test", reason = "Needs review", needsReview = true });
        var body = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, body));
        using var vm = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), new AiMatchingService(http));
        await vm.TestAndSaveAsync("synthetic-key");
        Assert.True(vm.IsReady); Assert.Contains("Valid", vm.Status);
    }

    [Fact]
    public async Task TypedKeyTakesPriorityAndSavedKeyWorksOnlyWhenInputIsEmpty()
    {
        var store = new AiSetupTests.MemoryStore { Value = new("old", "saved-key") };
        using var handler = new Handler(HttpStatusCode.Unauthorized, "{}");
        using var http = new HttpClient(handler);
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http));
        await vm.TestAndSaveAsync("typed-key"); Assert.Equal("typed-key", handler.Key);
        await vm.TestAndSaveAsync(null); Assert.Equal("saved-key", handler.Key);
        store.Value = null;
        await vm.TestAndSaveAsync(null);
        Assert.Equal(2, handler.Requests); Assert.Contains("No saved key exists", vm.Status);
    }

    [Fact]
    public async Task SlowResponseTimesOutWithoutSaving()
    {
        using var http = new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http));
        await vm.TestAndSaveAsync("synthetic-key");
        Assert.False(vm.IsReady); Assert.True(vm.IsIdle); Assert.Null(store.Value);
        Assert.Contains("timed out", vm.Status);
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Model, Key;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Key = request.Headers.Authorization?.Parameter;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Model = json.RootElement.GetProperty("model").GetString();
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            return new(status) { Content = new StringContent(body) };
        }
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    }
}
