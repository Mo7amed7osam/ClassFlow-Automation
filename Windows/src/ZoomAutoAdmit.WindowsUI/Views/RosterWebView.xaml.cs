using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ZoomAutoAdmit.Roster;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using static ZoomAutoAdmit.WindowsUI.Views.WebPageBridge;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// Groups & Students (React, WebSessions/roster.html): every group's roster in its order, edited
/// in place, and a group's students brought from the LMS in one click with the LMS account in use
/// (the admin's or the coordinator's). The rosters are the same ones the Attendance page loads.
/// </summary>
public partial class RosterWebView : UserControl
{
    private MainViewModel? _main;
    private WebPageBridge? _bridge;
    private Task? _initializing;
    private IReadOnlyList<RosterGroup> _groups = [];
    private string? _reading;
    private string _status = "";

    public RosterWebView()
    {
        InitializeComponent();
        Loaded += (_, _) => _initializing ??= InitializeAsync();
    }

    private IGroupRosterService Store => _main!.Roster.Service;

    private async Task InitializeAsync()
    {
        try
        {
            _main = DataContext as MainViewModel ?? throw new InvalidOperationException("The app is not loaded.");
            _bridge = new WebPageBridge(Browser, "roster.zoomautoadmit", "WebSessions", "roster.html", HandleAsync, BuildState);
            _main.Central.PropertyChanged += (_, _) => _bridge.Schedule();
            _main.LmsSessions.Changed += _bridge.Schedule;
            LmsRosterImport.Changed += _ => Dispatcher.BeginInvoke(async () => { try { await ReloadAsync(); } catch { } });
            await ReloadAsync();
            await _bridge.InitializeAsync();
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Placeholder.Text = "Groups & Students could not be opened: " + ex.Message;
            WindowsUiErrorLog.Write("Groups & Students could not be opened.", ex);
        }
    }

    /// <summary>Reads the rosters again, here and for the pages that use them (AI Matching, Attendance).</summary>
    private async Task ReloadAsync()
    {
        _groups = await Store.ListAsync();
        _bridge?.Schedule();
        try { await _main!.Roster.RefreshAsync(); } catch { }
    }

    private async Task<RosterGroup> GroupAsync(string id) =>
        (await Store.ListAsync()).FirstOrDefault(g => g.GroupId.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("That group is no longer there; the list was refreshed.");

    private async Task<object?> HandleAsync(string method, JsonElement p)
    {
        try { return await DoAsync(method, p); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.InvalidDataException or System.IO.IOException)
        {
            await ReloadAsync();
            return new { ok = false, message = ex.Message };
        }
    }

    private async Task<object?> DoAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "refresh":
                await ReloadAsync();
                return Done("Up to date.");
            case "lmsRoster":
            {
                string group = Text(p, "group").Trim();
                if (_reading != null) return new { ok = false, message = $"{_reading} is being read from the LMS; wait for it to finish." };
                _reading = group; _status = $"Reading {group}'s students from the LMS…"; _bridge!.PushState();
                try
                {
                    var result = await new LmsRosterImport(Store).ImportAsync(group);
                    _status = result.Message;
                    await ReloadAsync();
                    return new { ok = result.Ok, message = result.Message, group = result.Group, added = result.Added };
                }
                finally { _reading = null; _bridge!.Schedule(); }
            }
            case "createGroup":
            {
                string id = Text(p, "id").Trim();
                if (id.Length == 0) return new { ok = false, message = "Type the group's name as the LMS writes it, e.g. CAI5_AIS4_S9." };
                string name = Text(p, "name").Trim();
                await Store.CreateAsync(id, name.Length > 0 ? name : id);
                await ReloadAsync();
                return Done($"{id} was added. Bring its students from the LMS, import a file, or add them one by one.");
            }
            case "renameGroup":
                await Store.RenameAsync(await GroupAsync(Text(p, "id")), Text(p, "name").Trim());
                await ReloadAsync();
                return Done("Renamed.");
            case "deleteGroup":
            {
                var group = await GroupAsync(Text(p, "id"));
                await Store.DeleteAsync(group);
                await ReloadAsync();
                return Done($"{group.GroupId} and its {group.Students.Count} students were deleted (the previous file is kept as groups.json.bak).");
            }
            case "saveStudent":
            {
                var group = await GroupAsync(Text(p, "group"));
                string id = Text(p, "id"), name = Text(p, "name").Trim();
                if (name.Length == 0) return new { ok = false, message = "Type the student's name." };
                var aliases = List(p, "aliases").Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();
                string? email = Text(p, "email").Trim() is { Length: > 0 } e ? e : null;
                var existing = group.Students.FirstOrDefault(s => s.StudentId == id);
                if (existing == null)
                {
                    int order = group.Students.Select(s => s.Order).DefaultIfEmpty(0).Max() + 1;
                    await Store.AddStudentAsync(group, new GroupStudent(Guid.NewGuid().ToString("D"), group.GroupId, order, name, aliases, email));
                }
                else await Store.UpdateStudentAsync(group, existing with { FullName = name, Aliases = aliases, Email = email });
                await ReloadAsync();
                return Done(existing == null ? $"{name} was added at the end of the roster." : "Saved.");
            }
            case "deleteStudent":
            {
                var group = await GroupAsync(Text(p, "group"));
                var student = group.Students.FirstOrDefault(s => s.StudentId == Text(p, "id")) ?? throw new InvalidOperationException("That student is no longer there.");
                await Store.DeleteStudentAsync(group, student.StudentId);
                await ReloadAsync();
                return Done($"{student.FullName} was removed.");
            }
            case "move":
            {
                var group = await GroupAsync(Text(p, "group"));
                var ids = group.Students.OrderBy(s => s.Order).Select(s => s.StudentId).ToList();
                int from = ids.IndexOf(Text(p, "id")), to = from + (Number(p, "dir") < 0 ? -1 : 1);
                if (from < 0 || to < 0 || to >= ids.Count) return Done("Already at the edge.");
                (ids[from], ids[to]) = (ids[to], ids[from]);
                await Store.ReorderAsync(group, ids);
                await ReloadAsync();
                return true;
            }
            case "import":
            {
                var group = await GroupAsync(Text(p, "group"));
                string? path = _main!.Roster.Dialogs.SelectImportFile();
                if (path == null) return Done("");
                int count = await Store.ImportAsync(group, path);
                await ReloadAsync();
                return Done($"{count} students imported into {group.GroupId}.");
            }
            case "open":
                _main!.NavigateCommand.Execute(Number(p, "page").ToString());
                return true;
            default:
                throw new InvalidOperationException("Unknown request.");
        }
    }

    private static object Done(string message) => new { ok = true, message };

    private object BuildState()
    {
        var main = _main!;
        var account = main.LmsSessions.SelectedAccount;
        var me = main.Central.IsSignedIn ? main.Central.Api.Me : null;
        var mine = LmsRosterImport.GroupsFor(main);
        return new
        {
            groups = _groups.Select(g => new
            {
                id = g.GroupId, name = g.DisplayName, mine = mine.Contains(g.GroupId, StringComparer.OrdinalIgnoreCase),
                students = g.Students.OrderBy(s => s.Order).Select(s => new { id = s.StudentId, order = s.Order, name = s.FullName, aliases = s.Aliases, email = s.Email ?? "" }),
            }),
            lmsGroups = mine,
            account = account == null ? null : new { label = account.Label, email = account.Email },
            me = me == null ? null : new { name = me.DisplayName, role = me.Role },
            reading = _reading,
            status = _status,
            pages = new { attendance = 8, dashboard = MainViewModel.DashboardPage },
        };
    }
}
