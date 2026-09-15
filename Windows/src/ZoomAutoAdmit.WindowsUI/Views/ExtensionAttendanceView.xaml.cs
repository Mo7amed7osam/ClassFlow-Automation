using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// Attendance, exactly as the Chrome extension does it: its own page (WebAttendance), in the app.
/// The page asks for the meetings and a fresh read through chrome-shim.js; the app pushes each
/// Participants read as it is written to disk, and the groups' rosters under "Saved rosters".
/// </summary>
public partial class ExtensionAttendanceView : UserControl
{
    private const string HostName = "attendance.zoomautoadmit";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ExtensionAttendanceFeed _feed = new();
    private FileSystemWatcher? _watcher, _admissionsWatcher;
    private Task? _initializing;
    private bool _ready;

    public ExtensionAttendanceView()
    {
        InitializeComponent();
        Loaded += (_, _) => _initializing ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await RecordingsDashboardView.SharedEnvironment.Value;
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            string folder = Path.Combine(AppContext.BaseDirectory, "WebAttendance");
            core.SetVirtualHostNameToFolderMapping(HostName, folder, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host != HostName && uri.Scheme != "data" && uri.Scheme != "blob")
                { e.Cancel = true; OpenOutside(e.Uri); }
            };
            bool dark = MainWindow.CurrentIsDark;
            Browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x0D, 0x10, 0x17) : System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9);
            MainWindow.ThemeChanged += OnThemeChanged;
            core.Navigate($"https://{HostName}/attendance.html?theme={(dark ? "dark" : "light")}");
            Placeholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Placeholder.Text = "The attendance page could not be opened: " + ex.Message;
            WindowsUiErrorLog.Write("The attendance page could not be opened.", ex);
        }
    }

    private void OnThemeChanged(bool dark)
    {
        Browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x0D, 0x10, 0x17) : System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9);
        Push(new { push = "theme", dark });
    }

    /// <summary>
    /// Today's admits (AdmissionLedger), each sent to the class it happened in: from 45 minutes
    /// before its start to 4 hours after. The page counts each once, however often it is sent.
    /// </summary>
    private async Task PushAdmissionsAsync()
    {
        var (tabs, ledger) = await Task.Run(() => (_feed.Tabs(), AdmissionLedger.Read(DateOnly.FromDateTime(DateTime.Now))));
        if (ledger.Count == 0) return;
        foreach (var tab in tabs)
        {
            var from = tab.Start.AddMinutes(-45); var to = tab.Start.AddHours(4);
            var entries = ledger.Where(e => e.At.LocalDateTime >= from && e.At.LocalDateTime <= to)
                .Select(e => new { id = $"{e.At:O}|{e.Name}|{e.Source}", name = e.Name, at = e.At.ToString("O"), people = e.People })
                .ToArray();
            if (entries.Length > 0) Push(new { push = "admissions", tabId = tab.Id, entries });
        }
    }

    private void WatchAdmissions()
    {
        if (_admissionsWatcher != null) return;
        Directory.CreateDirectory(AdmissionLedger.Folder);
        _admissionsWatcher = new FileSystemWatcher(AdmissionLedger.Folder, "admissions-*.jsonl") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        // Several admits in a row (Admit all) are one push.
        var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        debounce.Tick += async (_, _) => { debounce.Stop(); try { await PushAdmissionsAsync(); } catch (Exception ex) { WindowsUiErrorLog.Write("Admissions could not be sent to the attendance page.", ex); } };
        void Changed() => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        _admissionsWatcher.Changed += (_, _) => Changed();
        _admissionsWatcher.Created += (_, _) => Changed();
        _admissionsWatcher.EnableRaisingEvents = true;
    }

    private static void OpenOutside(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone(); }
        catch (JsonException) { return; }
        string method = message.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        int? id = message.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;
        try
        {
            switch (method)
            {
                case "ready":
                    await OnPageReadyAsync();
                    return;
                case "tabs":
                    Reply(id, await Task.Run(() => _feed.Tabs().Select(t => new { id = t.Id, url = t.Url, title = t.Title, live = t.Live }).ToArray()));
                    return;
                case "ai":
                {
                    string body = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                    var (status, text) = await AiProxy.ChatAsync(body);
                    Reply(id, new { status, body = text });
                    return;
                }
                case "lmsGroups":
                    Reply(id, LmsRosterImport.GroupsFor(DataContext as ViewModels.MainViewModel));
                    return;
                case "lmsRoster":
                {
                    string group = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
                    var result = await new LmsRosterImport().ImportAsync(group);
                    if (result.Ok) await PushRostersAsync();
                    Reply(id, new { ok = result.Ok, message = result.Message, group = result.Group, count = result.Names.Count, added = result.Added });
                    return;
                }
                case "results":
                    if (parameters.ValueKind == JsonValueKind.Object) await Task.Run(() => _feed.SaveResults(parameters));
                    return;
                case "capture":
                    int tabId = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tabId", out var t) ? t.GetInt32() : 0;
                    var latest = await Task.Run(() => _feed.Latest(tabId));
                    Reply(id, new { names = latest?.Names ?? [], url = latest == null ? null : ExtensionAttendanceFeed.PageUrl(latest.MeetingUrl, tabId) });
                    return;
                default:
                    if (id != null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error = "Unknown request." }, Json));
                    return;
            }
        }
        catch (Exception ex)
        {
            if (id != null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, error = ex.Message }, Json));
        }
    }

    private void Reply(int? id, object result)
    {
        if (id == null) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { id, result }, Json));
    }

    /// <summary>Rosters, then every read of today (the page skips the ones it already has), then live reads.</summary>
    private async Task OnPageReadyAsync()
    {
        await PushRostersAsync();
        foreach (var s in await Task.Run(() => _feed.Since(DateTime.Today)))
            PushSnapshot(s, _rosterByGroup);
        _ready = true;
        StartWatching();
        await PushAdmissionsAsync();
        WatchAdmissions();
        if (!_rostersWatched)
        {
            _rostersWatched = true;
            // A roster brought from the LMS (here or on Groups & Students) shows under Saved rosters.
            LmsRosterImport.Changed += _ => Dispatcher.BeginInvoke(async () => { try { await PushRostersAsync(); } catch { } });
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _rosterByGroup = new Dictionary<string, IReadOnlyList<string>>();
    private bool _rostersWatched;

    private async Task PushRostersAsync()
    {
        var rosters = await _feed.RostersAsync();
        _rosterByGroup = rosters.ToDictionary(r => r.Group, r => r.Names, StringComparer.OrdinalIgnoreCase);
        // Saved rosters offer this person's groups only (a roster read with another account stays on the PC).
        var mine = LmsRosterImport.GroupsFor(DataContext as ViewModels.MainViewModel);
        var shown = mine.Count == 0 ? rosters : [.. rosters.Where(r => mine.Contains(r.Group, StringComparer.OrdinalIgnoreCase))];
        Push(new { push = "rosters", rosters = shown.Select(r => new { group = r.Group, names = r.Names }) });
    }

    private void PushSnapshot(ExtensionAttendanceFeed.Snapshot s, IReadOnlyDictionary<string, IReadOnlyList<string>> rosters)
    {
        int tabId = ExtensionAttendanceFeed.TabId(s.ClassKey);
        Push(new
        {
            push = "snapshot", snapshotId = s.File, tabId, url = ExtensionAttendanceFeed.PageUrl(s.MeetingUrl, tabId),
            live = DateTimeOffset.Now - s.At < TimeSpan.FromMinutes(5),
            names = s.Names, capturedAt = s.At.ToString("O"),
            roster = rosters.TryGetValue(s.Group, out var names) ? names : [],
        });
    }

    private void Push(object message)
    {
        try { Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }

    private void StartWatching()
    {
        if (_watcher != null || !Directory.Exists(_feed.Root)) return;
        _watcher = new FileSystemWatcher(_feed.Root, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
        // The collector writes a .tmp file and renames it: the rename is the finished read.
        void OnFile(string path) => Task.Run(async () =>
        {
            await Task.Delay(300);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || ExtensionAttendanceFeed.Read(path) is not { } s) return;
            await Dispatcher.InvokeAsync(() => { if (_ready) PushSnapshot(s, _rosterByGroup); });
            // A class's first read starts its meeting on the page; its earlier admits go in now.
            await Dispatcher.InvokeAsync(async () => { if (_ready) await PushAdmissionsAsync(); }).Task.Unwrap();
        });
        _watcher.Renamed += (_, e) => OnFile(e.FullPath);
        _watcher.Created += (_, e) => OnFile(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }
}
