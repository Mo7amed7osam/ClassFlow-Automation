using System.Text.Json;
using ZoomAutoAdmit.AttendanceMatching;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// A class opens and its group has never been read on this PC: the match brings the students from
/// the LMS by itself instead of leaving the attendance empty until somebody presses Load roster.
/// </summary>
public sealed class AppAttendanceRosterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit.Tests", "roster-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Class = new(2026, 9, 18, 14, 0, 0);
    private const string Group = "CAI5_AIS4_S7";

    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch { } }

    private string Snapshot(params string[] names)
    {
        string root = Path.Combine(_folder, "attendance");
        string session = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var snapshot = new
        {
            timestamp = new DateTimeOffset(Class.AddMinutes(20)).ToString("O"),
            trigger = "test",
            meeting = new { accountId = Group, scheduledStart = new DateTimeOffset(Class).ToString("O"), meetingUrl = "https://zoom.us/j/1234567890" },
            participants = names.Select(n => new { name = n }),
        };
        File.WriteAllText(Path.Combine(session, "read.json"), JsonSerializer.Serialize(snapshot));
        return root;
    }

    private sealed class Rules : IAiMatchingService
    {
        public Task TestAsync(AiConnectionSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<AttendanceMatchResult> MatchAsync(AiConnectionSettings settings, RosterGroup group, IReadOnlyList<string> names, CancellationToken token) =>
            MatchWithRulesOnlyAsync(group, names, token);
        public Task<AttendanceMatchResult> MatchWithRulesOnlyAsync(RosterGroup group, IReadOnlyList<string> names, CancellationToken token) =>
            new AttendanceMatchingEngine(new JsonAliasMemory(Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit.Tests", "aliases-" + Guid.NewGuid().ToString("N") + ".json")),
                log: _ => { }).MatchAsync(group, names, token);
    }

    private sealed class NoKey : IAiCredentialStore
    {
        public AiConnectionSettings? Read() => null;
        public void Save(AiConnectionSettings settings) { }
        public void Delete() { }
    }

    private async Task<(ExtensionAttendanceFeed.ClassResult? Result, int Imports)> MatchAsync(bool importWorks)
    {
        string root = Snapshot("Mohab Osama Sayed");
        var store = new GroupRosterStore(Path.Combine(_folder, "groups.json"), log: _ => { });
        int imports = 0;
        var matcher = new AppAttendanceMatcher(new ExtensionAttendanceFeed(root), store, new Rules(), new NoKey())
        {
            ImportRoster = async (group, token) =>
            {
                imports++;
                if (!importWorks) return false;
                await store.CreateAsync(group, group, token);
                var saved = (await store.ListAsync(token)).Single(g => g.GroupId == group);
                await store.AddStudentsAsync(saved, [new("s1", group, 1, "Mohab Osama Sayed Mohamed", [])], token);
                return true;
            },
        };
        return (await matcher.MatchClassAsync(Group, Class), imports);
    }

    [Fact]
    public async Task TheGroupsStudentsAreBroughtFromTheLmsAndTheNamesMatched()
    {
        var (result, imports) = await MatchAsync(importWorks: true);

        Assert.Equal(1, imports);
        Assert.NotNull(result);
        Assert.Equal(["Mohab Osama Sayed Mohamed"], result!.Present);
    }

    [Fact]
    public async Task AGroupTheLmsCannotGiveIsNotAskedForOverAndOver()
    {
        string root = Snapshot("Mohab Osama Sayed");
        var store = new GroupRosterStore(Path.Combine(_folder, "groups.json"), log: _ => { });
        int imports = 0;
        var matcher = new AppAttendanceMatcher(new ExtensionAttendanceFeed(root), store, new Rules(), new NoKey())
        {
            ImportRoster = (_, _) => { imports++; return Task.FromResult(false); },
        };

        Assert.Null(await matcher.MatchClassAsync(Group, Class));
        Assert.Null(await matcher.MatchClassAsync(Group, Class));
        Assert.Equal(1, imports);
    }
}
