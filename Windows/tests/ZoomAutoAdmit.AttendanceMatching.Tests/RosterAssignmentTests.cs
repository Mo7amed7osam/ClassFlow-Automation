using ZoomAutoAdmit.Roster;
using Xunit;

namespace ZoomAutoAdmit.AttendanceMatching.Tests;

/// <summary>
/// The last pass of a match: the AI is shown the students still without a name and every Zoom name
/// nobody was confirmed from, all at once. It is what finds "Kero Saeed"; it is never allowed to
/// break a tie, to take a name the rules already gave to somebody else, or to hand one name out twice.
/// </summary>
public class RosterAssignmentTests
{
    private sealed class Assigner(Func<IReadOnlyList<GroupStudent>, IReadOnlyList<string>, IReadOnlyList<RosterAssignment>> answer) : IAiRosterAssigner
    {
        public int Calls;
        public readonly List<string> NamesSeen = [];
        public Task<IReadOnlyList<RosterAssignment>> AssignAsync(IReadOnlyList<GroupStudent> students,
            IReadOnlyList<string> observedNames, CancellationToken token = default)
        {
            Calls++;
            NamesSeen.AddRange(observedNames);
            return Task.FromResult(answer(students, observedNames));
        }
    }

    private sealed class Decisions : IAiDecisionMemory
    {
        public readonly Dictionary<string, AiNameMatch> Answers = new(StringComparer.OrdinalIgnoreCase);
        private static string Key(string group, string student, string observed) =>
            $"{group}|{student}|{NameNormalizer.Normalize(observed)}";
        public bool TryGet(string groupId, string studentId, string rosterName, string observedName, out AiNameMatch answer) =>
            Answers.TryGetValue(Key(groupId, studentId, observedName), out answer!);
        public void Save(string groupId, string studentId, string rosterName, string observedName, AiNameMatch answer) =>
            Answers[Key(groupId, studentId, observedName)] = answer;
    }

    private static GroupStudent Student(string id, string name, int order) => new(id, "G", order, name, []);
    private static RosterGroup Group(params GroupStudent[] students) => new("G", "Group", DateTimeOffset.UtcNow, students);
    private static AttendanceMatchingEngine Engine(IAiRosterAssigner assigner, IAiDecisionMemory? decisions = null,
        MatchingTests.Memory? memory = null) =>
        new(memory ?? new(), ai: null, options: null, log: _ => { }, decisions: decisions, assigner: assigner);

    private static RosterAssignment Says(string studentId, string observed, int confidence = 92) =>
        new(studentId, observed, confidence, false, "The short form of the first name and the family name agree.");

    [Fact]
    public async Task ANicknameNoRuleCanMatchIsGivenToItsStudent()
    {
        var assigner = new Assigner((students, names) => [Says(students[0].StudentId, names[0])]);
        var result = await Engine(assigner).MatchAsync(
            Group(Student("S1", "Kyrillos Saeed Milad Saeed", 1)), ["Kero Saeed"]);

        var row = Assert.Single(result.Students);
        Assert.Equal(AttendanceMatchStatus.Present, row.Status);
        Assert.Equal(MatchSource.AI, row.MatchSource);
        Assert.Equal(["Kero Saeed"], row.ObservedNames);
        Assert.Empty(result.ReviewQueue);
        Assert.Equal(1, assigner.Calls);
    }

    [Fact]
    public async Task TwoStudentsTheNameFitsEquallyAreStillNotDecidedByTheAi()
    {
        var assigner = new Assigner((students, names) => [Says(students[0].StudentId, names[0], 100)]);
        var result = await Engine(assigner).MatchAsync(
            Group(Student("A", "Ahmed Mohamed Ali", 1), Student("B", "Ahmed Mohamed Hassan", 2)), ["Ahmed Mohamed"]);

        Assert.All(result.Students, s => Assert.Equal(AttendanceMatchStatus.NeedsReview, s.Status));
        Assert.Single(result.ReviewQueue);
    }

    [Fact]
    public async Task ANameTheRulesGiveToSomebodyElseIsNotTakenAway()
    {
        // "Mariam Tarek Fouad" is Mariam's by the name rules; the AI offering it to Nour changes nothing.
        var assigner = new Assigner((students, names) => [Says(students[0].StudentId, names.FirstOrDefault() ?? "")]);
        var result = await Engine(assigner).MatchAsync(
            Group(Student("N", "Nour Hassan Ibrahim", 1), Student("M", "Mariam Tarek Fouad", 2)),
            ["Mariam Tarek Fouad"]);

        var nour = result.Students.Single(s => s.StudentId == "N");
        var mariam = result.Students.Single(s => s.StudentId == "M");
        Assert.Equal(AttendanceMatchStatus.Present, mariam.Status);
        Assert.Equal(MatchSource.Rule, mariam.MatchSource);
        Assert.Equal(AttendanceMatchStatus.NotObserved, nour.Status);
        // A name already confirmed is never among the choices the AI is shown.
        Assert.DoesNotContain("Mariam Tarek Fouad", assigner.NamesSeen);
    }

    [Fact]
    public async Task OneNameIsNeverGivenToTwoStudents()
    {
        var assigner = new Assigner((students, names) => [.. students.Select(s => Says(s.StudentId, names[0]))]);
        var result = await Engine(assigner).MatchAsync(
            Group(Student("A", "Youssef Adel Sami", 1), Student("B", "Mostafa Khaled Reda", 2)), ["Joe Adel"]);

        Assert.Single(result.Students, s => s.Status == AttendanceMatchStatus.Present);
    }

    [Theory]
    [InlineData(84, false)]
    [InlineData(95, true)]
    public async Task AnUnsureAnswerLeavesTheStudentWaiting(int confidence, bool needsReview)
    {
        var assigner = new Assigner((students, names) => [new(students[0].StudentId, names[0], confidence, needsReview, "Not sure.")]);
        var result = await Engine(assigner).MatchAsync(Group(Student("S1", "Kyrillos Saeed Milad Saeed", 1)), ["Kero Saeed"]);

        Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students[0].Status);
    }

    [Fact]
    public async Task AnAnswerGivenBeforeIsUsedWithoutAskingAgain()
    {
        var decisions = new Decisions();
        decisions.Save("G", "S1", "Kyrillos Saeed Milad Saeed", "Kero Saeed", new(true, 92, "S1", "Asked yesterday.", false));
        var assigner = new Assigner((_, _) => []);
        var result = await Engine(assigner, decisions).MatchAsync(Group(Student("S1", "Kyrillos Saeed Milad Saeed", 1)), ["Kero Saeed"]);

        Assert.Equal(AttendanceMatchStatus.Present, result.Students[0].Status);
        Assert.Equal(0, assigner.Calls);
    }

    [Fact]
    public async Task EveryFreshAnswerIsRememberedSoItIsNeverPaidForTwice()
    {
        var decisions = new Decisions();
        var assigner = new Assigner((students, names) => [Says(students[0].StudentId, names[0])]);
        await Engine(assigner, decisions).MatchAsync(Group(Student("S1", "Kyrillos Saeed Milad Saeed", 1)), ["Kero Saeed"]);

        Assert.True(decisions.TryGet("G", "S1", "Kyrillos Saeed Milad Saeed", "Kero Saeed", out var remembered));
        Assert.True(remembered.Match);
    }

    [Fact]
    public async Task TheAiBeingUnreachableLeavesTheMatchAsTheRulesMadeIt()
    {
        var assigner = new Assigner((_, _) => throw new HttpRequestException("no connection"));
        var result = await Engine(assigner).MatchAsync(
            Group(Student("S1", "Mohab Osama Sayed Mohamed", 1), Student("S2", "Kyrillos Saeed Milad Saeed", 2)),
            ["Mohab Osama", "Kero Saeed"]);

        Assert.Equal(AttendanceMatchStatus.Present, result.Students.Single(s => s.StudentId == "S1").Status);
        Assert.Equal(AttendanceMatchStatus.NeedsReview, result.Students.Single(s => s.StudentId == "S2").Status);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("AI assignment unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StudentsAreAskedAboutInBatches()
    {
        var roster = Enumerable.Range(1, 9).Select(i => Student("S" + i, $"Student{i} Family{i} Last{i}", i)).ToArray();
        var assigner = new Assigner((_, _) => []);
        await new AttendanceMatchingEngine(new MatchingTests.Memory(), ai: null,
            options: new MatchingOptions { AssignmentBatch = 4 }, log: _ => { }, assigner: assigner)
            .MatchAsync(Group(roster), ["Nobody Here At All"]);

        Assert.Equal(3, assigner.Calls);        // 4 + 4 + 1
    }

    [Fact]
    public void OnlyTheIdsTheModelWasGivenAreBelieved()
    {
        var names = new[] { ("z0", "Kero Saeed") };
        var answer = """
            {"matches":[{"studentId":"S1","observedId":"z0","confidence":90,"needsReview":false,"reason":"ok"},
                        {"studentId":"S9","observedId":"z0","confidence":99,"needsReview":false,"reason":"invented"},
                        {"studentId":"S1","observedId":"z7","confidence":99,"needsReview":false,"reason":"invented"}]}
            """;
        var parsed = ChatRosterAssigner.ParseAnswer(answer, ["S1"], names);

        var only = Assert.Single(parsed);
        Assert.Equal("S1", only.StudentId);
        Assert.Equal("Kero Saeed", only.ObservedName);
        Assert.Equal(90, only.Confidence);
        Assert.False(only.NeedsReview);
    }
}
