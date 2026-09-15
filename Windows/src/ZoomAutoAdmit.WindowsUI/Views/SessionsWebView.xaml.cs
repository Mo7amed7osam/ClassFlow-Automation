using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ZoomAutoAdmit.WebAutomation.Lms;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.ViewModels;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// The Sessions page: a React page (WebSessions, built from Windows/web/sessions) drawn in the app.
/// Everything it shows comes from LmsSessionsViewModel, and every button comes back here and goes
/// through the same view model and the app's one LMS follow-up processor.
/// </summary>
public partial class SessionsWebView : UserControl
{
    private const string HostName = "sessions.zoomautoadmit";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private LmsSessionsViewModel? _model;
    private Task? _initializing;
    private bool _ready;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public SessionsWebView()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); PushState(); };
        Loaded += (_, _) => _initializing ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _model = (DataContext as MainViewModel)?.LmsSessions ?? throw new InvalidOperationException("The sessions are not loaded.");
            _model.Changed += Schedule;
            _model.PropertyChanged += (_, _) => Schedule();
            var environment = await RecordingsDashboardView.SharedEnvironment.Value;
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "WebSessions"), CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host != HostName) { e.Cancel = true; OpenOutside(e.Uri); }
            };
            bool dark = MainWindow.CurrentIsDark;
            Browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x0D, 0x10, 0x17) : System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9);
            MainWindow.ThemeChanged += d =>
            {
                Browser.DefaultBackgroundColor = d ? System.Drawing.Color.FromArgb(0x0D, 0x10, 0x17) : System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9);
                Push(new { push = "theme", dark = d });
            };
            core.Navigate($"https://{HostName}/index.html?theme={(dark ? "dark" : "light")}");
            Placeholder.Visibility = Visibility.Collapsed;
            _ = _model.ReloadAsync();
        }
        catch (Exception ex)
        {
            Placeholder.Text = "The Sessions page could not be opened: " + ex.Message;
            WindowsUiErrorLog.Write("The Sessions page could not be opened.", ex);
        }
    }

    private void Schedule() => Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    private static void OpenOutside(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); }
        catch (JsonException) { return; }
        string method = message.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        int? id = message.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        var p = message.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object ? ps : default;
        string S(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B(string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var model = _model!;
        try
        {
            switch (method)
            {
                case "ready": _ready = true; PushState(); Reply(id, true); return;
                case "state": Reply(id, BuildState()); return;
                case "refresh": await model.ReloadAsync(); Reply(id, true); return;
                case "check":
                    if (DateOnly.TryParse(S("from"), out var from) && DateOnly.TryParse(S("to"), out var to)) await model.CheckAsync(B("full"), from, to);
                    else await model.CheckAsync(B("full"));
                    Reply(id, new { ok = true, message = model.Status });
                    return;
                case "step":
                {
                    var (ok, text) = await model.RunStepAsync(S("group"), DateOnly.Parse(S("date")), TimeOnly.Parse(S("start")), S("step"), S("link"));
                    Reply(id, new { ok, message = text });
                    return;
                }
                case "sheetAll":
                {
                    var (ok, text) = await model.CheckSheetForAllAsync();
                    Reply(id, new { ok, message = text });
                    return;
                }
                case "saveSheet":
                {
                    var tabs = new Dictionary<string, string>();
                    if (p.TryGetProperty("tabs", out var t) && t.ValueKind == JsonValueKind.Object)
                        foreach (var tab in t.EnumerateObject()) tabs[tab.Name] = tab.Value.GetString() ?? "";
                    model.SaveSheetSettings(S("url"), tabs);
                    Reply(id, new { ok = true, message = model.Status });
                    return;
                }
                case "materialFolder":
                {
                    // A technical class's material: the folder is chosen here, on this PC.
                    var date = DateOnly.Parse(S("date")); var start = TimeOnly.Parse(S("start"));
                    if (B("clear")) { model.ChooseMaterialFolder(S("group"), date, start, null); Reply(id, new { ok = true, message = "The folder was removed from this class." }); await model.ReloadAsync(); return; }
                    var picker = new Microsoft.Win32.OpenFolderDialog { Title = $"Material for {S("group")} · {date:ddd dd MMM} {start:HH\\:mm}", InitialDirectory = MaterialStart(model) };
                    if (picker.ShowDialog(Window.GetWindow(this)) != true) { Reply(id, new { ok = false, message = "" }); return; }
                    model.ChooseMaterialFolder(S("group"), date, start, picker.FolderName);
                    await model.ReloadAsync();
                    var row = model.Rows.FirstOrDefault(r => r.Group.Equals(S("group"), StringComparison.OrdinalIgnoreCase) && r.Date == date && r.Start == start);
                    int count = row?.Material?.Files.Count ?? 0;
                    Reply(id, new { ok = count > 0, message = count > 0 ? $"{count} file(s) ready from {Path.GetFileName(picker.FolderName)}. Press Upload now to put them on the LMS." : row?.Material?.Note ?? "That folder has no PDF, ZIP or PowerPoint file." });
                    return;
                }
                case "trackFolder":
                {
                    // A track's default: a folder ("Session N" inside, or files named so) or one file.
                    string? path = PickPath(S("kind"), $"The {S("track")} material", MaterialStart(model, S("track")));
                    if (path == null) { Reply(id, new { ok = false, message = "" }); return; }
                    model.ChooseTrackFolder(S("track"), path);
                    await model.ReloadAsync();
                    Reply(id, new { ok = true, message = $"{S("track")} material comes from {path}." });
                    return;
                }
                case "materialPreview":
                {
                    // The upload box: what a folder or file (or the class's own material) would put up.
                    // Nothing is kept until Upload.
                    var date = DateOnly.Parse(S("date")); var start = TimeOnly.Parse(S("start"));
                    string? path = null;
                    if (S("kind") is "folder" or "file")
                    {
                        path = PickPath(S("kind"), $"Material for {S("group")} · {date:ddd dd MMM} {start:HH\\:mm}", MaterialStart(model, MaterialPlanner.Technical));
                        if (path == null) { Reply(id, new { ok = false, cancelled = true }); return; }
                    }
                    var (plan, files, assignment) = await model.PreviewMaterialAsync(S("group"), date, start, path);
                    Reply(id, new
                    {
                        ok = true, path, label = plan.Label, note = plan.Note, folder = plan.Folder,
                        files = files.Select(f => f.Title), skipped = plan.Skipped,
                        assignment = assignment == null ? null : new { title = assignment.Title, deadline = assignment.Deadline.ToString("ddd dd MMM HH:mm") },
                    });
                    return;
                }
                case "setAssignment":
                {
                    var date = DateOnly.Parse(S("date")); var start = TimeOnly.Parse(S("start"));
                    DateTime? deadline = DateTime.TryParse(S("deadline"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                    model.ChooseAssignment(S("group"), date, start, S("title"), deadline, B("none"), S("description"), S("file"));
                    await model.ReloadAsync();
                    Reply(id, new { ok = true, message = B("none") ? "Marked as having no assignment." : $"Assignment due {deadline:ddd dd MMM HH\\:mm}." });
                    return;
                }
                case "assignmentFile":
                {
                    // The assignment's own sheet (a technical class's), chosen on this PC.
                    var picker = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "The assignment's file", Filter = "Files the LMS takes (PDF, ZIP, PowerPoint)|*.pdf;*.zip;*.ppt;*.pptx",
                        InitialDirectory = model.Rows.FirstOrDefault(r => r.Group.Equals(S("group"), StringComparison.OrdinalIgnoreCase) && r.Date.ToString("yyyy-MM-dd") == S("date"))?.Material?.Folder
                                           ?? MaterialStart(model),
                    };
                    if (picker.ShowDialog(Window.GetWindow(this)) != true) { Reply(id, new { ok = false, path = "", name = "" }); return; }
                    Reply(id, new { ok = true, path = picker.FileName, name = Path.GetFileNameWithoutExtension(picker.FileName) });
                    return;
                }
                case "viewRange":
                {
                    // The classes of the chosen days, even old ones; "clear" goes back to the usual weeks.
                    if (!B("clear") && DateOnly.TryParse(S("from"), out var vf) && DateOnly.TryParse(S("to"), out var vt))
                        model.ViewRange = vf <= vt ? (vf, vt) : (vt, vf);
                    else model.ViewRange = null;
                    await model.ReloadAsync();
                    var v = model.ViewRange;
                    int shown = v == null ? model.Rows.Count : model.Rows.Count(r => r.Date >= v.Value.From && r.Date <= v.Value.To);
                    Reply(id, new { ok = true, message = v == null ? "Back to the last two weeks and the next one." : $"{shown} class(es) from {v.Value.From:dd MMM yyyy} to {v.Value.To:dd MMM yyyy} on this PC." });
                    return;
                }
                case "open": OpenOutside(S("url")); Reply(id, true); return;
                case "openPage":
                    (DataContext as MainViewModel)?.NavigateCommand.Execute(MainViewModel.DashboardPage.ToString());
                    Reply(id, true);
                    return;
                case "useAccount": model.UseAccount(S("id")); Reply(id, new { ok = true, message = model.Status }); PushState(); return;
                case "removeAccount": model.RemoveAccount(S("id")); Reply(id, new { ok = true, message = model.Status }); PushState(); return;
                case "saveAccount":
                    // The password goes straight to Windows Credential Manager; it is not kept or logged here.
                    model.SaveAccount(S("label"), S("email"), S("role"), S("password"), B("makeActive"));
                    Reply(id, new { ok = true, message = model.Status }); PushState();
                    return;
                default: Fail(id, "Unknown request."); return;
            }
        }
        catch (Exception ex) { Fail(id, ex.Message); }
    }

    /// <summary>
    /// Where a picker opens: the track's own location when it has one (Technical's is where technical
    /// material is kept), else next to the other tracks.
    /// </summary>
    private static string MaterialStart(LmsSessionsViewModel model, string? track = null)
    {
        var tracks = model.Materials.Tracks;
        if (track != null && tracks.TryGetValue(track, out var own))
        {
            if (Directory.Exists(own)) return own;
            if (File.Exists(own) && Path.GetDirectoryName(own) is { } ownFolder) return ownFolder;
        }
        var any = tracks.Values.FirstOrDefault(Directory.Exists);
        string? parent = any == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(any));
        return parent != null && Directory.Exists(parent) ? parent : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>A folder or a file (PDF, ZIP, PowerPoint: what the LMS takes), or null when nothing was chosen.</summary>
    private string? PickPath(string kind, string title, string start)
    {
        var owner = Window.GetWindow(this);
        if (kind == "file")
        {
            var file = new Microsoft.Win32.OpenFileDialog { Title = title, InitialDirectory = start, Filter = "Files the LMS takes (PDF, ZIP, PowerPoint)|*.pdf;*.zip;*.ppt;*.pptx" };
            return file.ShowDialog(owner) == true ? file.FileName : null;
        }
        var folder = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = start };
        return folder.ShowDialog(owner) == true ? folder.FolderName : null;
    }

    private object BuildState()
    {
        var model = _model!;
        var sheet = model.SheetSettings;
        var materials = model.Materials;
        var working = model.Working;
        var groups = model.Rows.Select(r => r.Group).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToArray();
        return new
        {
            now = DateTimeOffset.Now.ToString("O"),
            status = model.Status,
            busy = model.IsBusy,
            lastRead = model.LastLmsCheck,
            stats = new { running = model.RunningNow, today = model.Today, attention = model.NeedsAttention, drivePending = model.WaitingForDrive, done = model.Rows.Count(r => r.Tone == "done") },
            account = model.ActiveAccount,
            accounts = model.Accounts.Select(a => new { id = a.Id, label = a.Label, email = a.Email, role = a.Role, active = a.Id == model.SelectedAccount?.Id }),
            sheet = new { url = sheet.Url ?? "", tabs = sheet.Tabs, groups },
            view = model.ViewRange is { } range ? new { from = range.From.ToString("yyyy-MM-dd"), to = range.To.ToString("yyyy-MM-dd") } : null,
            materials = new
            {
                tracks = MaterialPlanner.FixedTracks.Append(MaterialPlanner.Technical)
                    .Select(t => new { track = t, folder = materials.Tracks.GetValueOrDefault(t) ?? "", isFile = File.Exists(materials.Tracks.GetValueOrDefault(t) ?? "") }),
            },
            working,
            rows = model.Rows.Select(r => new
            {
                key = r.Key, group = r.Group, date = r.Date.ToString("yyyy-MM-dd"), start = r.Start.ToString("HH:mm"),
                title = r.Title, tone = r.Tone, next = r.NextStep, zoom = r.Zoom,
                lmsStatus = r.LmsStatus, lmsReadAt = r.LmsReadAt, lmsUrl = r.LmsUrl, linkKind = r.LinkKind, recordLink = r.RecordLink,
                steps = r.Steps.Select(s => new { key = s.Key, label = s.Label, state = s.State, text = s.Text, detail = s.Detail }),
                details = r.Details,
                material = r.Material is not { } m ? null : new
                {
                    track = m.Track, number = m.Number, folder = m.Folder, files = m.Files, skipped = m.Skipped, technical = m.Technical,
                    @fixed = m.Fixed, note = m.Note, assignmentTitle = m.AssignmentTitle, deadline = m.Deadline, noAssignment = m.NoAssignment, done = m.Done,
                    description = m.Description, assignmentFile = m.AssignmentFile, chosen = m.Chosen, removedOnLms = m.RemovedOnLms, seenOnLms = m.SeenOnLms,
                },
            }),
        };
    }

    private void PushState() { if (_ready) Push(new { push = "state", state = BuildState() }); }

    private void Reply(int? id, object result)
    {
        if (id == null) return;
        try { Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result }, Json)); } catch { }
    }

    private void Fail(int? id, string error)
    {
        if (id == null) return;
        try { Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error }, Json)); } catch { }
    }

    private void Push(object message)
    {
        try { Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }
}
