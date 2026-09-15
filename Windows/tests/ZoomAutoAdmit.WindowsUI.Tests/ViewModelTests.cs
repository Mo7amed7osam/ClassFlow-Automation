using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task StartMeetingPassesSelectedAccountUrlAndPreferenceToRuntimeService()
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("teacher-1"));
        var viewModel = new StartMeetingViewModel(service);
        await viewModel.RefreshAccountsAsync();
        viewModel.MeetingUrl = "https://zoom.us/j/123456789";
        viewModel.EnginePreference = EnginePreference.Web;

        await viewModel.StartAsync();

        Assert.Equal("teacher-1", service.StartAccountId);
        Assert.Equal("https://zoom.us/j/123456789", service.StartMeetingUrl);
        Assert.Equal(EnginePreference.Web, service.StartPreference);
        Assert.Contains("Web", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AccountEditorSavesGroupAndCredentialReference()
    {
        var service = new FakeWindowsUiService();
        var viewModel = new AccountsViewModel(service)
        {
            AccountId = "teacher-2",
            DisplayName = "Teacher Two",
            ZoomEmail = "teacher2@example.com",
            GroupName = "CAI5_AIS4_S7",
            CredentialReference = "wincred:ZoomAutoAdmit/teacher-2",
            PreferredEngine = EnginePreference.Desktop
        };

        await viewModel.SaveAsync();

        var saved = Assert.Single(service.Accounts);
        Assert.Equal("wincred:ZoomAutoAdmit/teacher-2", saved.CredentialReference);
        Assert.Equal("teacher2@example.com", saved.ZoomEmail);
        Assert.Equal("CAI5_AIS4_S7", saved.GroupName);
        Assert.Equal(AccountEnginePreference.Desktop, saved.PreferredEngine);
        Assert.Equal("Profile saved. Save a password if this Zoom profile needs one.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AccountEditorStoresPasswordOutsideMetadataAndSavesOnlyItsReference()
    {
        var service = new FakeWindowsUiService();
        var credentials = new FakeZoomCredentials();
        var viewModel = new AccountsViewModel(service, credentials)
        {
            AccountId = "zoom-s7", DisplayName = "S7 Zoom", GroupName = "CAI5_AIS4_S7",
            ZoomEmail = "teacher@example.com", DefaultMeetingUrl = "https://zoom.us/j/12345678901"
        };

        viewModel.SavePassword("test-only-secret");
        await viewModel.SaveAsync();

        Assert.True(viewModel.HasSavedPassword);
        Assert.Equal(("zoom-s7", "teacher@example.com", "test-only-secret"), credentials.Saved);
        var account = Assert.Single(service.Accounts);
        Assert.Equal("wincred:ZoomAutoAdmit/ZoomProfile/zoom-s7", account.CredentialReference);
        Assert.Equal("CAI5_AIS4_S7", account.GroupName);
        Assert.DoesNotContain("test-only-secret", account.ToString());
    }

    [Fact]
    public async Task DashboardDisplaysAndStopsActiveSession()
    {
        var service = new FakeWindowsUiService();
        var session = new SessionDisplayInfo(
            Guid.NewGuid(),
            "teacher-1",
            "Teacher One",
            SessionEngineType.Desktop,
            "Monitoring",
            DateTimeOffset.UtcNow);
        service.Sessions.Add(session);
        var viewModel = new DashboardViewModel(service);

        await viewModel.RefreshAsync();
        viewModel.StopCommand.Execute(session);
        await service.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(session.SessionId, service.StoppedSessionId);
    }

    [Fact]
    public async Task MainInitializationContinuesWhenAccountServiceFails()
    {
        var service = new FakeWindowsUiService { ThrowOnGetAccounts = true };
        var root = Path.Combine(Path.GetTempPath(), "main-ui-init-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var viewModel = new MainViewModel(service,
                roster: new ZoomAutoAdmit.Roster.StudentRosterStore(Path.Combine(root, "students.json")),
                groups: new ZoomAutoAdmit.Roster.GroupRosterStore(Path.Combine(root, "groups.json"), seedOnFirstUse: false));
            var exception = await Assert.ThrowsAsync<AggregateException>(viewModel.InitializeAsync);
            Assert.NotEmpty(exception.InnerExceptions);
            Assert.Equal(1, service.GetActiveSessionsCalls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SwitchAccountCommandShowsServiceResult()
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("teacher-1"));
        var viewModel = new AccountsViewModel(service);
        await viewModel.RefreshAsync();
        viewModel.SelectedAccount = viewModel.Items.Single();

        await viewModel.SwitchAccountAsync();

        Assert.Equal("teacher-1", service.SwitchedAccountId);
        Assert.Equal("Zoom Desktop account switch completed.", viewModel.StatusMessage);
    }

    [Fact]
    public void DashboardDisplaysCurrentActionAndErrors()
    {
        var service = new FakeWindowsUiService();
        using var viewModel = new DashboardViewModel(service);

        service.EmitStatus(new UiActionStatus(
            "Scheduled meeting",
            "Failed",
            "Account could not be loaded.",
            false,
            DateTimeOffset.Now));

        Assert.Equal("Scheduled meeting", viewModel.LastAction);
        Assert.Equal("Failed", viewModel.CurrentOperation);
        Assert.Equal("Account could not be loaded.", viewModel.ErrorMessage);
    }

    [Fact]
    public void SchedulesViewDisplaysRuntimeProgress()
    {
        var service = new FakeWindowsUiService();
        using var viewModel = new SchedulesViewModel(service);

        service.EmitStatus(new UiActionStatus(
            "Scheduled meeting",
            "Meeting start requested.",
            string.Empty,
            true,
            DateTimeOffset.Now));

        Assert.Equal("Meeting start requested.", viewModel.ExecutionStatus);
    }

    [Fact]
    public void EverySidebarPageIsReachable()
    {
        using var model = new MainViewModel(new FakeWindowsUiService());
        foreach (var item in model.Navigation)
        {
            model.SelectedTabIndex = item.Index;
            Assert.Equal(item.Index, model.SelectedTabIndex);      // A capped index would silently keep the old page.
            Assert.Equal(item.Title, model.PageTitle);
            Assert.True(item.Index < MainViewModel.TabCount, $"{item.Title} points past the last page.");
        }
    }

    [Fact]
    public void TheRecordingsPageIsInTheSidebar()
    {
        var temp = Directory.CreateTempSubdirectory("recordings-page-").FullName;
        try
        {
            var dashboard = new RecordingsDashboardViewModel(
                new RecordingsDashboardSettingsStore(Path.Combine(temp, "dashboard.json")),
                new DatabasePasswordStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid()),     // nothing saved there: never a real secret
                new CentralServerHost(new DatabasePasswordStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid())));
            using var model = new MainViewModel(new FakeWindowsUiService(), recordingsDashboard: dashboard);
            var item = Assert.Single(model.Navigation, n => n.Title == "Recordings");
            Assert.Equal("RECORDS", item.Section);
            model.NavigateCommand.Execute(item.Index.ToString());
            Assert.Equal("Recordings", model.PageTitle);
            Assert.Same(dashboard, model.RecordingsDashboard);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static WindowsMeetingAccountMetadata Account(string id) =>
        new(id, "Teacher One", $"wincred:ZoomAutoAdmit/{id}");

    [Fact]
    public async Task SelectingGroupLoadsItsSavedLinkAndClearsOtherGroupsLink()
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("S7") with { DefaultMeetingUrl = "https://zoom.us/j/123456789" });
        service.Accounts.Add(Account("S8") with { DefaultMeetingUrl = "https://zoom.us/j/987654321" });
        service.Accounts.Add(Account("new"));
        var vm = new StartMeetingViewModel(service);
        await vm.RefreshAccountsAsync();
        Assert.Equal("https://zoom.us/j/123456789", vm.MeetingUrl);
        vm.SelectedAccount = vm.Accounts[1];
        Assert.Equal("https://zoom.us/j/987654321", vm.MeetingUrl);
        vm.SelectedAccount = vm.Accounts[2];
        Assert.Empty(vm.MeetingUrl);
    }

    [Fact]
    public async Task AddingAnAccountFromStartMeetingSavesItSelectsItAndNotifiesOtherPages()
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("S7") with { ZoomEmail = "s7@example.com" });
        var vm = new StartMeetingViewModel(service);
        await vm.RefreshAccountsAsync();
        int notified = 0;
        vm.AccountsChanged += () => notified++;

        vm.NewAccountId = " S9 ";
        vm.NewZoomEmail = " S9@Example.com ";
        vm.NewDefaultMeetingUrl = "https://zoom.us/j/987654321";
        await vm.AddAccountAsync();

        var saved = service.Accounts.Single(account => account.AccountId == "S9");
        Assert.Equal("S9", saved.DisplayName); // Empty display name falls back to the ID, never to a blank picker entry.
        Assert.Equal("s9@example.com", saved.ZoomEmail);
        Assert.Equal("https://zoom.us/j/987654321", saved.DefaultMeetingUrl);
        Assert.Empty(saved.CredentialReference);
        Assert.Equal("S9", vm.SelectedAccount!.AccountId);
        Assert.Equal("https://zoom.us/j/987654321", vm.MeetingUrl);
        Assert.Equal(1, notified);
        Assert.Contains("Saved S9", vm.AddAccountStatus);
        Assert.Empty(vm.NewAccountId + vm.NewZoomEmail + vm.NewDefaultMeetingUrl + vm.NewDisplayName);
    }

    [Theory]
    [InlineData("", "s9@example.com", "Account ID is required")]
    [InlineData("S 9", "s9@example.com", "single word")]
    [InlineData("S7", "s9@example.com", "already exists")]
    [InlineData("S9", "", "Zoom Email is required")]
    [InlineData("S9", "not-an-email", "complete Zoom email")]
    public async Task IncompleteQuickAddNeverSavesAnAccount(string id, string email, string expected)
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("S7") with { ZoomEmail = "s7@example.com" });
        var vm = new StartMeetingViewModel(service);
        await vm.RefreshAccountsAsync();

        vm.NewAccountId = id;
        vm.NewZoomEmail = email;
        await vm.AddAccountAsync();

        Assert.Single(service.Accounts);
        Assert.Contains(expected, vm.AddAccountStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualLaunchOverrideDoesNotOverwriteStoredDefault()
    {
        var service = new FakeWindowsUiService();
        service.Accounts.Add(Account("S7") with { DefaultMeetingUrl = "https://zoom.us/j/123456789" });
        var vm = new StartMeetingViewModel(service);
        await vm.RefreshAccountsAsync();
        vm.MeetingUrl = "https://zoom.us/j/987654321";
        await vm.StartAsync();
        Assert.Equal(vm.MeetingUrl, service.StartMeetingUrl);
        Assert.Equal("https://zoom.us/j/123456789", service.Accounts.Single().DefaultMeetingUrl);
    }

    [Fact]
    public async Task EditingAccountDefaultRefreshesStartMeetingAndPreservesOtherFields()
    {
        var service = new FakeWindowsUiService();
        var account = Account("S7") with { ZoomEmail = "depi+20@eyouthlearning.com",
            DefaultMeetingUrl = "https://zoom.us/j/123456789" };
        service.Accounts.Add(account);
        var start = new StartMeetingViewModel(service);
        await start.RefreshAccountsAsync();
        var editor = new AccountsViewModel(service) { SelectedAccount = account };
        Assert.Equal(account.DefaultMeetingUrl, editor.DefaultMeetingUrl);
        editor.DefaultMeetingUrl = "https://zoom.us/j/987654321";
        await editor.SaveAsync();
        await start.RefreshAccountsAsync();
        Assert.Equal(editor.DefaultMeetingUrl, start.MeetingUrl);
        Assert.Equal(account.ZoomEmail, service.Accounts.Single().ZoomEmail);
        Assert.Equal(account.CredentialReference, service.Accounts.Single().CredentialReference);
        editor.NewCommand.Execute(null);
        Assert.Empty(editor.DefaultMeetingUrl);
    }

    [Fact]
    public async Task ChangingSelectedAccountClearsPreviousSwitchResult()
    {
        var service = new FakeWindowsUiService();
        var s7 = Account("S7") with { ZoomEmail = "depi+20@eyouthlearning.com" };
        var s8 = Account("S8") with { ZoomEmail = "depi+21@eyouthlearning.com" };
        var vm = new AccountsViewModel(service) { SelectedAccount = s8 };
        await vm.SwitchAccountAsync();
        vm.SelectedAccount = s7;
        Assert.Equal(s7.ZoomEmail, vm.ZoomEmail);
        Assert.Contains("S7", vm.StatusMessage);
        Assert.Contains("depi+20", vm.StatusMessage);
        Assert.DoesNotContain("completed", vm.StatusMessage);
    }

    [Fact]
    public async Task UnsavedEmailCannotSilentlySwitchUsingOldMapping()
    {
        var service = new FakeWindowsUiService();
        var vm = new AccountsViewModel(service) { SelectedAccount = Account("S7") };
        vm.ZoomEmail = "depi+20@eyouthlearning.com";
        await vm.SwitchAccountAsync();
        Assert.Null(service.SwitchedAccountId);
        Assert.Contains("Save", vm.StatusMessage);
    }

    [Fact]
    public async Task NewAccountRequiresVisibleEmailNotJustCredentialReference()
    {
        var service = new FakeWindowsUiService();
        var vm = new AccountsViewModel(service) { AccountId = "S7", DisplayName = "S7", CredentialReference = "wincred:old" };
        await vm.SaveAsync();
        Assert.Empty(service.Accounts);
        Assert.Contains("Zoom Email is required", vm.StatusMessage);
    }

    private sealed class FakeWindowsUiService : IWindowsUiService
    {
        public List<WindowsMeetingAccountMetadata> Accounts { get; } = [];
        public List<SessionDisplayInfo> Sessions { get; } = [];
        public string? StartAccountId { get; private set; }
        public string? StartMeetingUrl { get; private set; }
        public EnginePreference? StartPreference { get; private set; }
        public Guid? StoppedSessionId { get; private set; }
        public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOnGetAccounts { get; init; }
        public int GetActiveSessionsCalls { get; private set; }
        public string? SwitchedAccountId { get; private set; }
        public event Action<UiActionStatus>? StatusChanged;
        public event Action<LiveMeeting>? MeetingBecameLive;
        public UiActionStatus CurrentStatus { get; private set; } =
            new("Application startup", "Ready", string.Empty, false, DateTimeOffset.Now);

        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            ThrowOnGetAccounts
                ? Task.FromException<IReadOnlyList<WindowsMeetingAccountMetadata>>(new InvalidOperationException("Account source unavailable."))
                : Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>(Accounts.ToArray());

        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default)
        {
            Accounts.RemoveAll(candidate => candidate.AccountId == account.AccountId);
            Accounts.Add(account);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.RemoveAll(account => account.AccountId == accountId) > 0);

        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default)
        {
            SwitchedAccountId = accountId;
            EmitStatus(new UiActionStatus(
                "Switch account",
                "Zoom Desktop account switch completed.",
                string.Empty,
                false,
                DateTimeOffset.Now));
            return Task.FromResult(new UiOperationResult(true, "Zoom Desktop account switch completed."));
        }

        public Task<SessionDisplayInfo> StartMeetingAsync(
            string accountId,
            string meetingUrl,
            EnginePreference preference,
            CancellationToken cancellationToken = default)
        {
            StartAccountId = accountId;
            StartMeetingUrl = meetingUrl;
            StartPreference = preference;
            return Task.FromResult(new SessionDisplayInfo(
                Guid.NewGuid(),
                accountId,
                accountId,
                preference == EnginePreference.Web ? SessionEngineType.Web : SessionEngineType.Desktop,
                "Monitoring",
                DateTimeOffset.UtcNow));
        }

        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            StoppedSessionId = sessionId;
            Sessions.RemoveAll(session => session.SessionId == sessionId);
            StopObserved.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
        {
            GetActiveSessionsCalls++;
            return Task.FromResult<IReadOnlyList<SessionDisplayInfo>>(Sessions.ToArray());
        }

        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingSchedule>>(Array.Empty<MeetingSchedule>());

        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void EmitStatus(UiActionStatus status)
        {
            CurrentStatus = status;
            StatusChanged?.Invoke(status);
        }
    }

    private sealed class FakeZoomCredentials : IZoomProfileCredentialStore
    {
        public (string Account, string Email, string Password)? Saved { get; private set; }
        public bool HasPassword(string accountId) => Saved?.Account == accountId;
        public string ReferenceFor(string accountId) => $"wincred:ZoomAutoAdmit/ZoomProfile/{accountId}";
        public void Save(string accountId, string email, string password) => Saved = (accountId, email, password);
        public void Delete(string accountId) { if (Saved?.Account == accountId) Saved = null; }
    }
}
