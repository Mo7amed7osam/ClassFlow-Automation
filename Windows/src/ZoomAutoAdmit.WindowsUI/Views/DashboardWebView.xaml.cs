using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using static ZoomAutoAdmit.WindowsUI.Views.WebPageBridge;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// The Dashboard (React, WebSessions/dashboard.html): who is signed in to the central server and as
/// what (admin or coordinator), the LMS account that person works with, every session at a glance
/// with Check all, and - for the admin - the coordinators: approve, add, their groups.
/// Everything goes through the app's own view models; passwords go to the server or to Windows
/// Credential Manager and are never kept here.
/// </summary>
public partial class DashboardWebView : UserControl
{
    private MainViewModel? _main;
    private WebPageBridge? _bridge;
    private LmsServerAccounts? _serverLms;
    private Task? _initializing;

    /// <summary>"home" (the Dashboard) or "coordinators" (the Coordinators & groups page).</summary>
    public string View { get; set; } = "home";

    public DashboardWebView()
    {
        InitializeComponent();
        Loaded += (_, _) => _initializing ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _main = DataContext as MainViewModel ?? throw new InvalidOperationException("The app is not loaded.");
            _bridge = new WebPageBridge(Browser, "dashboard.zoomautoadmit", "WebSessions", $"dashboard.html?view={View}", HandleAsync, BuildState);
            _main.Central.PropertyChanged += (_, _) => _bridge.Schedule();
            _main.LmsSessions.Changed += _bridge.Schedule;
            _main.LmsSessions.PropertyChanged += (_, _) => _bridge.Schedule();
            _main.RecordingsDashboard.PropertyChanged += (_, _) => _bridge.Schedule();
            await _bridge.InitializeAsync();
            Placeholder.Visibility = Visibility.Collapsed;
            await _main.Central.RefreshAsync();
            await SyncLmsAsync();
        }
        catch (Exception ex)
        {
            Placeholder.Text = "The Dashboard could not be opened: " + ex.Message;
            WindowsUiErrorLog.Write("The Dashboard could not be opened.", ex);
        }
    }

    /// <summary>
    /// Signed in: the user's LMS accounts come from the database, and the one they use becomes this
    /// PC's LMS sign-in. Not signed in: this PC's own accounts, as before.
    /// </summary>
    private async Task<string?> SyncLmsAsync()
    {
        var main = _main!;
        if (!main.Central.IsSignedIn) { _serverLms = null; return null; }
        _serverLms ??= new LmsServerAccounts(main.Central.Api);
        try
        {
            string? message = await _serverLms.SyncAsync();
            main.LmsSessions.ReloadAccounts();
            return message;
        }
        catch (Exception ex) { return $"Your LMS accounts could not be read from the server: {ex.Message}"; }
    }

    private async Task<object?> HandleAsync(string method, JsonElement p)
    {
        var main = _main!;
        var central = main.Central;
        var sessions = main.LmsSessions;
        switch (method)
        {
            case "refresh":
                await central.RefreshAsync(); await sessions.ReloadAsync();
                return Done(central.Status);
            case "signIn":
                central.Username = Text(p, "username");
                await central.SignInAsync(Text(p, "password"), Flag(p, "remember"), Flag(p, "savePassword"));
                string? lms = await SyncLmsAsync();
                return new { ok = central.IsSignedIn, message = central.IsSignedIn ? $"Signed in as {central.SignedInAs}. {lms}" : central.Status };
            case "switchAccount":
            {
                var me = await central.Api.SwitchToAsync(Text(p, "username"));
                if (me == null) return new { ok = false, message = $"{Text(p, "username")}'s session has ended and no working password is saved; type the password once to continue." };
                await central.RefreshAsync();
                string? lmsNow = await SyncLmsAsync();
                return Done($"Continuing as {me.DisplayName} ({me.Role}). {lmsNow}");
            }
            case "forgetAccount":
                central.Api.ForgetAccount(Text(p, "username"));
                await central.RefreshAsync();
                _serverLms = central.IsSignedIn ? _serverLms : null;
                return Done($"{Text(p, "username")} was removed from this PC.");
            case "signOut":
                central.SignOutCommand.Execute(null);
                _serverLms = null;
                return Done("Signed out; the session was ended on the server.");
            case "useLms":
                if (_serverLms != null) { string? used = await _serverLms.UseAsync(Text(p, "id")); sessions.ReloadAccounts(); return Done(used ?? "Saved."); }
                sessions.UseAccount(Text(p, "id"));
                return Done(sessions.Status);
            case "saveLms":
            {
                // The LMS account is the signed-in person's, so its role is theirs; before anyone signs
                // in, the server PC's is the admin's and a coordinator's PC's a coordinator's.
                string role = central.IsSignedIn ? (central.IsAdmin ? "admin" : "coordinator")
                    : main.RecordingsDashboard.IsClientMode ? "coordinator" : "admin";
                if (_serverLms != null)
                {
                    string? saved = await _serverLms.SaveAsync(Text(p, "label"), Text(p, "email"), role, Text(p, "password"), Flag(p, "makeActive"));
                    sessions.ReloadAccounts();
                    return Done($"Saved in the database (the password encrypted). {saved}");
                }
                sessions.SaveAccount(Text(p, "label"), Text(p, "email"), role, Text(p, "password"), Flag(p, "makeActive"));
                return Done(sessions.Status);
            }
            case "removeLms":
                if (_serverLms != null) { await _serverLms.RemoveAsync(Text(p, "id")); sessions.ReloadAccounts(); return Done("Removed from the database."); }
                sessions.RemoveAccount(Text(p, "id"));
                return Done(sessions.Status);
            case "check":
                if (DateOnly.TryParse(Text(p, "from"), out var from) && DateOnly.TryParse(Text(p, "to"), out var to)) await sessions.CheckAsync(Flag(p, "full"), from, to);
                else await sessions.CheckAsync(Flag(p, "full"));
                return Done(sessions.Status);
            case "sheetAll":
            {
                var (ok, message) = await sessions.CheckSheetForAllAsync();
                return new { ok, message };
            }
            case "open":
                main.NavigateCommand.Execute(Number(p, "page").ToString());
                return true;
            case "approve":
                await central.Api.ApproveAsync(Text(p, "id"), Flag(p, "approve"));
                await central.RefreshAsync();
                return Done(Flag(p, "approve") ? "Approved: they can sign in now." : "Rejected.");
            case "setStatus":
                await central.Api.SetUserStatusAsync(Text(p, "id"), Text(p, "status"));
                await central.RefreshAsync();
                return Done(Text(p, "status") == "disabled" ? "Disabled and signed out." : "They can sign in again.");
            case "createUser":
            {
                string username = Text(p, "username").Trim(), password = Text(p, "password");
                if (username.Length == 0 || password.Length < 12) return new { ok = false, message = "A username and a password of 12 characters or more are needed." };
                string display = Text(p, "displayName").Trim();
                await central.Api.CreateUserAsync(username, display.Length > 0 ? display : username, password, List(p, "groupIds"));
                await central.RefreshAsync();
                return Done($"{username} was created. Send them the app and this sign-in; they sign in on this Dashboard.");
            }
            case "setGroups":
                await central.Api.SetUserGroupsAsync(Text(p, "id"), List(p, "groupIds"));
                await central.RefreshAsync();
                return Done("Their groups were saved.");
            case "copyText":
            {
                // The sign-in the admin sends a coordinator. Only the clipboard sees it; nothing logs it.
                string text = Text(p, "text");
                if (text.Length == 0) return new { ok = false, message = "Nothing to copy." };
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetText(text); break; }
                    catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(100); }
                }
                return Done("Copied. Paste it in a message to them.");
            }
            case "resetPassword":
            {
                string password = Text(p, "password");
                if (password.Length < 12) return new { ok = false, message = "The new password needs 12 characters or more." };
                await central.Api.ResetPasswordAsync(Text(p, "id"), password);
                return Done("The password was changed and they were signed out.");
            }
            case "createGroup":
            {
                string name = Text(p, "name").Trim();
                if (name.Length == 0) return new { ok = false, message = "Type the group's name as the LMS writes it, e.g. CAI5_AIS4_S9." };
                await central.Api.CreateGroupAsync(name, string.IsNullOrWhiteSpace(Text(p, "displayName")) ? null : Text(p, "displayName").Trim());
                await central.RefreshAsync();
                return Done($"{name} was added.");
            }
            case "archiveGroup":
                await central.Api.ArchiveGroupAsync(Text(p, "id"), Flag(p, "archived"));
                await central.RefreshAsync();
                return Done(Flag(p, "archived") ? "Archived: it is hidden from lists but nothing is deleted." : "Active again.");
            case "startServer":
                await main.RecordingsDashboard.StartAsync();
                await Task.Delay(1500);
                await central.RefreshAsync();
                return Done(main.RecordingsDashboard.StatusText);
            default:
                throw new InvalidOperationException("Unknown request.");
        }
    }

    private static object Done(string message) => new { ok = true, message };

    private object BuildState()
    {
        var main = _main!;
        var central = main.Central;
        var sessions = main.LmsSessions;
        var me = central.IsSignedIn ? central.Api.Me : null;
        var server = main.RecordingsDashboard;
        return new
        {
            server = new { up = server.IsServerUp || server.IsClientMode, text = server.StatusText, detail = server.StatusDetail, clientMode = server.IsClientMode },
            shareServer = server.Settings.ServerUrl ?? "",
            status = central.Status,
            busy = central.IsBusy || sessions.IsBusy,
            savedLogin = central.Api.HasSavedLogin,
            known = central.Api.KnownAccounts.Select(a => new { username = a.Username, displayName = a.DisplayName, role = a.Role, hasSession = a.HasSession, hasPassword = a.HasPassword, lastUsed = a.LastUsed.LocalDateTime.ToString("ddd dd MMM") }),
            me = me == null ? null : new
            {
                username = me.Username, displayName = me.DisplayName, role = me.Role, allGroups = me.AllGroups,
                groups = (me.Groups ?? []).Where(g => !g.Archived).Select(g => g.Name),
            },
            lms = _serverLms != null
                ? (object)new
                {
                    active = _serverLms.Accounts.FirstOrDefault(a => a.Active)?.Id, onServer = true, canKeepPasswords = _serverLms.CanKeepPasswords,
                    accounts = _serverLms.Accounts.Select(a => new { id = a.Id, label = a.Label, email = a.Email, role = a.Role }),
                }
                : new
                {
                    active = sessions.SelectedAccount?.Id, onServer = false, canKeepPasswords = true,
                    accounts = sessions.Accounts.Select(a => new { id = a.Id, label = a.Label, email = a.Email, role = a.Role }),
                },
            sessions = new
            {
                running = sessions.RunningNow, today = sessions.Today, attention = sessions.NeedsAttention, drivePending = sessions.WaitingForDrive,
                done = sessions.Rows.Count(r => r.Tone == "done"), total = sessions.Rows.Count, lastRead = sessions.LastLmsCheck, status = sessions.Status,
                next = sessions.Rows.Where(r => r.Date.ToDateTime(r.Start) > DateTime.Now).OrderBy(r => r.Date.ToDateTime(r.Start))
                    .Select(r => new { group = r.Group, title = r.Title, date = r.Date.ToString("yyyy-MM-dd"), start = r.Start.ToString("HH:mm") }).FirstOrDefault(),
                attentionRows = sessions.Rows.Where(r => r.Tone == "bad").Take(5)
                    .Select(r => new { group = r.Group, title = r.Title, date = r.Date.ToString("yyyy-MM-dd"), start = r.Start.ToString("HH:mm"), next = r.NextStep }),
            },
            recordings = central.IsSignedIn ? new { total = central.Total, onLms = central.OnLms, pending = central.Pending, drive = central.DriveCount, zoomOnly = central.ZoomOnly, missing = central.Missing } : null,
            users = central.IsAdmin ? central.Users.Select(u => new
            {
                id = u.Id, username = u.Username, displayName = u.DisplayName, role = u.Role, status = u.Status,
                lastLogin = u.LastLoginAt?.LocalDateTime.ToString("ddd dd MMM HH:mm"), groups = u.Groups.Select(g => new { id = g.Id, name = g.Name }),
            }) : null,
            groups = central.IsAdmin ? central.Groups.Where(g => !g.Archived).Select(g => new { id = g.Id, name = g.Group }) : null,
            allGroups = central.IsAdmin ? central.Groups.Select(g => new
            {
                id = g.Id, name = g.Group, displayName = g.DisplayName, archived = g.Archived, recordings = g.Recordings, lastSession = g.LastSessionDate,
                pending = g.Pending, onLms = g.OnLms, missing = g.MissingLink,
                coordinators = (g.Coordinators ?? []).Select(c => c.DisplayName),
            }) : null,
            pages = new { sessions = MainViewModel.SessionsPage, recordings = MainViewModel.CentralRecordingsPage, coordinators = MainViewModel.CoordinatorsPage, server = 14 },
        };
    }
}
