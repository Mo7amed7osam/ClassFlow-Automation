using System.Net.Http;
using System.IO;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Roster;

namespace ZoomAutoAdmit.WindowsUI.Services;

public interface IAiMatchingService
{
    Task TestAsync(AiConnectionSettings settings, CancellationToken token);
    Task<AttendanceMatchResult> MatchAsync(AiConnectionSettings settings, RosterGroup group, IReadOnlyList<string> names, CancellationToken token);
    /// <summary>
    /// The same match with no AI in the loop: name rules and approved aliases only. Attendance is
    /// readable without a key, and nothing leaves the machine; uncertain names go to review.
    /// </summary>
    Task<AttendanceMatchResult> MatchWithRulesOnlyAsync(RosterGroup group, IReadOnlyList<string> names, CancellationToken token);
    /// <summary>One "is this observed name this person?" question, with the same validated schema.</summary>
    Task<AiNameMatch> CompareAsync(
        AiConnectionSettings settings,
        GroupStudent person,
        string observedName,
        CancellationToken token,
        string? context = null) => throw new NotSupportedException();
}

public sealed class AiMatchingService : IAiMatchingService
{
    // No redirects: bearer secrets go only to the explicitly selected provider's fixed endpoint.
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _client;
    private readonly IAliasMemory _aliases;
    private readonly IAiDecisionMemory? _decisions;
    /// <summary>Where AI answers are remembered by default (tests replace it, so they never touch this PC's file).</summary>
    public static Func<IAiDecisionMemory?> DefaultDecisions { get; set; } = () => new JsonAiDecisionMemory();
    public AiMatchingService(HttpClient? client = null, IAliasMemory? aliases = null, IAiDecisionMemory? decisions = null)
    { _client = client ?? Client; _aliases = aliases ?? new JsonAliasMemory(); _decisions = decisions ?? DefaultDecisions(); }

    public async Task TestAsync(AiConnectionSettings settings, CancellationToken token)
    {
        var matcher = CreateMatcher(settings);
        // A completed, schema-valid answer proves model access, even if it requests review.
        // A name-match confidence score is not evidence that an API key is valid/invalid.
        await matcher.MatchAsync(new GroupStudent("connection-test", "test", 1, "Test Student Example", []), "Test Student Example", token);
    }

    public async Task<AttendanceMatchResult> MatchAsync(AiConnectionSettings settings, RosterGroup group, IReadOnlyList<string> names, CancellationToken token)
    {
        var errors = new HashSet<string>();
        var matcher = new ReportingMatcher(CreateMatcher(settings), errors, settings.Provider);
        var result = await new AttendanceMatchingEngine(_aliases, matcher, log: ConsoleLogger.Info, decisions: _decisions).MatchAsync(group, names, token);
        return result with { Diagnostics = result.Diagnostics.Concat(errors).ToArray() };
    }

    public Task<AttendanceMatchResult> MatchWithRulesOnlyAsync(RosterGroup group, IReadOnlyList<string> names, CancellationToken token) =>
        new AttendanceMatchingEngine(_aliases, ai: null, log: ConsoleLogger.Info).MatchAsync(group, names, token);

    public Task<AiNameMatch> CompareAsync(
        AiConnectionSettings settings,
        GroupStudent person,
        string observedName,
        CancellationToken token,
        string? context = null) =>
        CreateMatcher(settings).MatchAsync(person, observedName, token, context);

    private IAiNameMatcher CreateMatcher(AiConnectionSettings settings)
    {
        if (settings.Provider == AiProvider.OpenAI && settings.ApiKey.StartsWith("sk-or-", StringComparison.Ordinal))
            throw new ArgumentException("Select OpenRouter for this key.");
        return settings.Provider switch
        {
            AiProvider.OpenAI => new OpenAiNameMatcher(_client, settings.Model, () => settings.ApiKey),
            AiProvider.OpenRouter => new OpenRouterNameMatcher(_client, settings.Model, () => settings.ApiKey),
            _ => throw new ArgumentException("Invalid AI provider.")
        };
    }

    private sealed class ReportingMatcher(IAiNameMatcher inner, HashSet<string> errors, AiProvider provider) : IAiNameMatcher
    {
        public async Task<AiNameMatch> MatchAsync(
            GroupStudent student,
            string observedName,
            CancellationToken token = default,
            string? context = null)
        {
            try { return await inner.MatchAsync(student, observedName, token); }
            catch (Exception ex)
            {
                errors.Add(AiConnectionErrors.Describe(ex, provider));
                throw;
            }
        }
    }
}
