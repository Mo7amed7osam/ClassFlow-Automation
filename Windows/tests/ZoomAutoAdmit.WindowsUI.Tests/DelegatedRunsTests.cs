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

        /// <summary>The Zoom sign-in each coordinator kept, by their account's id.</summary>
        public Dictionary<string, (string Email, string Password)> ZoomSecrets { get; } = [];
        public List<string> ZoomSecretsRead { get; } = [];

        public Task<CentralZoomSecret> CoordinatorZoomSecretAsync(string coordinatorId, string accountId, CancellationToken token)
        {
            ZoomSecretsRead.Add(accountId);
            if (!ZoomSecrets.TryGetValue(accountId, out var found))
                throw new CentralApiException(System.Net.HttpStatusCode.NotFound, "No Zoom password is saved.");
            return Task.FromResult(new CentralZoomSecret { Id = accountId, AccountId = accountId, Email = found.Email, Password = found.Password });
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

    /// <summary>A coordinator with their own groups, LMS sign-in and Zoom account (with its link).</summary>
    private static CentralDelegation Person(string id, string name, string email, string[] groups, string? zoom,
        bool enabled = true, string? zoomEmail = null, bool hasZoomPassword = false) =>
        new(id, name.ToLowerInvariant(), name, "active", enabled,
            [.. groups.Select(g => new CentralGroupRef(g, g, null, false))],
            new CentralLmsAccount($"a-{id}", name, email, "coordinator", true), null,
            zoom == null ? null : $"z-{zoom}", zoom,
            zoom == null ? [] : [new CentralZoomAccount($"z-{zoom}", zoom, zoom,
                zoomEmail ?? $"{name.ToLowerInvariant()}@zoom.example.com", zoom,
                "https://zoom.us/j/91473108490", null, true) { HasPassword = hasZoomPassword }],
            new CentralDelegationClasses(0, 0, 0, 0), null);

    private static CentralClassPlan Class(string coordinator, string group, DateOnly day, TimeOnly start,
        string? url = "https://zoom.us/j/91473108490", string? zoom = null, string? engine = null, string status = "planned") =>
        new(Guid.NewGuid().ToString(), coordinator, group, day.ToString("yyyy-MM-dd"), start.ToString("HH\\:mm"), "36 • Technical",
            url, zoom, engine, "lms", status, null, url == null, null, DateTimeOffset.Now);

    private sealed class FakeZoomCredentials : IZoomProfileCredentialStore
    {
        public Dictionary<string, (string Email, string Password)> Saved { get; } = [];
        public bool HasPassword(string accountId) => Saved.ContainsKey(accountId);
        public string ReferenceFor(string accountId) => $"wincred:ZoomAutoAdmit/ZoomProfile/{accountId}";
        public void Save(string accountId, string email, string password) => Saved[accountId] = (email, password);
        public void Delete(string accountId) => Saved.Remove(accountId);
    }

    private FakeZoomCredentials _zoom = new();

    private DelegatedRuns Runs(FakeCentral api, UiService service, ReadTimetable? timetable = null) =>
        new(api, service, _directory, _classes, _zoom,
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
            [new CentralGroupRef("CAI5_AIS4_S7", "CAI5_AIS4_S7", null, false)], null, null,
            null, "CAI5_AIS4_S7", [], null, null));

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

    [Fact]
    public async Task TheirZoomAccountIsFoundHereByTheEmailItSignsInWithEvenUnderAnotherName()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7",
            zoomEmail: "mona.teaches@zoom.example.com"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        // On this PC her Zoom account was added under a different name, but the same sign-in.
        var service = new UiService();
        service.Add("mona-s7", "mona.teaches@zoom.example.com");

        var report = await Runs(api, service).SyncAsync();

        Assert.Empty(report.Problems);
        Assert.Equal("mona-s7", Assert.Single(service.Schedules).AccountId);
    }

    [Fact]
    public async Task AZoomAccountNeitherNamedNorSignedInHereIsReportedWithBoth()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7",
            zoomEmail: "mona.teaches@zoom.example.com"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        var service = new UiService();
        service.Add("somebody-else", "else@zoom.example.com");

        var report = await Runs(api, service).SyncAsync();

        Assert.Empty(service.Schedules);
        var problem = Assert.Single(report.Problems);
        Assert.Contains("CAI5_AIS4_S7", problem);
        Assert.Contains("mona.teaches@zoom.example.com", problem);
    }

    [Fact]
    public async Task TheirZoomSignInIsKeptHereSoAFreshProfileDoesNotWaitForAPerson()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7", hasZoomPassword: true));
        api.ZoomSecrets["z-CAI5_AIS4_S7"] = ("mona@zoom.example.com", "made-up Zoom password for Mona");
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));

        var report = await Runs(api, new UiService("CAI5_AIS4_S7")).SyncAsync();

        Assert.Empty(report.Problems);
        Assert.Equal(("mona@zoom.example.com", "made-up Zoom password for Mona"), _zoom.Saved["CAI5_AIS4_S7"]);
    }

    [Fact]
    public async Task ACoordinatorWhoKeptNoZoomPasswordIsNotAskedForOne()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7"));
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));

        var report = await Runs(api, new UiService("CAI5_AIS4_S7")).SyncAsync();

        Assert.Empty(report.Problems);           // their profile here may well be signed in already
        Assert.Empty(api.ZoomSecretsRead);
        Assert.Empty(_zoom.Saved);
    }

    [Fact]
    public async Task AZoomSignInTheServerRefusesIsSaidPlainlyAndCostsNothingElse()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mona", "Mona", Email("mona"), ["CAI5_AIS4_S7"], "CAI5_AIS4_S7", hasZoomPassword: true));
        // Nothing in ZoomSecrets: the server refuses it.
        api.Plan.Add(Class("u-mona", "CAI5_AIS4_S7", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S7"));
        var service = new UiService("CAI5_AIS4_S7");

        var report = await Runs(api, service).SyncAsync();

        Assert.Single(service.Schedules);        // the class still runs
        Assert.Contains(report.Problems, p => p.Contains("Zoom sign-in") && p.Contains("wait for a person"));
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
    public async Task AClassThisPcAlreadyHasIsNotAddedASecondTime()
    {
        // 2026-09-21: S8 at 19:00 was both this PC's own class and Mohab's delegated one, and both
        // entries would have opened the same meeting.
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mohab", "Mohab", Email("mohab"), ["CAI5_AIS4_S8"], "CAI5_AIS4_S8"));
        api.Plan.Add(Class("u-mohab", "CAI5_AIS4_S8", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S8"));
        api.Plan.Add(Class("u-mohab", "CAI5_AIS4_S8", Today.AddDays(2), new TimeOnly(17, 0), zoom: "CAI5_AIS4_S8"));
        var service = new UiService("CAI5_AIS4_S8");
        var own = new MeetingSchedule(Guid.NewGuid(), "CAI5_AIS4_S8 • Technical", "https://zoom.us/j/92844609413", "CAI5_AIS4_S8",
            new TimeOnly(19, 0), ScheduleDays.None, true, OccurrenceDate: Today, GroupName: "CAI5_AIS4_S8");
        await service.SaveScheduleAsync(own);

        await Runs(api, service).SyncAsync();

        Assert.Equal(2, service.Schedules.Count);
        Assert.Single(service.Schedules, s => s.OccurrenceDate == Today);           // only its own
        Assert.Contains(service.Schedules, s => s.OccurrenceDate == Today.AddDays(2) && s.Coordinator == "Mohab");
    }

    [Fact]
    public async Task AWeeklyClassOfThisPcCoversTheSameDayOfTheirs()
    {
        var api = new FakeCentral();
        api.Delegations.Add(Person("u-mohab", "Mohab", Email("mohab"), ["CAI5_AIS4_S8"], "CAI5_AIS4_S8"));
        api.Plan.Add(Class("u-mohab", "CAI5_AIS4_S8", Today, new TimeOnly(19, 0), zoom: "CAI5_AIS4_S8"));
        var service = new UiService("CAI5_AIS4_S8");
        await service.SaveScheduleAsync(new MeetingSchedule(Guid.NewGuid(), "S8 weekly", "https://zoom.us/j/92844609413", "CAI5_AIS4_S8",
            new TimeOnly(19, 0), ScheduleDays.Monday, true, GroupName: "CAI5_AIS4_S8"));     // 2026-09-21 is a Monday

        await Runs(api, service).SyncAsync();

        Assert.Single(service.Schedules);
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

    private sealed class UiService : IWindowsUiService
    {
        private readonly List<WindowsMeetingAccountMetadata> _accounts = [];

        public UiService(params string[] accounts)
        {
            foreach (var account in accounts) Add(account, null);
        }

        /// <summary>A Zoom account on this PC, optionally with the e-mail it signs in to Zoom with.</summary>
        public void Add(string accountId, string? zoomEmail) =>
            _accounts.Add(new WindowsMeetingAccountMetadata(accountId, accountId, "") { ZoomEmail = zoomEmail });

        public List<MeetingSchedule> Schedules { get; } = [];
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.Now);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([.. _accounts]);
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
