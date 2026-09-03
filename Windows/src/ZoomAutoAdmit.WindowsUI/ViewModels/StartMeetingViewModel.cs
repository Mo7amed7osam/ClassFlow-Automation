using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class StartMeetingViewModel : ObservableObject
{
    private readonly IWindowsUiService _service;
    private WindowsMeetingAccountMetadata? _selectedAccount;
    private string _meetingUrl = string.Empty;
    private EnginePreference _enginePreference;
    private string _statusMessage = string.Empty;
    private string _newAccountId = string.Empty;
    private string _newDisplayName = string.Empty;
    private string _newZoomEmail = string.Empty;
    private string _newDefaultMeetingUrl = string.Empty;
    private string _addAccountStatus = string.Empty;

    public StartMeetingViewModel(IWindowsUiService service)
    {
        _service = service;
        // Deliberately not an AsyncRelayCommand: a start can take a minute or two (account switch,
        // join verification, media preparation) and must not lock the button. The user may start
        // a Desktop meeting and a Web meeting back to back; each run reports under its own account.
        StartCommand = new RelayCommand(_ => _ = StartAsync());
        AddAccountCommand = new AsyncRelayCommand(_ => AddAccountAsync());
        ClearNewAccountCommand = new RelayCommand(_ => ClearNewAccount());
    }

    public event Action? MeetingStarted;
    public event Action? AccountsChanged;
    public ObservableCollection<WindowsMeetingAccountMetadata> Accounts { get; } = [];
    public IReadOnlyList<EnginePreference> EnginePreferences { get; } = Enum.GetValues<EnginePreference>();
    public WindowsMeetingAccountMetadata? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value)) return;
            // Never carry a different group's link into the newly selected account.
            MeetingUrl = value?.DefaultMeetingUrl ?? string.Empty;
            StatusMessage = value == null ? string.Empty : string.IsNullOrEmpty(MeetingUrl)
                ? "No saved meeting link. Enter one here, or save it permanently in Accounts."
                : "Saved group link loaded. Changes here apply to this launch only; edit Accounts to save a new default.";
        }
    }
    public string MeetingUrl { get => _meetingUrl; set => SetProperty(ref _meetingUrl, value); }
    public EnginePreference EnginePreference { get => _enginePreference; set => SetProperty(ref _enginePreference, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string NewAccountId { get => _newAccountId; set => SetProperty(ref _newAccountId, value); }
    public string NewDisplayName { get => _newDisplayName; set => SetProperty(ref _newDisplayName, value); }
    public string NewZoomEmail { get => _newZoomEmail; set => SetProperty(ref _newZoomEmail, value); }
    public string NewDefaultMeetingUrl { get => _newDefaultMeetingUrl; set => SetProperty(ref _newDefaultMeetingUrl, value); }
    public string AddAccountStatus { get => _addAccountStatus; private set => SetProperty(ref _addAccountStatus, value); }
    public ICommand StartCommand { get; }
    public ICommand AddAccountCommand { get; }
    public ICommand ClearNewAccountCommand { get; }

    /// <summary>Saves a new account from this page, with the same rules as the Accounts page. No password is stored.</summary>
    public async Task AddAccountAsync()
    {
        string accountId = NewAccountId.Trim();
        if (string.IsNullOrWhiteSpace(accountId)) { AddAccountStatus = "Account ID is required."; return; }
        if (accountId.Length > 64 || accountId.Any(char.IsWhiteSpace))
        { AddAccountStatus = "Account ID must be a single word, at most 64 characters."; return; }
        if (Accounts.Any(account => account.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase)))
        { AddAccountStatus = "An account with this ID already exists. Edit it in Accounts, or use another ID."; return; }
        try
        {
            AddAccountStatus = "Saving account...";
            var email = WindowsMeetingAccountManager.NormalizeZoomEmail(NewZoomEmail)
                ?? throw new ArgumentException("Zoom Email is required, exactly as it appears in Zoom.");
            await _service.SaveAccountAsync(new WindowsMeetingAccountMetadata(
                accountId,
                string.IsNullOrWhiteSpace(NewDisplayName) ? accountId : NewDisplayName.Trim(),
                string.Empty,
                AccountEnginePreference.Auto)
            {
                ZoomEmail = email,
                DefaultMeetingUrl = WindowsMeetingAccountManager.NormalizeDefaultMeetingUrl(NewDefaultMeetingUrl)
            });
            await RefreshAccountsAsync();
            SelectedAccount = Accounts.FirstOrDefault(account => account.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                ?? SelectedAccount;
            ClearNewAccount();
            AddAccountStatus = $"Saved {accountId} and selected it. Passwords are never stored here.";
            AccountsChanged?.Invoke();
        }
        catch (Exception ex) { AddAccountStatus = ex.Message; }
    }

    private void ClearNewAccount()
    {
        NewAccountId = string.Empty;
        NewDisplayName = string.Empty;
        NewZoomEmail = string.Empty;
        NewDefaultMeetingUrl = string.Empty;
        AddAccountStatus = string.Empty;
    }

    public async Task RefreshAccountsAsync()
    {
        string? selectedId = SelectedAccount?.AccountId;
        var accounts = await _service.GetAccountsAsync();
        Accounts.Clear();
        foreach (var account in accounts) Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(account => account.AccountId == selectedId) ?? Accounts.FirstOrDefault();
    }

    public async Task StartAsync()
    {
        if (SelectedAccount == null) { StatusMessage = "Select an account."; return; }
        if (string.IsNullOrWhiteSpace(MeetingUrl)) { StatusMessage = "Enter a meeting URL."; return; }
        // Capture the inputs now: the user may change the form to start the next meeting while
        // this one is still launching.
        string accountId = SelectedAccount.AccountId;
        string meetingUrl = MeetingUrl;
        var preference = EnginePreference;
        try
        {
            StatusMessage = $"{accountId}: starting meeting... You can start another meeting meanwhile.";
            var session = await _service.StartMeetingAsync(accountId, meetingUrl, preference);
            StatusMessage = $"{accountId}: meeting started with {session.EngineType}.";
            MeetingStarted?.Invoke();
        }
        catch (Exception ex) { StatusMessage = $"{accountId}: {ex.Message}"; }
    }
}
