using ZoomAutoAdmit.Roster;
using Xunit;

namespace ZoomAutoAdmit.AttendanceMatching.Tests;

public class MatchingTests
{
    internal sealed class Memory : IAliasMemory
    {
        public List<ApprovedAlias> Aliases = [];
        public Task<IReadOnlyList<ApprovedAlias>> LoadAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<ApprovedAlias>>(Aliases.ToArray());
        public Task SaveAsync(ApprovedAlias alias, CancellationToken token = default) { Aliases.Add(alias); return Task.CompletedTask; }
    }
    internal sealed class Ai(Func<GroupStudent, string, AiNameMatch> answer) : IAiNameMatcher
    {
        public int Calls;
        public Task<AiNameMatch> MatchAsync(
            GroupStudent student,
            string observedName,
            CancellationToken token = default,
            string? context = null)
        { Calls++; return Task.FromResult(answer(student, observedName)); }
    }
    internal static GroupStudent Student(string id = "S1", string name = "Mohab Osama Sayed Mohamed", int order = 1,
        string group = "G") => new(id, group, order, name, []);
    internal static RosterGroup Group(params GroupStudent[] students) => new("G", "Group", DateTimeOffset.UtcNow, students);
    private static AttendanceMatchingEngine Engine(Memory? memory = null, IAiNameMatcher? ai = null, MatchingOptions? options = null) =>
        new(memory ?? new(), ai, options, _ => { });

    [Theory]
    [InlineData("Mohab Osama Sayed Mohamed", "Mohab Osama Sayed Mohamed", 100)]
    [InlineData("MOHAB  Osama", "Mohab Osama Sayed Mohamed", 95)]
    [InlineData("Mohab Osama Sayed", "Mohab Osama Sayed Mohamed", 98)]
    [InlineData("Mo7ab Osama", "Mohab Osama Sayed Mohamed", 95)]
    [InlineData("أَحْمَد مُحَمَّد عَلي", "احمد محمد علي", 100)]
    [InlineData("مُهاب أُسامة", "Mohab Osama Sayed Mohamed", 95)]
    [InlineData("José Luis", "Jose Luis Silva", 95)]
    public async Task ExactPrefixArabiziAndArabicMatches(string observed, string rosterName, int score)
    {
        var result = await Engine().MatchAsync(Group(Student(name: rosterName)), [observed]);
        var row = Assert.Single(result.Students);
        Assert.Equal(AttendanceMatchStatus.Present, row.Status);
        Assert.Equal(score, row.Confidence);
        Assert.Equal(MatchSource.Rule, row.MatchSource);
        Assert.Empty(result.ReviewQueue);
    }

    [Theory]
    [InlineData("Mohab")]
    [InlineData("Mohamed")]
    [InlineData("Random Mohamed")]
    public async Task FirstNameOrFamilyOnlyCannotBeConfirmedEvenByOverconfidentAi(string observed)
    {
        var memory = new Memory();
        var ai = new Ai((s, _) => new(true, 99, s.StudentId, "Looks similar", false));
        var result = await Engine(memory, ai).MatchAsync(Group(Student()), [observed]);
        Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students[0].Status);
        Assert.Single(result.ReviewQueue);
        Assert.Empty(memory.Aliases);
        Assert.Equal(1, ai.Calls);
    }

    [Fact]
    public async Task SharedPrefixIsNotResolvedByRosterPositionOrAiGuess()
    {
        var ai = new Ai((s, _) => new(true, 100, s.StudentId, "Guess", false));
        var result = await Engine(ai: ai).MatchAsync(Group(Student("B", "Ahmed Mohamed Ali", 9),
            Student("A", "Ahmed Mohamed Hassan", 2)), ["Ahmed Mohamed"]);
        Assert.Equal(new[] { "A", "B" }, result.Students.Select(s => s.StudentId));
        Assert.All(result.Students, s => Assert.Equal(AttendanceMatchStatus.NeedsReview, s.Status));
        Assert.Equal(2, Assert.Single(result.ReviewQueue).Candidates.Count);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task DuplicateFullNamesAreAmbiguousEvenWithTwoObservations()
    {
        var result = await Engine().MatchAsync(Group(Student(), Student("S2", order: 2)),
            ["Mohab Osama Sayed Mohamed", "Mohab Osama Sayed Mohamed"]);
        Assert.All(result.Students, s => Assert.Equal(AttendanceMatchStatus.NeedsReview, s.Status));
        Assert.Single(result.ReviewQueue);
    }

    [Fact]
    public async Task AiConfirmationIsRememberedAndReusedBeforeCallingAi()
    {
        var memory = new Memory();
        var ai = new Ai((s, _) => new(true, 96, s.StudentId, "First and family names with Arabizi", false));
        var first = await Engine(memory, ai).MatchAsync(Group(Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(MatchSource.AI, first.Students[0].MatchSource);
        var alias = Assert.Single(memory.Aliases);
        Assert.True(alias.Approved);
        Assert.Equal("Mo7ab Mohamed", alias.Alias);
        var second = await Engine(memory, ai).MatchAsync(Group(Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(MatchSource.Alias, second.Students[0].MatchSource);
        Assert.Equal(96, second.Students[0].Confidence);
        Assert.Equal(1, ai.Calls);
    }

    [Theory]
    [InlineData(false, 99, "G", "mohab osama sayed mohamed")]
    [InlineData(true, 94, "G", "mohab osama sayed mohamed")]
    [InlineData(true, 99, "Other", "mohab osama sayed mohamed")]
    [InlineData(true, 99, "G", "old roster name")]
    public async Task UnapprovedWeakWrongGroupOrStaleAliasesAreNotUsed(bool approved, int score, string group, string name)
    {
        var memory = new Memory();
        memory.Aliases.Add(new("S1", "Mo7ab Mohamed", score, approved, group, name, DateTimeOffset.UtcNow));
        var result = await Engine(memory).MatchAsync(Group(Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students[0].Status);
    }

    [Fact]
    public async Task AliasDoesNotOverrideConflictingRosterIdentity()
    {
        var memory = new Memory();
        memory.Aliases.Add(new("S1", "Mo7ab Mohamed", 96, true, "G", "mohab osama sayed mohamed", DateTimeOffset.UtcNow));
        var result = await Engine(memory).MatchAsync(Group(Student(), Student("S2", "Mohab Mohamed", 2)), ["Mo7ab Mohamed"]);
        Assert.All(result.Students, s => Assert.Equal(AttendanceMatchStatus.NeedsReview, s.Status));
    }

    [Theory]
    [InlineData(true, 90, false)]
    [InlineData(true, 99, true)]
    [InlineData(false, 99, false)]
    public async Task UncertainAiProducesReviewNotAbsence(bool match, int confidence, bool review)
    {
        var memory = new Memory();
        var ai = new Ai((s, _) => new(match, confidence, s.StudentId, "Uncertain", review));
        var result = await Engine(memory, ai).MatchAsync(Group(Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students[0].Status);
        Assert.Equal(MatchSource.AI, Assert.Single(result.ReviewQueue).Candidates[0].Source);
        Assert.Empty(memory.Aliases);
    }

    [Fact]
    public async Task ConflictingAiResultsAreNotLearned()
    {
        var memory = new Memory();
        var ai = new Ai((s, _) => new(true, 96, s.StudentId, "Similar", false));
        var result = await Engine(memory, ai).MatchAsync(Group(Student(), Student("S2", "Mohab Ahmed Mohamed", 2)), ["Mo7ab Mohamed"]);
        Assert.All(result.Students, s => Assert.Equal(AttendanceMatchStatus.NeedsReview, s.Status));
        Assert.Empty(memory.Aliases);
    }

    [Fact]
    public async Task ReviewQueueContainsUnknownObservationAndKeepsRosterOrder()
    {
        var result = await Engine().MatchAsync(Group(Student("Z", "Zulu Smith", 1), Student("A", "Alpha Jones", 8)), ["Unknown Person"]);
        Assert.Equal(new[] { "Zulu Smith", "Alpha Jones" }, result.Students.Select(s => s.StudentName));
        Assert.Equal("Unknown Person", Assert.Single(result.ReviewQueue).ObservedName);
        Assert.DoesNotContain(result.Students, s => s.Status == AttendanceMatchStatus.Present);
    }

    [Fact]
    public async Task UnobservedIsNotAbsentAndReviewCannotDowngradeConfirmedPresence()
    {
        var result = await Engine().MatchAsync(Group(Student(), Student("S2", "Zulu Jones", 6)),
            ["Mohab Osama Sayed Mohamed", "Mohab"]);
        Assert.Equal(AttendanceMatchStatus.Present, result.Students[0].Status);
        Assert.Equal(AttendanceMatchStatus.NotObserved, result.Students[1].Status);
        Assert.Single(result.ReviewQueue);
    }

    [Fact]
    public async Task ExplicitRosterAliasIsCheckedBeforeAi()
    {
        var ai = new Ai((_, _) => throw new InvalidOperationException());
        var result = await Engine(ai: ai).MatchAsync(Group(Student() with { Aliases = ["Student Mo"] }), ["Student Mo"]);
        Assert.Equal(MatchSource.Alias, result.Students[0].MatchSource);
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task WrongStudentIdAndNetworkFailuresGoToReview()
    {
        foreach (var ai in new[] { new Ai((_, _) => new(true, 99, "Other", "Wrong ID", false)),
                     new Ai((_, _) => throw new HttpRequestException("secret data must not be logged")) })
        {
            var result = await Engine(ai: ai).MatchAsync(Group(Student()), ["Mo7ab Mohamed"]);
            Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students[0].Status);
            Assert.NotEmpty(result.Diagnostics);
            Assert.DoesNotContain(result.Diagnostics, d => d.Contains("secret data"));
        }
    }

    [Fact]
    public async Task AiBudgetNeverSilentlyTruncatesCandidates()
    {
        var ai = new Ai((s, _) => new(true, 99, s.StudentId, "Guess", false));
        var result = await Engine(ai: ai, options: new() { MaximumAiCandidates = 1 }).MatchAsync(
            Group(Student(), Student("S2", "Mohab Ahmed Mohamed", 2)), ["Mo7ab Mohamed"]);
        Assert.Equal(0, ai.Calls);
        Assert.Contains("budget", Assert.Single(result.ReviewQueue).Reason);
    }

    [Fact]
    public async Task InvalidRosterOrderIsRejectedAndCancellationPropagates()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Engine().MatchAsync(Group(Student(), Student("S2")), []));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine().MatchAsync(Group(Student()), [], cancel.Token));
    }
}
