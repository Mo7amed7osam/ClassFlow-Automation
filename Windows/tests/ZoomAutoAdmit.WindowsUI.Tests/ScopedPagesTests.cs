using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.SessionRoles;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// The pages of a shared PC, seen by whoever is signed in. The admin set this PC up, so its Zoom
/// accounts, rosters and session types are all theirs; a coordinator signing in after them must
/// find their own and nothing else.
/// </summary>
public sealed class ScopedPagesTests
{
    private static CentralMe Admin =>
        new("u-admin", "admin", "The Admin", "admin", true, null, DateTimeOffset.Now.AddDays(1));

    private static CentralMe Mona =>
        new("u-mona", "mona", "Mona", "coordinator", false,
            [new CentralGroupRef("g1", "CAI5_IND1_G1", null, false), new CentralGroupRef("g2", "CAI5_IND1_G2", null, false)],
            DateTimeOffset.Now.AddDays(1));

    // ------------------------------------------------------------------ Accounts

    [Fact]
    public async Task ACoordinatorFindsOnlyTheZoomAccountsThatHostTheirGroups()
    {
        CentralMe? who = Admin;
        var scope = new SignedInScope(() => who);
        var service = new AccountsService(
            ("CAI5_AIS4_S7", "CAI5_AIS4_S7"), ("CAI5_AIS4_S8", "CAI5_AIS4_S8"),
            ("CAI5_IND1_G1", "CAI5_IND1_G1"), ("CAI5_IND1_G2", "CAI5_IND1_G2"));
        var page = new AccountsViewModel(service, new NoCredentials(), scope);

        await page.RefreshAsync();
        Assert.Equal(4, page.Items.Count);                 // the admin set it up and sees all of it
        Assert.Equal("", page.ScopeNote);

        who = Mona;
        await page.RefreshAsync();
        Assert.Equal(["CAI5_IND1_G1", "CAI5_IND1_G2"], page.Items.Select(a => a.AccountId));
        Assert.Contains("2 on this PC belong to somebody else", page.ScopeNote);
    }

    [Fact]
    public async Task AnAccountOpenedInTheEditorIsLetGoWhenItStopsBeingTheirs()
    {
        CentralMe? who = Admin;
        var scope = new SignedInScope(() => who);
        var page = new AccountsViewModel(new AccountsService(("CAI5_AIS4_S7", "CAI5_AIS4_S7"), ("CAI5_IND1_G1", "CAI5_IND1_G1")),
            new NoCredentials(), scope);
        await page.RefreshAsync();
        page.SelectedAccount = page.Items.First(a => a.AccountId == "CAI5_AIS4_S7");

        who = Mona;
        await page.RefreshAsync();

        Assert.Single(page.Items);
        Assert.Null(page.SelectedAccount);                 // somebody else's account is not left open
    }

    [Fact]
    public async Task AnAccountWhoseGroupIsNamedSeparatelyIsMatchedByThatGroup()
    {
        var scope = new SignedInScope(() => Mona);
        // The id is a profile name, and the group it hosts is the one that counts.
        var service = new AccountsService(("zoom-1", "CAI5_IND1_G1"), ("zoom-2", "CAI5_AIS4_S7"));
        var page = new AccountsViewModel(service, new NoCredentials(), scope);

        await page.RefreshAsync();

        Assert.Equal("zoom-1", Assert.Single(page.Items).AccountId);
    }

    // ------------------------------------------------------------------ Session roles

    [Fact]
    public void ACoordinatorFindsOnlyTheSessionTypesThatNameTheirGroups()
    {
        CentralMe? who = Admin;
        var scope = new SignedInScope(() => who);
        var store = new RolesStore(
            new SessionRoleProfile("Technical") { Accounts = ["CAI5_AIS4_S7"] },
            new SessionRoleProfile("Freelancing") { Accounts = ["CAI5_AIS4_S8"] },
            new SessionRoleProfile("Industry") { Accounts = ["CAI5_IND1_G1"] },
            new SessionRoleProfile("Every meeting"));
        var page = new SessionRolesViewModel(store, scope);

        Assert.Equal(4, page.Profiles.Count);

        who = Mona;
        page.Load();

        // Theirs, and the one that names no group because it covers their meetings too.
        Assert.Equal(["Every meeting", "Industry"], page.Profiles.Select(p => p.SessionType).Order());
        Assert.Contains("2 on this PC belong to somebody else", page.ScopeNote);
    }

    // ------------------------------------------------------------------ Groups & students

    [Fact]
    public async Task ACoordinatorFindsOnlyTheirOwnRosters()
    {
        CentralMe? who = Admin;
        var scope = new SignedInScope(() => who);
        var service = new RosterService("CAI5_AIS4_S7", "CAI5_AIS4_S8", "CAI5_IND1_G1");
        var page = new GroupRosterViewModel(service, new NoRosterDialogs(), scope);

        await page.RefreshAsync();
        Assert.Equal(3, page.Groups.Count);

        who = Mona;
        await page.RefreshAsync();

        Assert.Equal("CAI5_IND1_G1", Assert.Single(page.Groups).GroupId);
        Assert.Contains("2 on this PC belong to somebody else", page.ScopeNote);
    }

    [Fact]
    public async Task TheSearchBoxStillWorksInsideWhatIsTheirs()
    {
        var page = new GroupRosterViewModel(new RosterService("CAI5_IND1_G1", "CAI5_IND1_G2", "CAI5_AIS4_S7"),
            new NoRosterDialogs(), new SignedInScope(() => Mona));
        await page.RefreshAsync();
        Assert.Equal(2, page.Groups.Count);

        page.GroupSearch = "G2";
        Assert.Equal("CAI5_IND1_G2", Assert.Single(page.Groups).GroupId);

        // Searching can never reach past what is theirs.
        page.GroupSearch = "AIS4";
        Assert.Empty(page.Groups);
    }

    // ------------------------------------------------------------------ the stand-ins

    private sealed class AccountsService(params (string Id, string Group)[] accounts) : IWindowsUiService
    {
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.Now);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>(
                [.. accounts.Select(a => new WindowsMeetingAccountMetadata(a.Id, a.Id, "") { GroupName = a.Group })]);
        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingSchedule>>([]);
        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionDisplayInfo> StartMeetingAsync(string accountId, string meetingUrl, EnginePreference preference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoCredentials : IZoomProfileCredentialStore
    {
        public bool HasPassword(string accountId) => false;
        public string ReferenceFor(string accountId) => $"wincred:{accountId}";
        public void Save(string accountId, string email, string password) { }
        public void Delete(string accountId) { }
    }

    private sealed class RolesStore(params SessionRoleProfile[] profiles) : ISessionRoleStore
    {
        private SessionRoleDocument _document = new() { Profiles = [.. profiles] };
        public SessionRoleDocument Load() => _document;
        public void Save(SessionRoleDocument document) => _document = document;
    }

    private sealed class RosterService(params string[] groups) : IGroupRosterService
    {
        private readonly List<RosterGroup> _groups =
            [.. groups.Select(name => new RosterGroup(name, name, DateTimeOffset.Now, []))];

        public Task<IReadOnlyList<RosterGroup>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<RosterGroup>>([.. _groups]);
        public Task CreateAsync(string groupId, string displayName, CancellationToken token = default) => Task.CompletedTask;
        public Task RenameAsync(RosterGroup expected, string displayName, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteAsync(RosterGroup expected, CancellationToken token = default) => Task.CompletedTask;
        public Task AddStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default) => Task.CompletedTask;
        public Task AddStudentsAsync(RosterGroup expected, IReadOnlyList<GroupStudent> students, CancellationToken token = default) => Task.CompletedTask;
        public Task UpdateStudentAsync(RosterGroup expected, GroupStudent student, CancellationToken token = default) => Task.CompletedTask;
        public Task DeleteStudentAsync(RosterGroup expected, string studentId, CancellationToken token = default) => Task.CompletedTask;
        public Task ReorderAsync(RosterGroup expected, IReadOnlyList<string> orderedStudentIds, CancellationToken token = default) => Task.CompletedTask;
        public Task<int> ImportAsync(RosterGroup expected, string path, CancellationToken token = default) => Task.FromResult(0);
    }

    private sealed class NoRosterDialogs : IGroupRosterDialogs
    {
        public string? SelectImportFile() => null;
        public bool ConfirmDeleteGroup(string groupId, int studentCount) => false;
        public bool ConfirmDeleteStudent(string studentId) => false;
    }
}
