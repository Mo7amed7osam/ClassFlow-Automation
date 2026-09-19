using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

public sealed class AccountsViewModel : ObservableObject
{
    private readonly IWindowsUiService _service;
    private readonly IZoomProfileCredentialStore _credentials;
    private readonly SignedInScope _scope;
    private WindowsMeetingAccountMetadata? _selectedAccount;
    private string _accountId = string.Empty;
    private string _displayName = string.Empty;
    private string _credentialReference = string.Empty;
    private string _zoomEmail = string.Empty;
    private string _groupName = string.Empty;
    private string _defaultMeetingUrl = string.Empty;
    private string _webProfileName = string.Empty;
    private EnginePreference _preferredEngine;
    private string _statusMessage = string.Empty;

    public AccountsViewModel(IWindowsUiService service, IZoomProfileCredentialStore? credentials = null,
        SignedInScope? scope = null)
    {
        _service = service;
        _credentials = credentials ?? new ZoomProfileCredentialStore();
        // One PC holds everybody's Zoom accounts; a coordinator signed in here sees only the ones
        // that host a group of theirs.
        _scope = scope ?? new SignedInScope(() => null);
        _scope.Changed += () => _ = RefreshAsync();
        NewCommand = new RelayCommand(_ => ClearEditor());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync());
        SwitchAccountCommand = new AsyncRelayCommand(_ => SwitchAccountAsync());
    }

    public event Action? AccountsChanged;
    public ObservableCollection<WindowsMeetingAccountMetadata> Items { get; } = [];
    public IReadOnlyList<EnginePreference> EnginePreferences { get; } = Enum.GetValues<EnginePreference>();
    public WindowsMeetingAccountMetadata? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value) || value == null) return;
            AccountId = value.AccountId;
            GroupName = value.GroupName ?? value.AccountId;
            DisplayName = value.DisplayName;
            CredentialReference = value.CredentialReference;
            ZoomEmail = value.ZoomEmail ?? string.Empty;
            DefaultMeetingUrl = value.DefaultMeetingUrl ?? string.Empty;
            WebProfileName = value.WebProfileName ?? string.Empty;
            StatusMessage = string.IsNullOrEmpty(ZoomEmail)
                ? "Legacy account: set Zoom Email explicitly and Save to verify the account mapping."
                : $"Selected {value.AccountId}: {ZoomEmail}";
            PreferredEngine = value.PreferredEngine switch
            {
                AccountEnginePreference.Desktop => EnginePreference.Desktop,
                AccountEnginePreference.Web => EnginePreference.Web,
                _ => EnginePreference.Auto
            };
            OnPropertyChanged(nameof(HasSavedPassword));
        }
    }
    public string AccountId { get => _accountId; set => SetProperty(ref _accountId, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string CredentialReference { get => _credentialReference; set => SetProperty(ref _credentialReference, value); }
    public string ZoomEmail { get => _zoomEmail; set => SetProperty(ref _zoomEmail, value); }
    public string GroupName { get => _groupName; set => SetProperty(ref _groupName, value); }
    public string DefaultMeetingUrl { get => _defaultMeetingUrl; set => SetProperty(ref _defaultMeetingUrl, value); }
    public string WebProfileName { get => _webProfileName; set => SetProperty(ref _webProfileName, value); }
    public EnginePreference PreferredEngine { get => _preferredEngine; set => SetProperty(ref _preferredEngine, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasSavedPassword => !string.IsNullOrWhiteSpace(AccountId) && SafeHasPassword(AccountId);
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SwitchAccountCommand { get; }

    /// <summary>What this PC is showing less of, and why. Empty when the whole list is shown.</summary>
    public string ScopeNote { get => _scopeNote; private set => SetProperty(ref _scopeNote, value); }
    private string _scopeNote = string.Empty;

    public async Task RefreshAsync()
    {
        var accounts = await _service.GetAccountsAsync();
        // An account hosts a group: its own GroupName, or failing that the id, which is the group
        // code on every account the app makes.
        var mine = accounts.Where(account => _scope.Owns(account.GroupName ?? account.AccountId)).ToArray();
        var keep = SelectedAccount?.AccountId;
        Items.Clear();
        foreach (var account in mine) Items.Add(account);
        ScopeNote = _scope.Narrowed(mine.Length, accounts.Count, "Zoom account(s)");
        // Somebody else's account was open when the sign-in changed: let go of it.
        if (keep != null && !Items.Any(a => a.AccountId.Equals(keep, StringComparison.OrdinalIgnoreCase)))
            ClearEditor();
    }

    public async Task SaveAsync()
    {
        try
        {
            if (SelectedAccount != null && !SelectedAccount.AccountId.Equals(AccountId.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Account ID is stable for schedules/profiles. Use New to create another account.");
            var email = WindowsMeetingAccountManager.NormalizeZoomEmail(ZoomEmail)
                ?? throw new ArgumentException("Zoom Email is required. Credential reference is not an account email.");
            AccountEnginePreference engine = PreferredEngine switch
            {
                EnginePreference.Desktop => AccountEnginePreference.Desktop,
                EnginePreference.Web => AccountEnginePreference.Web,
                _ => AccountEnginePreference.Auto
            };
            await _service.SaveAccountAsync(new WindowsMeetingAccountMetadata(
                AccountId.Trim(),
                DisplayName.Trim(),
                CredentialReference.Trim(),
                engine) { ZoomEmail = email,
                    GroupName = string.IsNullOrWhiteSpace(GroupName) ? AccountId.Trim() : GroupName.Trim(),
                    DefaultMeetingUrl = WindowsMeetingAccountManager.NormalizeDefaultMeetingUrl(DefaultMeetingUrl),
                    WebProfileName = WindowsMeetingAccountManager.NormalizeWebProfileName(WebProfileName) });
            await RefreshAsync();
            SelectedAccount = Items.First(account => account.AccountId.Equals(AccountId, StringComparison.OrdinalIgnoreCase));
            StatusMessage = HasSavedPassword
                ? "Profile saved. Its password is protected by Windows Credential Manager."
                : "Profile saved. Save a password if this Zoom profile needs one.";
            AccountsChanged?.Invoke();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    public void SavePassword(string? password)
    {
        try
        {
            var email = WindowsMeetingAccountManager.NormalizeZoomEmail(ZoomEmail)
                ?? throw new ArgumentException("Enter a valid Zoom Email before saving the password.");
            _credentials.Save(AccountId, email, password ?? string.Empty);
            CredentialReference = _credentials.ReferenceFor(AccountId);
            StatusMessage = "Zoom password saved in Windows Credential Manager. Save the profile to keep its reference.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        OnPropertyChanged(nameof(HasSavedPassword));
    }

    public void ForgetPassword()
    {
        try { _credentials.Delete(AccountId); StatusMessage = "The saved Zoom password was removed."; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        OnPropertyChanged(nameof(HasSavedPassword));
    }

    private bool SafeHasPassword(string accountId)
    {
        try { return _credentials.HasPassword(accountId); }
        catch { return false; }
    }

    public async Task DeleteAsync()
    {
        if (string.IsNullOrWhiteSpace(AccountId)) return;
        try
        {
            bool removed = await _service.DeleteAccountAsync(AccountId);
            if (removed) _credentials.Delete(AccountId);
            await RefreshAsync();
            ClearEditor();
            StatusMessage = removed ? "Account deleted." : "Account was not found.";
            if (removed) AccountsChanged?.Invoke();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    public async Task SwitchAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(AccountId))
        {
            StatusMessage = "Select an account first.";
            return;
        }
        if (SelectedAccount == null || !SelectedAccount.AccountId.Equals(AccountId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SelectedAccount.ZoomEmail ?? string.Empty, ZoomEmail.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SelectedAccount.CredentialReference, CredentialReference.Trim(), StringComparison.Ordinal))
        {
            StatusMessage = "Save account changes before switching; switching uses the saved Zoom Email.";
            return;
        }
        string requestedId = AccountId;
        StatusMessage = "Switching account...";
        var result = await _service.SwitchAccountAsync(requestedId);
        if (AccountId == requestedId) StatusMessage = result.Message;
    }

    private void ClearEditor()
    {
        SelectedAccount = null;
        AccountId = string.Empty;
        DisplayName = string.Empty;
        CredentialReference = string.Empty;
        ZoomEmail = string.Empty;
        GroupName = string.Empty;
        DefaultMeetingUrl = string.Empty;
        WebProfileName = string.Empty;
        PreferredEngine = EnginePreference.Auto;
        StatusMessage = string.Empty;
    }
}
