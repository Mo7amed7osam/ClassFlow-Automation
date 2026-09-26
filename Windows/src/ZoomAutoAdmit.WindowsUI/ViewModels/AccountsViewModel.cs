using System.Collections.ObjectModel;
using System.Windows.Input;
using ZoomAutoAdmit.Core.Formatting;
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
            OnPropertyChanged(nameof(HasSavedPassword)); OnPropertyChanged(nameof(PasswordState));
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
    /// <summary>Said plainly: without a saved password a fresh browser profile cannot sign itself in to Zoom.</summary>
    public string PasswordState => HasSavedPassword
        ? "Saved on this PC - a browser profile signs itself in to Zoom with it."
        : "Not saved - this account cannot sign itself in to Zoom. Type its password and press Save password.";
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
                // A saved password decides the reference; a hand-typed one that names nothing
                // would leave the profile unable to sign itself in.
                SafeHasPassword(AccountId) ? _credentials.ReferenceFor(AccountId) : CredentialReference.Trim(),
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
            if (HasSavedPassword) _ = SignInProfileAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    /// <summary>
    /// Signs the account's browser profile in to Zoom and keeps it signed in: set by the window.
    /// Given the account and its profile folder, it answers what happened.
    /// </summary>
    public Func<string, string, Task<string>>? SignInToZoom { get; set; }

    private bool _signingIn;

    /// <summary>
    /// After a save: the profile is signed in to Zoom now, once, so later meetings, recordings and
    /// reports on it start signed in. A window opens only if Zoom wants a person to finish.
    /// </summary>
    private async Task SignInProfileAsync()
    {
        if (SignInToZoom == null || _signingIn || string.IsNullOrWhiteSpace(AccountId)) return;
        _signingIn = true;
        string account = AccountId.Trim();
        string profile = string.IsNullOrWhiteSpace(WebProfileName) ? account : WebProfileName.Trim();
        string saved = StatusMessage;
        StatusMessage = $"{saved} Signing the '{profile}' profile in to Zoom… if a window opens, finish the sign-in there (captcha or code).";
        try { StatusMessage = $"{saved} {await SignInToZoom(account, profile)}"; }
        catch (Exception ex) { StatusMessage = $"{saved} The '{profile}' profile could not be signed in to Zoom: {ex.Message}"; }
        finally { _signingIn = false; }
    }

    /// <summary>
    /// Sends a just-typed Zoom password to the database, under the signed-in dashboard account,
    /// and answers what happened. Null when this window has no server to send it to.
    /// </summary>
    public Func<string, string, Task<string>>? SaveToDatabase { get; set; }

    /// <summary>
    /// Keeps the account's Zoom password: on this PC, and - the part every other PC depends on -
    /// in the database against the signed-in dashboard account, encrypted there. The two are tried
    /// apart on purpose, so a PC whose Credential Manager refuses still puts the password where a
    /// cloud PC or a replacement can take it from. What was kept, and where, is said plainly.
    /// </summary>
    public async Task<bool> SavePasswordAsync(string? password)
    {
        string typed = password ?? string.Empty;
        if (typed.Length == 0) { StatusMessage = "Type the account's Zoom password first."; return false; }
        string email;
        try
        {
            email = WindowsMeetingAccountManager.NormalizeZoomEmail(ZoomEmail)
                ?? throw new ArgumentException("Enter a valid Zoom Email before saving the password.");
        }
        catch (Exception ex) { StatusMessage = ex.Message; return false; }

        string? hereProblem = null;
        try
        {
            _credentials.Save(AccountId, email, typed);
            CredentialReference = _credentials.ReferenceFor(AccountId);
        }
        catch (Exception ex) { hereProblem = ex.Message; }

        string? serverSaid = null;
        bool inDatabase = false;
        if (SaveToDatabase != null)
        {
            try { serverSaid = await SaveToDatabase(AccountId.Trim(), typed); inDatabase = true; }
            catch (Exception ex) { serverSaid = $"it did not reach the database ({CentralApiException.Explain(ex)})"; }
        }

        OnPropertyChanged(nameof(HasSavedPassword)); OnPropertyChanged(nameof(PasswordState));
        StatusMessage = (hereProblem == null ? "Zoom password saved on this PC" : $"This PC did not keep it ({hereProblem})")
                        + (serverSaid == null ? ". Sign in on the Dashboard page to keep it in the database too." : $"; {serverSaid}.");
        ConsoleLogger.Info($"[ACCOUNTS] {AccountId}: {StatusMessage}");
        if (hereProblem == null) _ = SignInProfileAsync();
        return hereProblem == null || inDatabase;
    }

    /// <summary>The account's saved Zoom password, for "Show password"; null when none is saved.</summary>
    public string? SavedPassword()
    {
        if (string.IsNullOrWhiteSpace(AccountId)) return null;
        try { return ZoomAutoAdmit.WebAutomation.ZoomSignInCredential.ReadFor(null, AccountId.Trim())?.Password; }
        catch { return null; }
    }

    public void ForgetPassword()
    {
        try { _credentials.Delete(AccountId); StatusMessage = "The saved Zoom password was removed."; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        OnPropertyChanged(nameof(HasSavedPassword)); OnPropertyChanged(nameof(PasswordState));
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
