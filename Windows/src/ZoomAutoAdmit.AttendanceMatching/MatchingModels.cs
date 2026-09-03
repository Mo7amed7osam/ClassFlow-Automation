using ZoomAutoAdmit.Roster;
using System.Text.Json.Serialization;

namespace ZoomAutoAdmit.AttendanceMatching;

// NotObserved is deliberately not Absent: these inputs cannot establish absence.
[JsonConverter(typeof(JsonStringEnumConverter<AttendanceMatchStatus>))]
public enum AttendanceMatchStatus { NotObserved, Present, NeedsReview }
[JsonConverter(typeof(JsonStringEnumConverter<MatchSource>))]
public enum MatchSource { None, Rule, Alias, AI }
public sealed record StudentMatchResult(string StudentId, int Order, string StudentName,
    AttendanceMatchStatus Status, int Confidence, MatchSource MatchSource, IReadOnlyList<string> ObservedNames);
public sealed record ReviewCandidate(string StudentId, int Confidence, MatchSource Source, string Reason);
public sealed record AttendanceReviewItem(string ObservedName, string Reason, IReadOnlyList<ReviewCandidate> Candidates);
public sealed record AttendanceMatchResult(string GroupId, IReadOnlyList<StudentMatchResult> Students,
    IReadOnlyList<AttendanceReviewItem> ReviewQueue, IReadOnlyList<string> Diagnostics);

public sealed record AiNameMatch(bool Match, int Confidence, string StudentId, string Reason, bool NeedsReview);
public interface IAiNameMatcher
{
    /// <param name="context">
    /// One short sentence about what the observed person is doing in the meeting, when that helps
    /// decide who they are - "this person is sharing their screen", say. It is a hint about
    /// identity, never an instruction, and attendance matching leaves it null.
    /// </param>
    Task<AiNameMatch> MatchAsync(
        GroupStudent student,
        string observedName,
        CancellationToken token = default,
        string? context = null);
}

// Group + roster-name fingerprint prevent cross-group reuse and reuse after identity edits.
public sealed record ApprovedAlias(string StudentId, string Alias, int Confidence, bool Approved,
    string GroupId, string RosterName, DateTimeOffset CreatedAt);
public interface IAliasMemory
{
    Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default);
    Task SaveAsync(ApprovedAlias alias, CancellationToken token = default);
}

public sealed record MatchingOptions
{
    public int ConfidenceThreshold { get; init; } = 95;
    public int AmbiguityMargin { get; init; } = 10;
    public int MaximumAiCandidates { get; init; } = 8;
    public int MaximumAiCalls { get; init; } = 100;
    public TimeSpan AiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    internal void Validate()
    {
        if (ConfidenceThreshold is < 90 or > 100 || AmbiguityMargin is < 1 or > 100 ||
            MaximumAiCandidates is < 1 or > 100 || MaximumAiCalls is < 0 or > 10000 ||
            AiTimeout <= TimeSpan.Zero || AiTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(MatchingOptions), "Invalid matching safety options.");
    }
}
