using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

/// <summary>
/// The Schedules page with several people's classes on it: whose each one is, showing one person's
/// at a time, and what they open with.
/// </summary>
public sealed class SchedulesCoordinatorTests
{
    private static MeetingSchedule Class(string name, string? coordinator, string? coordinatorId = null,
        SessionEngineType? engine = null, DayOfWeek day = DayOfWeek.Tuesday) =>
        new(Guid.NewGuid(), name, "https://zoom.us/j/91473108490", "CAI5_AIS4_S7", new TimeOnly(19, 0),
            ScheduleDays.None, true, OccurrenceDate: NextDay(day), GroupName: "CAI5_AIS4_S7",
            PreferredEngine: engine, Coordinator: coordinator, CoordinatorId: coordinatorId);

    private static DateOnly NextDay(DayOfWeek day)
    {
        var date = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        while (date.DayOfWeek != day) date = date.AddDays(1);
        return date;
    }

    private static async Task<(SchedulesViewModel Vm, UiService Service)> LoadedAsync(params MeetingSchedule[] schedules)
    {
        var service = new UiService();
        foreach (var schedule in schedules) await service.SaveScheduleAsync(schedule);
        var vm = new SchedulesViewModel(service, classes: NoClaims());
        await vm.RefreshAsync();
        return (vm, service);
    }

    /// <summary>A group-to-coordinator file of its own, so a test never reads this PC's.</summary>
    private static ClassLmsAccounts NoClaims() =>
        new(Path.Combine(Path.GetTempPath(), "ZoomScheduleScope", Guid.NewGuid().ToString("N"), "class-accounts.json"));

    [Fact]
    public async Task WithOnlyThisPcsOwnClassesThereIsNobodyToChooseBetween()
    {
        var (vm, _) = await LoadedAsync(Class("My class", null));
        using (vm)
        {
            Assert.False(vm.HasCoordinators);
            Assert.Equal([SchedulesViewModel.Everyone, SchedulesViewModel.ThisPc], vm.Coordinators);
            Assert.Single(vm.FilteredItems);
        }
    }

    [Fact]
    public async Task EveryCoordinatorWithAClassHereCanBeShownOnTheirOwn()
    {
        var (vm, _) = await LoadedAsync(
            Class("S7 · 36", "Mona", "u-mona"), Class("S8 · 33", "Sami", "u-sami"), Class("My own", null));
        using (vm)
        {
            Assert.True(vm.HasCoordinators);
            Assert.Equal([SchedulesViewModel.Everyone, SchedulesViewModel.ThisPc, "Mona", "Sami"], vm.Coordinators);
            Assert.Equal(3, vm.FilteredItems.Count);

            vm.CoordinatorFilter = "Mona";
            Assert.Equal("S7 · 36", Assert.Single(vm.FilteredItems).Name);
            Assert.Equal("Showing 1 of 3 schedules.", vm.FilterSummary);

            vm.CoordinatorFilter = SchedulesViewModel.ThisPc;
            Assert.Equal("My own", Assert.Single(vm.FilteredItems).Name);

            vm.CoordinatorFilter = SchedulesViewModel.Everyone;
            Assert.Equal(3, vm.FilteredItems.Count);
        }
    }

    [Fact]
    public async Task ThePersonAndTheDayNarrowItTogether()
    {
        var (vm, _) = await LoadedAsync(
            Class("Mona Tuesday", "Mona", "u-mona", day: DayOfWeek.Tuesday),
            Class("Mona Thursday", "Mona", "u-mona", day: DayOfWeek.Thursday),
            Class("Sami Tuesday", "Sami", "u-sami", day: DayOfWeek.Tuesday));
        using (vm)
        {
            vm.CoordinatorFilter = "Mona";
            vm.ScheduleFilter = "Tuesday";
            Assert.Equal("Mona Tuesday", Assert.Single(vm.FilteredItems).Name);
        }
    }

    [Fact]
    public async Task WhenSomebodysClassesAreGoneTheListGoesBackToEveryone()
    {
        var (vm, service) = await LoadedAsync(Class("S7 · 36", "Mona", "u-mona"), Class("S8 · 33", "Sami", "u-sami"));
        using (vm)
        {
            vm.CoordinatorFilter = "Mona";
            foreach (var gone in service.Schedules.Where(s => s.Coordinator == "Mona").ToArray())
                await service.DeleteScheduleAsync(gone.Id);
            await vm.RefreshAsync();

            Assert.DoesNotContain("Mona", vm.Coordinators);
            Assert.Equal(SchedulesViewModel.Everyone, vm.CoordinatorFilter);
            Assert.Single(vm.FilteredItems);
        }
    }

    [Fact]
    public async Task EditingSomebodysClassByHandLeavesItTheirs()
    {
        var (vm, service) = await LoadedAsync(Class("S7 · 36", "Mona", "u-mona"));
        using (vm)
        {
            vm.SelectedSchedule = vm.FilteredItems[0];
            vm.Name = "S7 · 36 (moved)";
            vm.Time = "20:00";
            await vm.SaveAsync();

            var saved = Assert.Single(service.Schedules);
            Assert.Equal("S7 · 36 (moved)", saved.Name);
            Assert.Equal(new TimeOnly(20, 0), saved.Time);
            Assert.Equal("Mona", saved.Coordinator);
            Assert.Equal("u-mona", saved.CoordinatorId);
        }
    }

    [Fact]
    public async Task WhatTheShownClassesOpenWithIsSetForAllOfThemAtOnce()
    {
        var (vm, service) = await LoadedAsync(
            Class("Mona 1", "Mona", "u-mona"), Class("Mona 2", "Mona", "u-mona", engine: SessionEngineType.Desktop),
            Class("Sami 1", "Sami", "u-sami"));
        using (vm)
        {
            vm.CoordinatorFilter = "Mona";
            vm.OpensWith = "Web (browser)";
            await vm.SetOpensWithForShownAsync();

            Assert.All(service.Schedules.Where(s => s.Coordinator == "Mona"),
                s => Assert.Equal(SessionEngineType.Web, s.PreferredEngine));
            // Nobody else's class was touched.
            Assert.Null(service.Schedules.Single(s => s.Coordinator == "Sami").PreferredEngine);
            Assert.Contains("2 schedules now open with Web (browser)", vm.StatusMessage);
        }
    }

    [Fact]
    public async Task SettingWhatTheyOpenWithTwiceSaysThereWasNothingToDo()
    {
        var (vm, _) = await LoadedAsync(Class("Mona 1", "Mona", "u-mona", engine: SessionEngineType.Web));
        using (vm)
        {
            vm.OpensWith = "Web (browser)";
            await vm.SetOpensWithForShownAsync();
            Assert.Contains("already opens with Web (browser)", vm.StatusMessage);
        }
    }

    private sealed class UiService : IWindowsUiService
    {
        public List<MeetingSchedule> Schedules { get; } = [];
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.Now);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([new("CAI5_AIS4_S7", "S7", "")]);
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

    // ------------------------------------------------------------------ whose class is this, really

    [Fact]
    public async Task AClassImportedHereIsNamedForTheCoordinatorWhoseGroupItIs()
    {
        // The 98 classes a person imports from Excel say nothing about who owns them. Turning that
        // coordinator on is what makes the group theirs - and from then on the class already goes
        // up on the LMS under their account, because that is decided by the group.
        var claims = NoClaims();
        claims.SetGroups("u-mona", "Mona", "mona", ["CAI5_IND1_G1"]);
        var service = new UiService();
        await service.SaveScheduleAsync(Class("CAI5_IND1_G1 • 29", null) with { GroupName = "CAI5_IND1_G1" });
        await service.SaveScheduleAsync(Class("CAI5_AIS4_S7 • 36", null) with { GroupName = "CAI5_AIS4_S7" });

        using var vm = new SchedulesViewModel(service, classes: claims);
        await vm.RefreshAsync();

        Assert.Equal("Mona", vm.Items.Single(s => s.GroupName == "CAI5_IND1_G1").Coordinator);
        Assert.Null(vm.Items.Single(s => s.GroupName == "CAI5_AIS4_S7").Coordinator);
        Assert.Contains("Mona", vm.Coordinators);
    }

    [Fact]
    public async Task AClassThatAlreadySaysWhoseItIsIsNotRenamed()
    {
        var claims = NoClaims();
        claims.SetGroups("u-sami", "Sami", "sami", ["CAI5_IND1_G1"]);
        var service = new UiService();
        await service.SaveScheduleAsync(Class("theirs", "Mona", "u-mona") with { GroupName = "CAI5_IND1_G1" });

        using var vm = new SchedulesViewModel(service, classes: claims);
        await vm.RefreshAsync();

        // What the run plan put on the class wins over a later claim on the group.
        Assert.Equal("Mona", Assert.Single(vm.Items).Coordinator);
    }

    [Fact]
    public async Task ACoordinatorSignedInHereSeesOnlyTheirOwnClasses()
    {
        var service = new UiService();
        await service.SaveScheduleAsync(Class("mine", null) with { GroupName = "CAI5_IND1_G1" });
        await service.SaveScheduleAsync(Class("theirs", null) with { GroupName = "CAI5_AIS4_S7" });
        var me = new CentralMe("u-mona", "mona", "Mona", "coordinator", false,
            [new CentralGroupRef("g1", "CAI5_IND1_G1", null, false)], DateTimeOffset.Now.AddDays(1));

        using var vm = new SchedulesViewModel(service, scope: new SignedInScope(() => me), classes: NoClaims());
        await vm.RefreshAsync();

        Assert.Equal("CAI5_IND1_G1", Assert.Single(vm.Items).GroupName);
        Assert.Contains("1 on this PC belong to somebody else", vm.ScopeNote);
    }

    [Fact]
    public async Task TheAdminStillSeesEveryClassOnThisPc()
    {
        var service = new UiService();
        await service.SaveScheduleAsync(Class("mine", null) with { GroupName = "CAI5_IND1_G1" });
        await service.SaveScheduleAsync(Class("theirs", null) with { GroupName = "CAI5_AIS4_S7" });
        var admin = new CentralMe("u-admin", "admin", "The Admin", "admin", true, null, DateTimeOffset.Now.AddDays(1));

        using var vm = new SchedulesViewModel(service, scope: new SignedInScope(() => admin), classes: NoClaims());
        await vm.RefreshAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("", vm.ScopeNote);
    }
}
