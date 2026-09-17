using Xunit;

namespace ZoomAutoAdmit.AttendanceMatching.Tests;

public sealed class AliasMemoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "attendance-alias-tests-" + Guid.NewGuid().ToString("N"));
    private JsonAliasMemory Memory => new(Path.Combine(_directory, "aliases.json"));
    private static ApprovedAlias Alias(string id = "S1", string name = "Mo7ab Mohamed", string group = "G") =>
        new(id, name, 96, true, group, "mohab osama sayed mohamed", DateTimeOffset.UtcNow);

    [Fact]
    public async Task RoundTripAndReuseAcrossEngineInstances()
    {
        var ai = new MatchingTests.Ai((s, _) => new(true, 96, s.StudentId, "Consistent first and family names", false));
        var first = new AttendanceMatchingEngine(Memory, ai);
        await first.MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        var result = await new AttendanceMatchingEngine(Memory).MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(MatchSource.Alias, result.Students[0].MatchSource);
        Assert.Equal(1, ai.Calls);
        Assert.True(Assert.Single(await Memory.LoadAsync()).Approved);
    }

    [Fact]
    public async Task AnAiAnswerIsNeverPaidForTwiceEvenANo()
    {
        var decisions = Path.Combine(_directory, "ai-decisions.json");
        // The AI says "not this student": no alias is saved for a no, so before it was asked every run.
        var ai = new MatchingTests.Ai((s, _) => new(false, 10, s.StudentId, "Different people", false));
        for (int run = 0; run < 3; run++)
            await new AttendanceMatchingEngine(new MatchingTests.Memory(), ai, decisions: new JsonAiDecisionMemory(decisions))
                .MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(1, ai.Calls);

        // A corrected roster name is a new question.
        await new AttendanceMatchingEngine(new MatchingTests.Memory(), ai, decisions: new JsonAiDecisionMemory(decisions))
            .MatchAsync(MatchingTests.Group(MatchingTests.Student() with { FullName = "Mohab Osama Sayed Ali" }), ["Mo7ab Mohamed"]);
        Assert.Equal(2, ai.Calls);
    }

    [Fact]
    public async Task ConflictingAliasRejectedWithoutOverwritingAndOtherGroupsAreIndependent()
    {
        await Memory.SaveAsync(Alias());
        var original = await File.ReadAllTextAsync(Memory.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => Memory.SaveAsync(Alias("S2")));
        Assert.Equal(original, await File.ReadAllTextAsync(Memory.FilePath));
        await Memory.SaveAsync(Alias("S2", group: "Other"));
        Assert.Equal(2, (await Memory.LoadAsync()).Count);
        Assert.True(File.Exists(Memory.FilePath + ".bak"));
    }

    [Fact]
    public async Task ConcurrentWritesPreserveAllEntries()
    {
        await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Memory.SaveAsync(Alias("S" + i, "Unique Alias " + i))));
        Assert.Equal(12, (await Memory.LoadAsync()).Count);
    }

    [Fact]
    public async Task CorruptMemoryPreservedEngineStillProvidesRulesAndDiagnostics()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Memory.FilePath, "not json");
        var result = await new AttendanceMatchingEngine(Memory).MatchAsync(MatchingTests.Group(MatchingTests.Student()),
            ["Mohab Osama Sayed Mohamed"]);
        Assert.Equal(AttendanceMatchStatus.Present, result.Students[0].Status);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal("not json", await File.ReadAllTextAsync(Memory.FilePath));
    }

    [Fact]
    public async Task CorruptMemoryIsNotReplacedByAiLearning()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Memory.FilePath, "not json");
        var ai = new MatchingTests.Ai((s, _) => new(true, 96, s.StudentId, "Match", false));
        var result = await new AttendanceMatchingEngine(Memory, ai).MatchAsync(MatchingTests.Group(MatchingTests.Student()), ["Mo7ab Mohamed"]);
        Assert.Equal(AttendanceMatchStatus.Present, result.Students[0].Status);
        Assert.Equal("not json", await File.ReadAllTextAsync(Memory.FilePath));
    }

    [Fact]
    public async Task CanceledSaveAndInvalidEntryDoNotWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Memory.SaveAsync(Alias(), cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => Memory.SaveAsync(Alias() with { Confidence = 101 }));
        Assert.False(File.Exists(Memory.FilePath));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
