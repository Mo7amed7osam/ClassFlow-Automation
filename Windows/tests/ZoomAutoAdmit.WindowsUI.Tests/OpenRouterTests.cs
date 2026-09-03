using System.Net;
using System.Net.Http;
using System.Text.Json;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class OpenRouterTests
{
    private static string Answer(string id = "connection-test", bool match = true) =>
        JsonSerializer.Serialize(new { match, confidence = match ? 96 : 40, studentId = id, reason = "Synthetic comparison", needsReview = !match });
    private static string Envelope(string answer, string finish = "stop") =>
        JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content = answer } } } });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProbeRequiresStructuredCompletionAndSavesSelectedProvider(bool match)
    {
        using var handler = new Handler(HttpStatusCode.OK, Envelope(Answer(match: match)));
        using var http = new HttpClient(handler);
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http)) { Provider = AiProvider.OpenRouter };
        await vm.TestAndSaveAsync("synthetic-router-key");
        Assert.True(vm.IsReady);
        Assert.Contains("OpenRouter", vm.Status);
        Assert.Equal(AiProvider.OpenRouter, store.Value!.Provider);
        Assert.Equal("openai/gpt-4.1-mini", store.Value.Model);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", handler.Url);
        Assert.Equal("synthetic-router-key", handler.Key);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("openai/gpt-4.1-mini", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Contains("connection-test", handler.Body);
        Assert.DoesNotContain("synthetic-router-key", handler.Body + vm.Status + store.Value);
    }

    [Theory]
    [InlineData(401, "Not valid")]
    [InlineData(402, "credits")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "model not found")]
    [InlineData(429, "rate limit")]
    [InlineData(400, "structured JSON")]
    [InlineData(503, "service error")]
    [InlineData(302, "no successful response")]
    public async Task ProviderErrorsNeverSaveOrLeakRawResponse(int status, string expected)
    {
        using var handler = new Handler((HttpStatusCode)status, JsonSerializer.Serialize(new
        { error = new { code = status, message = "secret-body-do-not-show" } }));
        using var http = new HttpClient(handler);
        var store = new AiSetupTests.MemoryStore { Value = new("old-model", "old-key") };
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http)) { Provider = AiProvider.OpenRouter };
        await vm.TestAndSaveAsync("synthetic-router-key");
        Assert.False(vm.IsReady); Assert.True(vm.IsIdle);
        Assert.Contains(expected, vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-body-do-not-show", vm.Status);
        Assert.Equal("old-key", store.Value!.ApiKey);
        // A 404 is retried once without the endpoint-parameter demand; every other failure is reported after one request.
        Assert.Equal(status == 404 ? 2 : 1, handler.Requests);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("wrong-id")]
    [InlineData("invalid-schema")]
    [InlineData("refusal")]
    [InlineData("too-large")]
    [InlineData("embedded-error")]
    public async Task Http200IsNotEnoughToProveUsableModel(string problem)
    {
        var body = problem switch
        {
            "empty" => "{\"choices\":[]}",
            "malformed" => "not-json",
            "truncated" => Envelope(Answer(), "length"),
            "wrong-id" => Envelope(Answer("wrong-student")),
            "invalid-schema" => Envelope("{\"match\":true}"),
            "refusal" => "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"secret-body\"}}]}",
            "too-large" => new string(' ', 65537),
            _ => "{\"error\":{\"code\":402,\"message\":\"secret-body\"}}"
        };
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, body));
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http)) { Provider = AiProvider.OpenRouter };
        await vm.TestAndSaveAsync("synthetic-router-key");
        Assert.False(vm.IsReady); Assert.Null(store.Value);
        Assert.DoesNotContain("secret-body", vm.Status);
        if (problem == "embedded-error") Assert.Contains("credits", vm.Status);
    }

    [Fact]
    public async Task ProviderSwitchRequiresFreshConsentAndCannotReuseOtherProviderSecret()
    {
        var store = new AiSetupTests.MemoryStore();
        var service = new AiSetupTests.FakeAi();
        using var vm = new AiMatchingViewModel(store, service);
        await vm.TestAndSaveAsync("synthetic-openai-key");
        vm.AllowExternalMatching = true;
        vm.Provider = AiProvider.OpenRouter;
        Assert.False(vm.IsReady); Assert.False(vm.AllowExternalMatching); Assert.False(vm.HasSavedKey);
        await vm.TestAndSaveAsync(null);
        Assert.Equal(1, service.Tests);
        Assert.Contains("belongs to OpenAI", vm.Status);
        vm.RemoveKeyCommand.Execute(null);
        Assert.NotNull(store.Value);
        await vm.TestAndSaveAsync("synthetic-router-key");
        using var next = new AiMatchingViewModel(store, service);
        next.LoadSavedSettings();
        Assert.Equal(AiProvider.OpenRouter, next.Provider);
        Assert.Equal("openai/gpt-4.1-mini", next.Model);
        Assert.True(next.IsReady); Assert.True(next.HasSavedKey);
        Assert.Contains("OpenRouter", next.ConnectionLabel);
        await next.TestAndSaveAsync(null);
        Assert.True(next.IsReady);
        Assert.Equal("synthetic-router-key", service.LastKey);
    }

    [Fact]
    public async Task WrongProviderAndUnqualifiedModelNeverSendARequest()
    {
        var service = new AiSetupTests.FakeAi();
        using var vm = new AiMatchingViewModel(new AiSetupTests.MemoryStore(), service);
        await vm.TestAndSaveAsync("sk-or-synthetic-not-a-real-key");
        Assert.Contains("Wrong provider", vm.Status); Assert.Equal(0, service.Tests);
        vm.Provider = AiProvider.OpenRouter; vm.Model = "gpt-4.1-mini";
        await vm.TestAndSaveAsync("synthetic-router-key");
        Assert.Contains("full model ID", vm.Status); Assert.Equal(0, service.Tests);
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => new AiMatchingService(http).TestAsync(new("model", "sk-or-synthetic"), default));
    }

    [Fact]
    public async Task ExistingRuleAiAliasPipelineWorksWithRouterWithoutChangingAdmission()
    {
        using var handler = new Handler(HttpStatusCode.OK, Envelope(Answer("S1")));
        using var http = new HttpClient(handler);
        var service = new AiMatchingService(http, new Aliases());
        var settings = new AiConnectionSettings("openai/gpt-4.1-mini", "synthetic-key", AiProvider.OpenRouter);
        var roster = new RosterGroup("G", "Group", DateTimeOffset.Now, [new("S1", "G", 1, "Mohab Osama Sayed Mohamed", [])]);
        var exact = await service.MatchAsync(settings, roster, ["Mohab Osama Sayed Mohamed"], default);
        Assert.Equal(MatchSource.Rule, Assert.Single(exact.Students).MatchSource); Assert.Equal(0, handler.Requests);
        var uncertain = await service.MatchAsync(settings, roster, ["Mo7ab Mohamed"], default);
        Assert.Equal(MatchSource.AI, Assert.Single(uncertain.Students).MatchSource); Assert.Equal(1, handler.Requests);
        var reuse = await service.MatchAsync(settings, roster, ["Mo7ab Mohamed"], default);
        Assert.Equal(MatchSource.Alias, Assert.Single(reuse.Students).MatchSource); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void SecureStoreRoundTripsProviderAndLegacyConstructorDefaultsToOpenAi()
    {
        Assert.Equal(AiProvider.OpenAI, new AiConnectionSettings("legacy-model", "synthetic-key").Provider);
        var store = new AiCredentialStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid().ToString("N"));
        try
        {
            store.Save(new("openai/gpt-4.1-mini", "synthetic-key", AiProvider.OpenRouter));
            var saved = store.Read()!;
            Assert.Equal(AiProvider.OpenRouter, saved.Provider);
            Assert.Equal("openai/gpt-4.1-mini", saved.Model);
            Assert.Equal("synthetic-key", saved.ApiKey);
            Assert.DoesNotContain("synthetic-key", saved.ToString());
        }
        finally { store.Delete(); }
    }

    [Fact]
    public async Task CancellationStopsRouterRequestWithoutSaving()
    {
        using var http = new HttpClient(new SlowHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http)) { Provider = AiProvider.OpenRouter };
        await vm.TestAndSaveAsync("synthetic-router-key");
        Assert.Contains("timed out", vm.Status); Assert.True(vm.IsIdle); Assert.False(vm.IsReady); Assert.Null(store.Value);
    }

    [Fact]
    public async Task ModelHiddenByEndpointParameterFilteringSucceedsOnTheAutomaticRetry()
    {
        using var handler = new SequenceHandler(
            (HttpStatusCode.NotFound, JsonSerializer.Serialize(new { error = new { code = 404, message = "No endpoints found matching your data policy." } })),
            (HttpStatusCode.OK, Envelope(Answer())));
        using var http = new HttpClient(handler);
        var store = new AiSetupTests.MemoryStore();
        using var vm = new AiMatchingViewModel(store, new AiMatchingService(http))
        { Provider = AiProvider.OpenRouter, Model = "openai/gpt-5.4-mini" };

        await vm.TestAndSaveAsync("synthetic-router-key");

        Assert.True(vm.IsReady);
        Assert.Equal("openai/gpt-5.4-mini", store.Value!.Model);
        Assert.Equal(2, handler.Bodies.Count);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        Assert.True(first.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.False(second.RootElement.TryGetProperty("provider", out _));
        // The retry drops only the endpoint demand: the strict schema and the model still stand.
        Assert.True(second.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("openai/gpt-5.4-mini", second.RootElement.GetProperty("model").GetString());
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _index;
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(token));
            var next = responses[Math.Min(_index++, responses.Length - 1)];
            return new(next.Status) { Content = new StringContent(next.Body) };
        }
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string Url = "", Key = "", Body = ""; public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; Url = request.RequestUri!.AbsoluteUri; Key = request.Headers.Authorization!.Parameter!;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(status) { Content = new StringContent(body) };
        }
    }
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    }
    private sealed class Aliases : IAliasMemory
    {
        private readonly List<ApprovedAlias> _items = [];
        public Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ApprovedAlias>>(_items.ToArray());
        public Task SaveAsync(ApprovedAlias alias, CancellationToken token = default) { _items.Add(alias); return Task.CompletedTask; }
    }
}
