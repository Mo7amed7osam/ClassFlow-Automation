using System.Text.Json;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.Inspector.Runtime;
using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

public sealed class WindowsRuntimeBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZoomAutoAdmitBootstrapperTests",
        Guid.NewGuid().ToString("N"));

    // The bootstrapper defaults to the user's real schedules file and the real Task Scheduler;
    // the tests always pass their own so nothing here can reach the user's meetings.
    private string SchedulesPath => Path.Combine(_root, "Schedules", "schedules.json");

    [Fact]
    public async Task BootstrapCreatesCompleteProductionDependencyGraph()
    {
        string accountsPath = CreateAccountsFile("teacher-1");
        string profilesRoot = Path.Combine(_root, "Profiles");

        await using var bootstrapper = new WindowsRuntimeBootstrapper(
            accountsPath,
            profilesRoot,
            new AlwaysResolvableCredentialReference(),
            SchedulesPath,
            new NoTaskScheduler());

        Assert.NotNull(bootstrapper.AccountManager);
        Assert.NotNull(bootstrapper.ProfileMapper);
        Assert.NotNull(bootstrapper.SessionCoordinator);
        Assert.NotNull(bootstrapper.RuntimeFactory);
        Assert.NotNull(bootstrapper.Orchestrator);
        Assert.NotNull(bootstrapper.ScheduleStore);
        Assert.NotNull(bootstrapper.Scheduler);
        Assert.NotNull(bootstrapper.LifecycleEvents);
        Assert.NotNull(bootstrapper.Attendance);
    }

    [Fact]
    public async Task BootstrapWiresAttendanceAndDisposesItBeforeRuntimeShutdown()
    {
        var store = new AttendanceTestStore();
        var bootstrapper = new WindowsRuntimeBootstrapper(CreateAccountsFile("teacher-1"),
            Path.Combine(_root, "Profiles"), new AlwaysResolvableCredentialReference(),
            SchedulesPath, new NoTaskScheduler(),
            attendanceSources: _ => new AttendanceTestSource(), attendanceStore: store);
        var meeting = new ZoomAutoAdmit.Core.Meetings.ScheduledMeeting(
            new Uri("https://zoom.us/j/12345678901"), "teacher-1", DateTimeOffset.UtcNow);
        var session = new ZoomAutoAdmit.Core.Meetings.MeetingSession(Guid.NewGuid(), meeting, DateTimeOffset.UtcNow);
        var context = new ZoomAutoAdmit.Core.Meetings.MeetingLaunchContext(session,
            new("teacher-1", "Teacher", "reference"), SessionEngineType.Desktop, null);
        await bootstrapper.LifecycleEvents.PublishAsync(context,
            ZoomAutoAdmit.Core.Meetings.MeetingLifecycleEventKind.Active);
        bootstrapper.LifecycleEvents.PublishAdmission(session.SessionId);
        await bootstrapper.Attendance.CaptureManualAsync(session.SessionId);
        await bootstrapper.DisposeAsync();
        Assert.Equal(new[] { "MeetingStart", "AdmitEvent", "Manual", "MeetingEnd" },
            store.Snapshots.Select(snapshot => snapshot.Reason));
    }

    private sealed class AttendanceTestSource : ZoomAutoAdmit.Attendance.IAttendanceParticipantSource
    {
        public ZoomAutoAdmit.Attendance.AttendanceSource Source => ZoomAutoAdmit.Attendance.AttendanceSource.Desktop;
        public Task<ZoomAutoAdmit.Attendance.ParticipantReadResult> ReadAsync(CancellationToken token) =>
            Task.FromResult(new ZoomAutoAdmit.Attendance.ParticipantReadResult([new("Participant")], true));
    }

    private sealed class AttendanceTestStore : ZoomAutoAdmit.Attendance.IAttendanceSnapshotStore
    {
        public System.Collections.Concurrent.ConcurrentQueue<ZoomAutoAdmit.Attendance.AttendanceSnapshot> Snapshots { get; } = new();
        public Task SaveAsync(ZoomAutoAdmit.Attendance.AttendanceSnapshot snapshot, CancellationToken token)
        {
            Snapshots.Enqueue(snapshot);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AccountConfigurationLoadsPreferredEngineAndCreatesIsolatedProfile()
    {
        string accountsPath = CreateAccountsFile("teacher-1", AccountEnginePreference.Web);
        string profilesRoot = Path.Combine(_root, "Profiles");
        var mapper = new WindowsAccountWebProfileMapper(profilesRoot);
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            mapper);

        var account = await manager.LoadAsync("TEACHER-1");

        Assert.NotNull(account);
        Assert.Equal("teacher-1", account.AccountId);
        Assert.Equal("Teacher One", account.DisplayName);
        Assert.Equal("wincred:ZoomAutoAdmit/teacher-1", account.CredentialReference);
        Assert.Equal(SessionEngineType.Web, account.PreferredEngine);
        Assert.True(Directory.Exists(Path.Combine(profilesRoot, "teacher-1")));
    }

    [Theory]
    [InlineData(AccountEnginePreference.Auto, null)]
    [InlineData(AccountEnginePreference.Desktop, SessionEngineType.Desktop)]
    [InlineData(AccountEnginePreference.Web, SessionEngineType.Web)]
    public async Task LoadAccountSupportsEveryConfiguredEnginePreference(
        AccountEnginePreference configured,
        SessionEngineType? expectedRuntimePreference)
    {
        string accountsPath = CreateAccountsFile("teacher-1", configured);
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            new WindowsAccountWebProfileMapper(Path.Combine(_root, "Profiles")));

        var account = await manager.LoadAsync("teacher-1");

        Assert.NotNull(account);
        Assert.Equal(expectedRuntimePreference, account.PreferredEngine);
    }

    [Fact]
    public async Task LegacyNullPreferenceStillLoadsAsAuto()
    {
        string accountsPath = CreateAccountsFile("teacher-1", null);
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            new WindowsAccountWebProfileMapper(Path.Combine(_root, "Profiles")));

        var account = await manager.LoadAsync("teacher-1");

        Assert.NotNull(account);
        Assert.Null(account.PreferredEngine);
    }

    [Theory]
    [InlineData(AccountEnginePreference.Auto)]
    [InlineData(AccountEnginePreference.Desktop)]
    [InlineData(AccountEnginePreference.Web)]
    public async Task SaveAccountPreservesSelectedEngine(AccountEnginePreference selected)
    {
        string accountsPath = Path.Combine(_root, "Accounts", "accounts.json");
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            new WindowsAccountWebProfileMapper(Path.Combine(_root, "Profiles")));

        await manager.UpsertAsync(new WindowsMeetingAccountMetadata(
            "teacher-1",
            "Teacher One",
            "wincred:ZoomAutoAdmit/teacher-1",
            selected));

        var saved = Assert.Single(await manager.ListConfiguredAsync());
        Assert.Equal(selected, saved.PreferredEngine);
    }

    [Fact]
    public void ProfileMappingReusesAccountDirectoryAndPreservesLegacyProfiles()
    {
        string profilesRoot = Path.Combine(_root, "Profiles");
        string legacyProfile = Path.Combine(profilesRoot, "Default");
        Directory.CreateDirectory(legacyProfile);
        File.WriteAllText(Path.Combine(legacyProfile, "marker.txt"), "keep");
        var mapper = new WindowsAccountWebProfileMapper(profilesRoot);

        string first = mapper.ResolveDirectory("teacher-1");
        string second = mapper.ResolveDirectory("teacher-1");

        Assert.Equal(first, second);
        Assert.Equal(Path.Combine(profilesRoot, "teacher-1"), first);
        Assert.True(File.Exists(Path.Combine(legacyProfile, "marker.txt")));
    }

    [Fact]
    public async Task MissingAccountFailsWithoutCreatingAProfile()
    {
        string accountsPath = CreateAccountsFile("teacher-1");
        string profilesRoot = Path.Combine(_root, "Profiles");
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            new WindowsAccountWebProfileMapper(profilesRoot));

        var account = await manager.LoadAsync("missing-account");

        Assert.Null(account);
        Assert.False(Directory.Exists(Path.Combine(profilesRoot, "missing-account")));
    }

    [Fact]
    public async Task AccountStoreSupportsAddEditAndDelete()
    {
        string accountsPath = Path.Combine(_root, "Accounts", "accounts.json");
        var manager = new WindowsMeetingAccountManager(
            accountsPath,
            new AlwaysResolvableCredentialReference(),
            new WindowsAccountWebProfileMapper(Path.Combine(_root, "Profiles")));

        await manager.UpsertAsync(new WindowsMeetingAccountMetadata(
            "teacher-1",
            "Teacher One",
            "wincred:ZoomAutoAdmit/teacher-1",
            AccountEnginePreference.Desktop));
        await manager.UpsertAsync(new WindowsMeetingAccountMetadata(
            "teacher-1",
            "Updated Teacher",
            "wincred:ZoomAutoAdmit/teacher-1",
            AccountEnginePreference.Web));

        var updated = Assert.Single(await manager.ListConfiguredAsync());
        Assert.Equal("Updated Teacher", updated.DisplayName);
        Assert.Equal(AccountEnginePreference.Web, updated.PreferredEngine);
        Assert.True(await manager.DeleteAsync("teacher-1"));
        Assert.Empty(await manager.ListConfiguredAsync());
    }

    private string CreateAccountsFile(
        string accountId,
        AccountEnginePreference? preferredEngine = AccountEnginePreference.Auto)
    {
        string accountsDirectory = Path.Combine(_root, "Accounts");
        Directory.CreateDirectory(accountsDirectory);
        string path = Path.Combine(accountsDirectory, "accounts.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            new WindowsMeetingAccountMetadata(
                accountId,
                "Teacher One",
                $"wincred:ZoomAutoAdmit/{accountId}",
                preferredEngine)
        }, new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class AlwaysResolvableCredentialReference : IWindowsCredentialReferenceResolver
    {
        public bool CanResolve(string credentialReference) => true;
    }

    private sealed class NoTaskScheduler : ZoomAutoAdmit.WindowsRuntime.Scheduling.IWindowsTaskScheduler
    {
        public Task RegisterTaskAsync(
            ZoomAutoAdmit.WindowsRuntime.Scheduling.MeetingSchedule schedule,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteTaskAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
