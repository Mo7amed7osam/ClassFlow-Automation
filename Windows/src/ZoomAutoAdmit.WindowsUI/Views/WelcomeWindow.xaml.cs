using System.IO;
using System.Text.Json;
using System.Windows;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using static ZoomAutoAdmit.WindowsUI.Views.WebPageBridge;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// "Get started" (React, WebSessions/welcome.html): the first time a coordinator opens the app, one
/// step at a time - the account the admin made for them, their LMS account, a Zoom account and
/// session link for each of their groups, and (skippable) their own OpenRouter key. Nothing ships
/// with the app: every account and key is typed here by the person who owns it, and each goes
/// where the rest of the app keeps it (the server, Windows Credential Manager), never into a file.
/// </summary>
public partial class WelcomeWindow : Window
{
    private static string DoneFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "welcome.json");

    private readonly MainViewModel _main;
    private WebPageBridge? _bridge;
    private LmsServerAccounts? _serverLms;
    private int _openAfter = -1;

    public WelcomeWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>
    /// A PC that connects to someone else's server and has not been set up yet. A coordinator who was
    /// already working before this version (signed in, with Zoom accounts) is not asked again.
    /// </summary>
    public static bool IsFirstRun(MainViewModel main) =>
        !File.Exists(DoneFile) && main.RecordingsDashboard.IsClientMode &&
        (main.Central.Api.KnownAccounts.Count == 0 || main.Accounts.Items.Count == 0);

    /// <summary>Opens the steps over the main window; afterwards the app shows the page the person chose.</summary>
    public static void Open(Window owner, MainViewModel main)
    {
        var window = new WelcomeWindow(main) { Owner = owner };
        window.ShowDialog();
        if (window._openAfter >= 0) main.NavigateCommand.Execute(window._openAfter.ToString());
    }

    private async Task InitializeAsync()
    {
        try
        {
            _bridge = new WebPageBridge(Browser, "welcome.zoomautoadmit", "WebSessions", "welcome.html", HandleAsync, BuildState);
            _main.Central.PropertyChanged += OnChanged;
            _main.AiMatching.PropertyChanged += OnChanged;
            _main.Accounts.AccountsChanged += _bridge.Schedule;
            Closed += (_, _) =>
            {
                _main.Central.PropertyChanged -= OnChanged;
                _main.AiMatching.PropertyChanged -= OnChanged;
                _main.Accounts.AccountsChanged -= _bridge.Schedule;
            };
            await _bridge.InitializeAsync();
            Placeholder.Visibility = Visibility.Collapsed;
            // Someone already signed in on this PC (the steps were closed half way): carry on from there.
            await _main.Central.RefreshAsync();
            if (_main.Central.IsSignedIn) await SyncLmsAsync();
            _bridge.Schedule();
        }
        catch (Exception ex)
        {
            Placeholder.Text = "Get started could not be opened: " + ex.Message;
            WindowsUiErrorLog.Write("Get started could not be opened.", ex);
        }
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _bridge?.Schedule();

    private async Task<string?> SyncLmsAsync()
    {
        if (!_main.Central.IsSignedIn) { _serverLms = null; return null; }
        _serverLms ??= new LmsServerAccounts(_main.Central.Api);
        try
        {
            string? message = await _serverLms.SyncAsync();
            _main.LmsSessions.ReloadAccounts();
            return message;
        }
        catch (Exception ex) { return $"Your LMS accounts could not be read from the server: {Services.CentralApiException.Explain(ex)}"; }
    }

    private async Task<object?> HandleAsync(string method, JsonElement p)
    {
        var central = _main.Central;
        switch (method)
        {
            case "signIn":
            {
                string server = Text(p, "server").Trim();
                var dashboard = _main.RecordingsDashboard;
                if (server.Length > 0 && !string.Equals(server, dashboard.ServerUrl?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    dashboard.IsClientMode = true;
                    dashboard.ServerUrl = server;
                    if (!dashboard.Save()) return new { ok = false, message = dashboard.Notice };
                }
                central.Username = Text(p, "username").Trim();
                await central.SignInAsync(Text(p, "password"), remember: true, savePassword: Flag(p, "savePassword"));
                if (!central.IsSignedIn) return new { ok = false, message = central.Status };
                string? lms = await SyncLmsAsync();
                return Done($"Signed in as {central.SignedInAs}. {lms}".Trim());
            }
            case "signOut":
                central.SignOutCommand.Execute(null);
                _serverLms = null;
                return Done("Signed out.");
            case "saveLms":
            {
                string email = Text(p, "email").Trim(), password = Text(p, "password");
                if (email.Length == 0 || password.Length == 0) return new { ok = false, message = "Type the LMS email and password." };
                string label = Text(p, "label").Trim();
                if (label.Length == 0) label = central.Api.Me?.DisplayName ?? "My LMS account";
                string role = central.IsAdmin ? "admin" : "coordinator";
                if (_serverLms != null)
                {
                    string? saved = await _serverLms.SaveAsync(label, email, role, password, true);
                    _main.LmsSessions.ReloadAccounts();
                    return Done($"Saved (the password encrypted on the server). {saved}".Trim());
                }
                _main.LmsSessions.SaveAccount(label, email, role, password, true);
                return Done(_main.LmsSessions.Status);
            }
            case "saveZoom":
                return await SaveZoomAsync(p);
            case "testAi":
            {
                var ai = _main.AiMatching;
                string model = Text(p, "model").Trim();
                if (!model.Contains('/')) return new { ok = false, message = "Use a full OpenRouter model ID, e.g. openai/gpt-4.1-mini." };
                ai.Provider = AiProvider.OpenRouter;
                ai.Model = model;
                await ai.TestAndSaveAsync(Text(p, "key"));
                return new { ok = ai.IsReady, message = ai.Status };
            }
            case "finish":
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DoneFile)!);
                    File.WriteAllText(DoneFile, JsonSerializer.Serialize(new { finishedAt = DateTimeOffset.Now, user = central.Api.Me?.Username }));
                }
                catch (Exception ex) { WindowsUiErrorLog.Write("Get started could not note that it was finished.", ex); }
                _openAfter = Number(p, "page") is var page && page > 0 ? page : MainViewModel.DashboardPage;
                _ = Dispatcher.BeginInvoke(Close);
                return Done("All set.");
            case "later":
                _ = Dispatcher.BeginInvoke(Close);
                return true;
            default:
                throw new InvalidOperationException("Unknown request.");
        }
    }

    /// <summary>
    /// One Zoom account per group, named after the group so schedules and the LMS find it: the email
    /// it signs in with, the usual session link, and (only if Zoom asks for one) its password.
    /// </summary>
    private async Task<object> SaveZoomAsync(JsonElement p)
    {
        string group = Text(p, "group").Trim();
        if (group.Length == 0) return new { ok = false, message = "Type the group's name as the LMS writes it, e.g. CAI5_AIS4_S9." };
        var accounts = _main.Accounts;
        var existing = accounts.Items.FirstOrDefault(a =>
            string.Equals(a.GroupName ?? a.AccountId, group, StringComparison.OrdinalIgnoreCase) ||
            a.AccountId.Equals(group, StringComparison.OrdinalIgnoreCase));
        accounts.NewCommand.Execute(null);
        if (existing != null) accounts.SelectedAccount = existing;
        accounts.AccountId = existing?.AccountId ?? group;
        accounts.GroupName = group;
        string display = Text(p, "displayName").Trim();
        accounts.DisplayName = display.Length > 0 ? display : existing?.DisplayName ?? group;
        accounts.ZoomEmail = Text(p, "email").Trim();
        accounts.DefaultMeetingUrl = Text(p, "link").Trim();
        string password = Text(p, "password");
        if (password.Length > 0)
        {
            // Kept on this PC and in the database both; either one is enough to go on with.
            if (!await accounts.SavePasswordAsync(password)) return new { ok = false, message = accounts.StatusMessage };
        }
        await accounts.SaveAsync();
        bool ok = accounts.StatusMessage.StartsWith("Profile saved", StringComparison.Ordinal);
        return new { ok, message = ok ? $"{group}: saved." : accounts.StatusMessage };
    }

    private static object Done(string message) => new { ok = true, message };

    private object BuildState()
    {
        var central = _main.Central;
        var me = central.IsSignedIn ? central.Api.Me : null;
        var ai = _main.AiMatching;
        var zoomPasswords = new ZoomProfileCredentialStore();
        bool HasZoomPassword(string id) { try { return zoomPasswords.HasPassword(id); } catch { return false; } }
        return new
        {
            server = _main.RecordingsDashboard.ServerUrl,
            busy = central.IsBusy || !ai.IsIdle,
            status = central.Status,
            me = me == null ? null : new
            {
                username = me.Username, displayName = me.DisplayName, role = me.Role, allGroups = me.AllGroups,
                groups = (me.Groups ?? []).Where(g => !g.Archived).Select(g => g.Name),
            },
            lms = new
            {
                onServer = _serverLms != null,
                accounts = _serverLms != null
                    ? _serverLms.Accounts.Select(a => new { id = a.Id, label = a.Label, email = a.Email, active = a.Active }).ToArray()
                    : _main.LmsSessions.Accounts.Select(a => new { id = a.Id, label = a.Label, email = a.Email, active = a.Id == _main.LmsSessions.SelectedAccount?.Id }).ToArray(),
            },
            zoom = _main.Accounts.Items.Select(a => new
            {
                id = a.AccountId, group = a.GroupName ?? a.AccountId, displayName = a.DisplayName, email = a.ZoomEmail ?? "",
                link = a.DefaultMeetingUrl ?? "", hasPassword = HasZoomPassword(a.AccountId),
            }),
            ai = new
            {
                model = ai.Provider == AiProvider.OpenRouter ? ai.Model : AiMatchingViewModel.BuiltInModels(AiProvider.OpenRouter)[0],
                models = AiMatchingViewModel.BuiltInModels(AiProvider.OpenRouter),
                ready = ai.IsReady && ai.Provider == AiProvider.OpenRouter,
                status = ai.Status,
            },
            pages = new { dashboard = MainViewModel.DashboardPage, schedules = 3, sessions = MainViewModel.SessionsPage },
        };
    }
}
