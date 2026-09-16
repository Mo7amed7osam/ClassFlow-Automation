using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.ViewModels;

/// <summary>A group with a tick, for choosing a coordinator's groups.</summary>
public sealed class GroupChoice(string id, string name) : ObservableObject
{
    private bool _isChecked;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}

/// <summary>
/// The central server's records inside the app, replacing the web page: the recordings (Drive,
/// Zoom or missing, and where each stands on the LMS), and for the admin the accounts and groups.
/// Talks to the same API with the same roles: a coordinator sees and changes only their groups.
/// </summary>
public sealed class CentralViewModel : ObservableObject
{
    private readonly CentralApiClient _api;
    private string _status = "", _username = "", _signedInAs = "";
    private bool _isSignedIn, _isAdmin, _isBusy;
    private string _filterGroup = "", _filterLms = "", _filterLink = "";
    private CentralRecording? _selectedRecording;
    private CentralUser? _selectedUser;
    private string _editLink = "", _newGroupName = "", _newUsername = "", _newDisplayName = "";

    public CentralViewModel(CentralApiClient? api = null)
    {
        _api = api ?? new CentralApiClient();
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        SignOutCommand = new RelayCommand(_ => SignOut());
        AttachCommand = new AsyncRelayCommand(p => AttachAsync(p as CentralRecording, replace: false, dryRun: false));
        DryRunCommand = new AsyncRelayCommand(p => AttachAsync(p as CentralRecording, replace: false, dryRun: true));
        ReplaceCommand = new AsyncRelayCommand(p => AttachAsync(p as CentralRecording, replace: true, dryRun: false));
        CancelJobCommand = new AsyncRelayCommand(p => RunAsync(async () => { await _api.CancelJobAsync(((CentralRecording)p!).Id); return "The queued job was cancelled."; }));
        OpenLinkCommand = new RelayCommand(p => { if ((p as CentralRecording)?.Link is { } l) Process.Start(new ProcessStartInfo(l) { UseShellExecute = true }); });
        SaveLinkCommand = new AsyncRelayCommand(_ => RunAsync(async () =>
        {
            if (SelectedRecording == null || string.IsNullOrWhiteSpace(EditLink)) return "Choose a recording and paste its link.";
            await _api.EditLinkAsync(SelectedRecording.Id, EditLink.Trim());
            EditLink = "";
            return "The link was saved; the recording is Pending again until it is attached.";
        }));
        CreateGroupCommand = new AsyncRelayCommand(_ => RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(NewGroupName)) return "Type the group's name, e.g. CAI5_AIS4_S9.";
            await _api.CreateGroupAsync(NewGroupName.Trim(), null);
            NewGroupName = "";
            return "Group added.";
        }));
        ArchiveGroupCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            var g = (CentralGroup)p!;
            await _api.ArchiveGroupAsync(g.Id, !g.Archived);
            return g.Archived ? $"{g.Group} is active again." : $"{g.Group} was archived.";
        }));
        ApproveCommand = new AsyncRelayCommand(p => RunAsync(async () => { await _api.ApproveAsync(((CentralUser)p!).Id, true); return "Approved."; }));
        RejectCommand = new AsyncRelayCommand(p => RunAsync(async () => { await _api.ApproveAsync(((CentralUser)p!).Id, false); return "Rejected."; }));
        ToggleDisabledCommand = new AsyncRelayCommand(p => RunAsync(async () =>
        {
            var u = (CentralUser)p!;
            await _api.SetUserStatusAsync(u.Id, u.Status == "disabled" ? "active" : "disabled");
            return u.Status == "disabled" ? $"{u.Username} can sign in again." : $"{u.Username} was disabled and signed out.";
        }));
        SaveUserGroupsCommand = new AsyncRelayCommand(_ => RunAsync(async () =>
        {
            if (SelectedUser == null) return "Choose a coordinator first.";
            await _api.SetUserGroupsAsync(SelectedUser.Id, GroupChoices.Where(c => c.IsChecked).Select(c => c.Id));
            return $"{SelectedUser.Username}'s groups were saved.";
        }));
    }

    /// <summary>The one connection to the central server, shared with the Dashboard and Sessions pages.</summary>
    public CentralApiClient Api => _api;

    public ObservableCollection<CentralRecording> Recordings { get; } = [];
    public ObservableCollection<CentralGroup> Groups { get; } = [];
    public ObservableCollection<CentralUser> Users { get; } = [];
    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];
    public ObservableCollection<string> GroupNames { get; } = [];
    public IReadOnlyList<string> LmsFilters { get; } = ["", "pending", "attached", "failed"];
    public IReadOnlyList<string> LinkFilters { get; } = ["", "drive", "zoom", "missing"];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string SignedInAs { get => _signedInAs; private set => SetProperty(ref _signedInAs, value); }
    public bool IsSignedIn { get => _isSignedIn; private set => SetProperty(ref _isSignedIn, value); }
    public bool IsAdmin { get => _isAdmin; private set => SetProperty(ref _isAdmin, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string FilterGroup { get => _filterGroup; set { if (SetProperty(ref _filterGroup, value ?? "")) _ = RefreshAsync(); } }
    public string FilterLms { get => _filterLms; set { if (SetProperty(ref _filterLms, value ?? "")) _ = RefreshAsync(); } }
    public string FilterLink { get => _filterLink; set { if (SetProperty(ref _filterLink, value ?? "")) _ = RefreshAsync(); } }
    public CentralRecording? SelectedRecording { get => _selectedRecording; set { if (SetProperty(ref _selectedRecording, value)) EditLink = value?.Link ?? ""; } }
    public CentralUser? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (!SetProperty(ref _selectedUser, value)) return;
            foreach (var c in GroupChoices) c.IsChecked = value?.Groups.Any(g => g.Id == c.Id) == true;
        }
    }
    public string EditLink { get => _editLink; set => SetProperty(ref _editLink, value); }
    public string NewGroupName { get => _newGroupName; set => SetProperty(ref _newGroupName, value); }
    public string NewUsername { get => _newUsername; set => SetProperty(ref _newUsername, value); }
    public string NewDisplayName { get => _newDisplayName; set => SetProperty(ref _newDisplayName, value); }

    public int Total { get; private set; }
    public int OnLms { get; private set; }
    public int Pending { get; private set; }
    public int DriveCount { get; private set; }
    public int ZoomOnly { get; private set; }
    public int Missing { get; private set; }
    public int PendingUsers { get; private set; }

    public ICommand RefreshCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand AttachCommand { get; }
    public ICommand DryRunCommand { get; }
    public ICommand ReplaceCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand OpenLinkCommand { get; }
    public ICommand SaveLinkCommand { get; }
    public ICommand CreateGroupCommand { get; }
    public ICommand ArchiveGroupCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand ToggleDisabledCommand { get; }
    public ICommand SaveUserGroupsCommand { get; }

    // ------------------------------------------------------------------ sign-in (password from code-behind)

    /// <param name="savePassword">Keep the password in Windows Credential Manager, so Continue works after the session ends.</param>
    public async Task SignInAsync(string password, bool remember, bool savePassword = false)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(password)) { Status = "Username and password are required."; return; }
        CentralMe me;
        try
        {
            IsBusy = true;
            me = await _api.SignInAsync(Username, password, remember, savePassword);
        }
        catch (Exception ex) { Status = CentralApiException.Explain(ex); return; }
        finally { IsBusy = false; }
        ShowMe(me);
        // Not busy any more: RefreshAsync skips itself while busy, which left the accounts, groups and
        // recordings unread after every sign-in with a password.
        await RefreshAsync();
    }

    private void SignOut()
    {
        _api.Forget();
        IsSignedIn = IsAdmin = false;
        SignedInAs = "";
        Recordings.Clear(); Users.Clear(); Groups.Clear();
        Status = "Signed out. The saved sign-in was removed from Windows.";
    }

    private void ShowMe(CentralMe me)
    {
        IsSignedIn = true;
        IsAdmin = me.IsAdmin;
        SignedInAs = $"{me.DisplayName} ({me.Username}, {me.Role})";
        Username = me.Username;
    }

    // ------------------------------------------------------------------ data

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var me = await _api.EnsureSignedInAsync();
            if (me == null) { IsSignedIn = false; Status = "Sign in with your dashboard account (admin or coordinator)."; return; }
            ShowMe(me);

            var groups = await _api.GroupsAsync(me.IsAdmin);
            Groups.Clear();
            foreach (var g in groups.OrderBy(g => g.Archived).ThenBy(g => g.Group)) Groups.Add(g);
            string keep = FilterGroup;
            GroupNames.Clear();
            GroupNames.Add("");
            foreach (var g in groups.Where(g => !g.Archived).Select(g => g.Group).Order()) GroupNames.Add(g);
            _filterGroup = GroupNames.Contains(keep) ? keep : "";
            OnPropertyChanged(nameof(FilterGroup));

            var page = await _api.RecordingsAsync(FilterGroup, FilterLms, FilterLink);
            Recordings.Clear();
            foreach (var r in page.Items) Recordings.Add(r);
            Total = page.Total;
            OnLms = page.Items.Count(r => r.LmsStatus == "attached");
            Pending = page.Items.Count(r => r.LmsStatus == "pending");
            DriveCount = page.Items.Count(r => r.LinkStatus.Link == "drive");
            ZoomOnly = page.Items.Count(r => r.LinkStatus.Link == "zoom");
            Missing = page.Items.Count(r => r.LinkStatus.Link == "missing");

            Users.Clear();
            GroupChoices.Clear();
            if (me.IsAdmin)
            {
                var users = await _api.UsersAsync();
                foreach (var u in users.Users) Users.Add(u);
                foreach (var g in groups.Where(g => !g.Archived)) GroupChoices.Add(new GroupChoice(g.Id, g.Group));
                PendingUsers = users.Users.Count(u => u.Status == "pending");
            }
            foreach (var name in new[] { nameof(Total), nameof(OnLms), nameof(Pending), nameof(DriveCount), nameof(ZoomOnly), nameof(Missing), nameof(PendingUsers) })
                OnPropertyChanged(name);
            Status = $"Updated {DateTime.Now:HH:mm}: {page.Total} recording(s){(me.IsAdmin ? $", {Users.Count} account(s)" : "")}.";
        }
        catch (Exception ex) { Status = CentralApiException.Explain(ex); }
        finally { IsBusy = false; }
    }

    private async Task AttachAsync(CentralRecording? recording, bool replace, bool dryRun)
    {
        if (recording == null) return;
        await RunAsync(async () =>
        {
            await _api.AttachAsync(recording.Id, replace, dryRun);
            return dryRun ? $"{recording.Group} {recording.Date}: a dry run was queued (nothing is saved on the LMS)."
                : $"{recording.Group} {recording.Date}: queued for the LMS{(replace ? ", replacing the link there" : "")}. The agent does it now.";
        });
    }

    /// <summary>Creates a coordinator; the password comes from the PasswordBox (code-behind).</summary>
    public Task CreateUserAsync(string password) => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || password.Length < 12) return "A username and a password of 12 characters or more are needed.";
        await _api.CreateUserAsync(NewUsername.Trim(), string.IsNullOrWhiteSpace(NewDisplayName) ? NewUsername.Trim() : NewDisplayName.Trim(),
            password, GroupChoices.Where(c => c.IsChecked).Select(c => c.Id));
        string made = NewUsername.Trim();
        NewUsername = NewDisplayName = "";
        return $"{made} was created with the ticked groups. Send them the app and this sign-in.";
    });

    public Task ResetPasswordAsync(string password) => RunAsync(async () =>
    {
        if (SelectedUser == null || password.Length < 12) return "Choose an account and type a new password of 12 characters or more.";
        await _api.ResetPasswordAsync(SelectedUser.Id, password);
        return $"{SelectedUser.Username} has a new password and was signed out.";
    });

    private async Task RunAsync(Func<Task<string>> action)
    {
        try { Status = await action(); }
        catch (Exception ex) { Status = CentralApiException.Explain(ex); }
        await RefreshAsync();
    }
}
