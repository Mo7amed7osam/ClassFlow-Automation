using System.Net;
using System.Net.Http;
using System.Text.Json;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class AiSetupTests
{
    [Fact]
    public async Task KeyIsSavedOnlyAfterSuccessfulTestAndReusedWithoutRedisplay()
    {
        var store = new MemoryStore(); var service = new FakeAi();
        using var vm = new AiMatchingViewModel(store, service);
        await vm.TestAndSaveAsync("synthetic-key");
        Assert.True(vm.IsReady); Assert.Contains("Done", vm.Status);
        Assert.Equal("synthetic-key", store.Value!.ApiKey);
        Assert.DoesNotContain("synthetic-key", vm.Status + store.Value);
        using var next = new AiMatchingViewModel(store, service);
        next.LoadSavedSettings();
        // The stored key was verified before it was saved, so a restart comes back connected.
        Assert.True(next.IsReady);
        Assert.StartsWith("Connected", next.ConnectionLabel);
        Assert.Equal("Success", next.Severity);
        await next.TestAndSaveAsync(null);
        Assert.True(next.IsReady); Assert.Equal(2, service.Tests);
        next.RemoveKeyCommand.Execute(null);
        Assert.Null(store.Value); Assert.False(next.IsReady);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("schema")]
    [InlineData("cancel")]
    public async Task FailureDoesNotOverwriteOldCredentialOrExposeExceptionText(string failure)
    {
        var store = new MemoryStore { Value = new("old-model", "old-key") };
        var service = new FakeAi { Failure = failure switch { "network" => new HttpRequestException("synthetic-secret"), "schema" => new InvalidDataException("synthetic-secret"), _ => new OperationCanceledException("synthetic-secret") } };
        using var vm = new AiMatchingViewModel(store, service);
        await vm.TestAndSaveAsync("new-key");
        Assert.False(vm.IsReady); Assert.True(vm.IsIdle);
        Assert.Equal("old-key", store.Value!.ApiKey);
        Assert.DoesNotContain("synthetic-secret", vm.Status);
    }

    [Fact]
    public async Task SaveFailureCannotShowDone()
    {
        using var vm = new AiMatchingViewModel(new MemoryStore { FailSave = true }, new FakeAi());
        await vm.TestAndSaveAsync("fake-key");
        Assert.False(vm.IsReady); Assert.DoesNotContain("Done", vm.Status);
    }

    [Fact]
    public async Task MatchingRequiresTestAndConsentAndDisplaysOrderedResults()
    {
        var ai = new FakeAi(); using var vm = new AiMatchingViewModel(new MemoryStore(), ai);
        vm.SelectedGroup = new("g", "Group", DateTimeOffset.UtcNow, [new("1", "g", 1, "Example Student", [])]);
        vm.ObservedNames = "Example Student";
        await vm.MatchAsync(); Assert.Equal(0, ai.Matches);
        await vm.TestAndSaveAsync("fake-key");
        await vm.MatchAsync(); Assert.Equal(0, ai.Matches);
        vm.AllowExternalMatching = true;
        await vm.MatchAsync(); Assert.Equal(1, ai.Matches); Assert.Single(vm.Results); Assert.Contains("Done", vm.Status);
        vm.Model = "changed"; Assert.False(vm.IsReady);
    }

    [Fact]
    public async Task ProductionProbeUsesSyntheticDataAndExistingStructuredMatcher()
    {
        using var handler = new ProbeHandler(); using var http = new HttpClient(handler);
        await new AiMatchingService(http).TestAsync(new("test-model", "test-secret"), CancellationToken.None);
        Assert.Equal("https://api.openai.com/v1/responses", handler.Url);
        Assert.Contains("connection-test", handler.Body);
        Assert.Contains("json_schema", handler.Body);
        Assert.DoesNotContain("test-secret", handler.Body);
    }

    [Fact]
    public void WindowsCredentialManagerRoundTripUsesIsolatedDisposableTestTarget()
    {
        var store = new AiCredentialStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(store.Read());
            store.Save(new("test-model", "synthetic-not-a-real-key"));
            Assert.Equal("synthetic-not-a-real-key", store.Read()!.ApiKey);
            store.Delete(); Assert.Null(store.Read());
        }
        finally { store.Delete(); }
    }

    internal sealed class MemoryStore : IAiCredentialStore
    {
        public AiConnectionSettings? Value; public bool FailSave;
        public AiConnectionSettings? Read() => Value;
        public void Save(AiConnectionSettings settings) { if (FailSave) throw new IOException(); Value = settings; }
        public void Delete() => Value = null;
    }
    internal sealed class FakeAi : IAiMatchingService
    {
        public Exception? Failure; public int Tests, Matches; public string? LastKey;
        public Task TestAsync(AiConnectionSettings settings, CancellationToken token) { Tests++; LastKey = settings.ApiKey; return Failure == null ? Task.CompletedTask : Task.FromException(Failure); }
        public Task<AttendanceMatchResult> MatchAsync(AiConnectionSettings settings, RosterGroup group, IReadOnlyList<string> names, CancellationToken token)
        { Matches++; return Task.FromResult(new AttendanceMatchResult(group.GroupId, group.Students.Select(s => new StudentMatchResult(s.StudentId, s.Order, s.FullName, AttendanceMatchStatus.Present, 99, MatchSource.Rule, names)).ToArray(), [], [])); }
        public int RulesOnlyMatches;
        // The real engine with no AI, so a rules-only run behaves here exactly as it does in the app.
        public Task<AttendanceMatchResult> MatchWithRulesOnlyAsync(RosterGroup group, IReadOnlyList<string> names, CancellationToken token)
        { RulesOnlyMatches++; return new AttendanceMatchingEngine(new NoAliases(), ai: null, log: _ => { }).MatchAsync(group, names, token); }
    }
    internal sealed class NoAliases : IAliasMemory
    {
        public Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ApprovedAlias>>([]);
        public Task SaveAsync(ApprovedAlias alias, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class ProbeHandler : HttpMessageHandler
    {
        public string Body = "", Url = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Url = request.RequestUri!.AbsoluteUri; Body = await request.Content!.ReadAsStringAsync(token);
            var match = JsonSerializer.Serialize(new { match = true, confidence = 99, studentId = "connection-test", reason = "Synthetic test", needsReview = false });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = match } } } } })) };
        }
    }
}
