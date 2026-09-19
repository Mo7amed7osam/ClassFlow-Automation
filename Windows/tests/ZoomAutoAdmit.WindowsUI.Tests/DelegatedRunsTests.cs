using System.Text.Json;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// One PC running several coordinators' classes: each class ends up carrying whose it is, opening
/// with their own Zoom account and going up on the LMS under their own sign-in.
///
/// The sign-ins go through the real account directory, so these tests write to Windows Credential
/// Manager - under their own throwaway targets (example.invalid addresses nobody has), which are
/// deleted again afterwards. No real account, schedule or browser is touched.
/// </summary>
public sealed class DelegatedRunsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ZoomDelegatedRuns", Guid.NewGuid().ToString("N"));
    private readonly string _stamp = Guid.NewGuid().ToString("N")[..8];
    private readonly LmsAccountDirectory _directory;
    private readonly ClassLmsAccounts _classes;
    private static readonly DateOnly Today = new(2026, 9, 21);

    public DelegatedRunsTests()
    {
        Directory.CreateDirectory(_folder);
        _directory = new LmsAccountDirectory(Path.Combine(_folder, "accounts.json"));
        _classes = new ClassLmsAccounts(Path.Combine(_folder, "class-accounts.json"), _directory);
    }

    public void Dispose()
    {
        foreach (var entry in _directory.List()) { try { _directory.Remove(entry.Id); } catch { } }
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private string Email(string who) => $"zaa-{who}-{_stamp}@example.invalid";

    // ------------------------------------------------------------------ the server, played back

    private sealed class FakeCentral : IDelegatedRunsApi
    {
        public bool IsAdmin { get; init; } = true;
        public List<CentralDelegation> Delegations { get; } = [];
        public List<CentralClassPlan> Plan { get; } = [];
        public List<(string Coordinator, int Rows)> Imported { get; } = [];
        public List<string> SecretsRead { get; } = [];
        public string? RefuseSecretFor { get; set; }

        public Task<CentralDelegationList> DelegationsAsync(CancellationToken token) =>
            Task.FromResult(new CentralDelegationList([.. Delegations]));

        public Task<CentralCoordinatorSecret> CoordinatorLmsSecretAsync(string coordinatorId, string accountId, CancellationToken token)
        {
            if (RefuseSecretFor == coordinatorId) throw new CentralApiException(System.Net.HttpStatusCode.Forbidden, "Turn this coordinator on first.");
            SecretsRead.Add(coordinatorId);
            var account = Delegations.First(d => d.CoordinatorId == coordinatorId).LmsAccount!;
            return Task.FromResult(new CentralCoordinatorSecret
            {
                Id = accountId, CoordinatorId = coordinatorId, Email = account.Email,
                Role = account.Role, Label = account.Label, Password = "made-up password for tests",
            });
        }

        public Task<JsonElement> ImportRunPlanAsync(string coordinatorId, IEnumerable<object> classes, CancellationToken token)
        {
            Imported.Add((coordinatorId, classes.Count()));
            return Task.FromResult(JsonDocument.Parse("""{"added":0}""").RootElement);
        }

        public Task<CentralRunPlan> RunPlanAsync(DateOnly from, DateOnly to, IEnumerable<string>? coordinators, CancellationToken token)
        {
            var wanted = coordinators?.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new CentralRunPlan(
                [.. Plan.Where(p => wanted == null || wanted.Contains(p.CoordinatorId))], []));
        }
    }

    private static CentralDelegation Person(string id, string name, string email, string[] groups, string? zoom, bool enabled = true) =>
        new(id, name.ToLowerInvariant(), name, "active", enabled,
            [.. groups.Select(g => new CentralGroupRef(g, g, null, false))],
            new CentralLmsAccount($"a-{id}", name, email, "coordinator", true), null, zoom,
            new CentralDelegationClasses(0, 0, 0, 0), null);

    private static CentralClassPlan Class(string coordinator, string group, DateOnly day, TimeOnly start,
        string? url = "https://zoom.us/j/91473108490", string? zoom = null, string? engine = null, string status = "planned") =>
        new(Guid.NewGuid().ToString(), coordinator, group, day.ToString("yyyy-MM-dd"), start.ToString("HH\\:mm"), "36 • Technical",
            url, zoom, engine, "lms", status, null, url == null, null, DateTimeOffset.Now);

    private DelegatedRuns Runs(FakeCentral api, UiService service, ReadTimetable? timetable = null) =>
        new(api, service, _directory, _classes,
            // Unless a test is about the timetable itself, each coordinator's LMS lists their own
            // one class today - what the plan in these tests already holds.
            readTimetable: timetable ?? ((_, _, _, groups, _) => Task.FromResult<IReadOnlyList<LmsSessionRunner.LmsSessionInfo>>(
                [.. groups.Select(g => new LmsSessionRunner.LmsSessionInfo(
                    g, Today, new TimeOnly(19, 0), "36 • Technical", "pending", null, "", "", "unknown", null, []))])),
            log: _ => { }, today: () => Today);

    // ------------------------------------------------------------------ nothing to do

    [Fact]
    public async Task ACoordinatorsOwnPcHasNobodyElsesClassesToRun()
    {
        var api = new FakeCentral { IsAdmin = false };
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["S7"], "S7"));
        var service = new UiService();

        var report = await Runs(api, service).SyncAsync();

        Assert.Equal(0, report.Coordinators);
        Assert.Empty(service.Schedules);
        Assert.Empty(api.SecretsRead);
    }

    // ------------------------------------------------------------------ two people at once

    [Fact]
    public async Task TwoCoordinatorsClassesAtTheSameTimeEachCarryTheirOwnTwoAccounts()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Delegations.Add(Person("u-sami", "Sami", Email("sami"), ["CAI5_AIS4_S8"], "CAI5_AIS4_S8"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        api.Plan.Add(Class("u-sami", "CAI5_AIS4_S8", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S8"));
        var service = new UiService("CAI5_AIS4_S7", "CAI5_AIS4_S8");

        var report = await Runs(api, service).SyncAsync();

        Assert.Equal(2, report.Coordinators);
        Assert.Equal(2, report.Scheduled);
        Assert.Empty(report.Problems);

        var mona = service.Schedules.Single(s => s.GroupName == "CAI5_AIS4_S7");
        var sami = service.Schedules.Single(s => s.GroupName == "CAI5_AIS4_S8");
        Assert.Equal("Mona", mona.Coordinator);
        Assert.Equal("Sami", sami.Coordinator);
        Assert.Equal(new TimeOnly(19, 0), mona.Time);
        Assert.Equal(new TimeOnly(19, 0), sami.Time);               // the same moment, no complaint
        Assert.Equal("CAI5_AIS4_S7", mona.AccountId);               // opened by her own Zoom account
        Assert.Equal("CAI5_AIS4_S8", sami.AccountId);
        Assert.Equal(Today, mona.OccurrenceDate);
        Assert.Equal(ScheduleDays.None, mona.Days);

        // And on the LMS each goes up under its own sign-in, with its own browser profile.
        string monaAccount = _classes.Find("CAI5_AIS4_S7")!.AccountId;
        string samiAccount = _classes.Find("CAI5_AIS4_S8")!.AccountId;
        Assert.NotEqual(monaAccount, samiAccount);
        Assert.NotEqual(_classes.StoreFor("CAI5_AIS4_S7").Profile, _classes.StoreFor("CAI5_AIS4_S8").Profile);
    }

    [Fact]
    public async Task NeitherCoordinatorBecomesTheAccountThisPcFallsBackTo()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        var mine = _directory.Upsert("This PC", Email("admin"), "made-up password for tests", "admin", makeActive: true);

        await Runs(api, new UiService("CAI5_AIS4_S7")).SyncAsync();

        Assert.Equal(mine.Id, _directory.Active().Id);
        Assert.Equal(2, _directory.List().Count);
    }

    // ------------------------------------------------------------------ what a person still has to supply

    [Fact]
    public async Task AClassWithNoZoomLinkIsAskedForInsteadOfFailingAtItsTime()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), url: null));
        var service = new UiService("CAI5_AIS4_S7");

        var report = await Runs(api, service).SyncAsync();

        Assert.Empty(service.Schedules);
        Assert.Contains(report.Problems, p => p.Contains("no Zoom link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AZoomAccountThisPcDoesNotHaveIsNamedSoItCanBeAdded()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "NOT_HERE"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "NOT_HERE"));
        var service = new UiService("CAI5_AIS4_S8");

        var report = await Runs(api, service).SyncAsync();

        Assert.Empty(service.Schedules);
        Assert.Contains(report.Problems, p => p.Contains("NOT_HERE"));
    }

    [Fact]
    public async Task ACoordinatorWithNoLmsSignInOfTheirOwnIsReportedAndClaimsNothing()
    {
        var api = new FakeCentral();
        api.Delegations.Add(new CentralDelegation("u-mona", "mona", "Mona", "active", true,
            [new CentralGroupRef("CAI5_AIS4_S7", "CAI5_AIS4_S7", null, false)], null, null, "CAI5_AIS4_S7", null, null));

        var report = await Runs(api, new UiService("CAI5_AIS4_S7")).SyncAsync();

        Assert.Equal(0, report.Coordinators);
        Assert.Null(_classes.Find("CAI5_AIS4_S7"));
        Assert.Contains(report.Problems, p => p.Contains("no LMS sign-in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ASignInTheServerRefusesStopsThatCoordinatorAndNobodyElse()
    {
        var api = new FakeCentral { RefuseSecretFor = "u-mona" };
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Delegations.Add(Person("u-sami", "Sami", Email("sami"), ["CAI5_AIS4_S8"], "CAI5_AIS4_S8"));
        api.Plan.Add(Class("u-sami", "CAI5_AIS4_S8", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S8"));
        var service = new UiService("CAI5_AIS4_S7", "CAI5_AIS4_S8");

        var report = await Runs(api, service).SyncAsync();

        Assert.Equal(1, report.Coordinators);
        Assert.Equal("Sami", Assert.Single(service.Schedules).Coordinator);
        Assert.Contains(report.Problems, p => p.StartsWith("Mona:"));
    }

    // ------------------------------------------------------------------ turning somebody off

    [Fact]
    public async Task ACoordinatorTurnedOffLosesTheirGroupsAndTheirClassesComeOffThisPc()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        var service = new UiService("CAI5_AIS4_S7");
        var runs = Runs(api, service);
        await runs.SyncAsync();
        Assert.Single(service.Schedules);

        api.Delegations[0] = api.Delegations[0] with { Enabled = false };
        var report = await runs.SyncAsync();

        Assert.Empty(service.Schedules);
        Assert.Equal(1, report.Removed);
        Assert.Null(_classes.Find("CAI5_AIS4_S7"));
    }

    [Fact]
    public async Task ThisPcsOwnClassesAreNeverTouchedByAnyOfThis()
    {
        var api = new FakeCentral();
        var service = new UiService("CAI5_AIS4_S7");
        var own = new MeetingSchedule(Guid.NewGuid(), "My own class", "https://zoom.us/j/1", "CAI5_AIS4_S7",
            new TimeOnly(9, 0), ScheduleDays.Monday, true);
        await service.SaveScheduleAsync(own);

        await Runs(api, service).SyncAsync();

        Assert.Equal(own, Assert.Single(service.Schedules));
    }

    [Fact]
    public async Task AClassThatAlreadyOpenedTodayIsNotOpenedAgainByBeingWrittenOutOnceMore()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        var service = new UiService("CAI5_AIS4_S7");
        var runs = Runs(api, service);
        await runs.SyncAsync();

        // It opened; the mark that says so must survive the next pass.
        var opened = service.Schedules[0] with { LastTriggeredDate = Today };
        await service.SaveScheduleAsync(opened);
        await runs.SyncAsync();

        Assert.Equal(Today, Assert.Single(service.Schedules).LastTriggeredDate);
    }

    // ------------------------------------------------------------------ their timetable

    [Fact]
    public async Task EachCoordinatorsTimetableIsReadWithTheirOwnSignInAndSentAsTheirPlan()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Delegations.Add(Person("u-sami", "Sami", Email("sami"), ["CAI5_AIS4_S8"], "CAI5_AIS4_S8"));
        var signedInAs = new List<string>();

        var report = await Runs(api, new UiService("CAI5_AIS4_S7", "CAI5_AIS4_S8"),
            timetable: (signIn, from, to, groups, _) =>
            {
                lock (signedInAs) signedInAs.Add(signIn.Profile);
                Assert.Equal(Today.AddDays(-DelegatedRuns.DaysBack), from);
                Assert.Equal(Today.AddDays(DelegatedRuns.DaysAhead), to);
                return Task.FromResult<IReadOnlyList<LmsSessionRunner.LmsSessionInfo>>(
                [
                    new(groups.First(), Today, new TimeOnly(19, 0), "36 • Technical", "pending", null, "", "", "unknown", null, []),
                    new(groups.First(), Today.AddDays(7), new TimeOnly(19, 0), "39 • Technical", "pending", null, "", "", "unknown", null, []),
                ]);
            }).SyncAsync(readTimetables: true);

        Assert.Equal(2, api.Imported.Count);
        Assert.All(api.Imported, i => Assert.Equal(2, i.Rows));
        Assert.Equal(["u-mona", "u-sami"], api.Imported.Select(i => i.Coordinator).Order());
        // Two people, two profiles: neither read signs the other out.
        Assert.Equal(2, signedInAs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, report.Coordinators);
    }

    [Fact]
    public async Task ACancelledSessionIsNotPartOfTheTimetable()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));

        await Runs(api, new UiService("CAI5_AIS4_S7"),
            timetable: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<LmsSessionRunner.LmsSessionInfo>>(
            [
                new("CAI5_AIS4_S7", Today, new TimeOnly(19, 0), "36", "pending", null, "", "", "unknown", null, []),
                new("CAI5_AIS4_S7", Today.AddDays(1), new TimeOnly(19, 0), "37", "cancelled", null, "", "", "unknown", null, []),
                new("CAI5_AIS4_S7", null, null, "no date", "pending", null, "", "", "unknown", null, []),
            ])).SyncAsync(readTimetables: true);

        Assert.Equal(1, Assert.Single(api.Imported).Rows);
    }

    [Fact]
    public async Task ATimetableReadLeftAloneIsNotReadAgainOnEveryPass()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        var runs = Runs(api, new UiService("CAI5_AIS4_S7"),
            timetable: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<LmsSessionRunner.LmsSessionInfo>>(
                [new("CAI5_AIS4_S7", Today, new TimeOnly(19, 0), "36", "pending", null, "", "", "unknown", null, [])]));

        await runs.SyncAsync(readTimetables: true);
        await runs.SyncAsync();                       // the ordinary pass, minutes later
        Assert.Single(api.Imported);

        await runs.SyncAsync(readTimetables: true);   // somebody pressed Refresh
        Assert.Equal(2, api.Imported.Count);
    }

    // ------------------------------------------------------------------ the fake app

    private sealed class UiService(params string[] accounts) : IWindowsUiService
    {
        public List<MeetingSchedule> Schedules { get; } = [];
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.Now);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([.. accounts.Select(a => new WindowsMeetingAccountMetadata(a, a, ""))]);
        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingSchedule>>(Schedules.ToArray());
        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default)
        { Schedules.RemoveAll(s => s.Id == schedule.Id); Schedules.Add(schedule); return Task.CompletedTask; }
        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Schedules.RemoveAll(s => s.Id == scheduleId) > 0);
        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionDisplayInfo> StartMeetingAsync(string accountId, string meetingUrl, EnginePreference preference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
