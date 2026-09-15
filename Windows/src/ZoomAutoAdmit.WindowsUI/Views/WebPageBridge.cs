using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// A page of the app drawn in WebView2 from a folder beside the app (a virtual host, never the
/// network), with the message protocol the React pages use: { id, method, params } in,
/// { id, result | error } back, { push: "state" | "theme" } whenever something changed.
/// "ready" and "state" are answered here; every other method goes to the page's handler.
/// </summary>
public sealed class WebPageBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebView2 _browser;
    private readonly string _host, _folder, _page;
    private readonly Func<string, JsonElement, Task<object?>> _handle;
    private readonly Func<object> _state;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _ready;

    public WebPageBridge(WebView2 browser, string host, string folder, string page, Func<string, JsonElement, Task<object?>> handle, Func<object> state)
    {
        (_browser, _host, _folder, _page, _handle, _state) = (browser, host, folder, page, handle, state);
        _debounce.Tick += (_, _) => { _debounce.Stop(); PushState(); };
    }

    public async Task InitializeAsync()
    {
        var environment = await RecordingsDashboardView.SharedEnvironment.Value;
        await _browser.EnsureCoreWebView2Async(environment);
        var core = _browser.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(_host, Path.Combine(AppContext.BaseDirectory, _folder), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Host != _host) { e.Cancel = true; OpenOutside(e.Uri); }
        };
        SetBackground(MainWindow.CurrentIsDark);
        // The theme is changed from the main window; this page may live in another window (Get started),
        // on its own thread in tests, or be closed already.
        MainWindow.ThemeChanged += dark => _browser.Dispatcher.BeginInvoke(() =>
        {
            try { SetBackground(dark); Push(new { push = "theme", dark }); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
        });
        char joiner = _page.Contains('?') ? '&' : '?';
        core.Navigate($"https://{_host}/{_page}{joiner}theme={(MainWindow.CurrentIsDark ? "dark" : "light")}");
    }

    private void SetBackground(bool dark) =>
        _browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x0D, 0x10, 0x17) : System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9);

    /// <summary>Something changed: the page gets the new state once things settle.</summary>
    public void Schedule() => _browser.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    public static void OpenOutside(string url)
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
        var parameters = message.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        try
        {
            object? result = method switch
            {
                "ready" => Ready(),
                "state" => _state(),
                _ => await _handle(method, parameters),
            };
            if (id != null) Push(new { id, result = result ?? true });
            Schedule();
        }
        catch (Exception ex) { if (id != null) Push(new { id, error = ex.Message }); }
    }

    private bool Ready() { _ready = true; Schedule(); return true; }

    public void PushState() { if (_ready) Push(new { push = "state", state = _state() }); }

    public void Push(object message)
    {
        try { _browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json)); } catch { }
    }

    // Small readers for the handlers.
    public static string Text(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static bool Flag(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    public static string[] List(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)] : [];
    public static int Number(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
