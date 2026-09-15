using System.IO;
using System.Windows.Threading;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;
using ZoomAutoAdmit.WindowsUI.Views;
using Xunit;

namespace ZoomAutoAdmit.WindowsUI.Tests;

public sealed class WelcomeWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "welcome-tests-" + Guid.NewGuid().ToString("N"));

    // Deliberately unable to save an account, start a meeting or switch Zoom: the steps only open here.
    private sealed class NoService : IWindowsUiService
    {
        public event Action<UiActionStatus>? StatusChanged { add { } remove { } }
        public event Action<LiveMeeting>? MeetingBecameLive { add { } remove { } }
        public UiActionStatus CurrentStatus => new("Test", "Ready", "", false, DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WindowsMeetingAccountMetadata>>([]);
        public Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionDisplayInfo>>([]);
        public Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MeetingSchedule>>([]);
        public Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionDisplayInfo> StartMeetingAsync(string accountId, string meetingUrl, EnginePreference preference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private MainViewModel Model(DashboardMode mode)
    {
        var store = new RecordingsDashboardSettingsStore(Path.Combine(_root, "dashboard.json"));
        store.Save(new RecordingsDashboardSettings { Mode = mode, ServerUrl = "https://central.example.net/" });
        var dashboard = new RecordingsDashboardViewModel(store,
            new DatabasePasswordStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid()),       // nothing saved there: never a real secret
            new CentralServerHost(new DatabasePasswordStore("ZoomAutoAdmit/Tests/" + Guid.NewGuid())));
        // No central server at all: the steps must never sign in to the real one.
        return new MainViewModel(new NoService(), aiCredentials: new AiSetupTests.MemoryStore(), aiService: new AiSetupTests.FakeAi(),
            recordingsDashboard: dashboard, central: new CentralViewModel(new CentralApiClient(() => null)));
    }

    [Fact]
    public void OnlyAPcThatConnectsToAServerIsWalkedThroughSetup()
    {
        using (var server = Model(DashboardMode.Server)) Assert.False(WelcomeWindow.IsFirstRun(server));
        using var client = Model(DashboardMode.Client);
        // True unless this PC already finished the steps (the file lives with the app's data).
        bool finished = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "welcome.json"));
        Assert.Equal(!finished, WelcomeWindow.IsFirstRun(client));
    }

    [Fact]
    public async Task TheStepsOpenOnTheCoordinatorsOwnAccount()
    {
        using var model = Model(DashboardMode.Client);
        string? heading = null, rail = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new WelcomeWindow(model) { ShowActivated = false, ShowInTaskbar = false, Left = -4000 };
            try
            {
                window.Show();
                var frame = new DispatcherFrame();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (int i = 0; i < 360 && heading == null; i++)
                        {
                            await Task.Delay(250);
                            await window.Dispatcher.InvokeAsync(async () =>
                            {
                                var core = ((Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("Browser")).CoreWebView2;
                                rail = "placeholder: " + ((System.Windows.Controls.TextBlock)window.FindName("Placeholder")).Text + " core: " + (core?.Source ?? "none");
                                if (core == null) return;
                                string text = await core.ExecuteScriptAsync("document.querySelector('.stage h1')?.textContent ?? null");
                                if (text != "null") heading = System.Text.Json.JsonSerializer.Deserialize<string>(text);
                                rail = System.Text.Json.JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('.rail')?.innerText ?? ''"));
                                if (heading != null && Environment.GetEnvironmentVariable("ZOOM_WELCOME_CAPTURE") is { Length: > 0 } capture)
                                {
                                    await using var png = File.Create(capture);
                                    await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, png);
                                }
                            }).Task.Unwrap();
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { frame.Continue = false; }
                });
                Dispatcher.PushFrame(frame);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await Task.Run(() => thread.Join(TimeSpan.FromMinutes(2)));
        if (failure != null) throw failure;
        Assert.Equal("Your coordinator account", heading);
        Assert.Contains("LMS account", rail);
        Assert.Contains("Zoom & session links", rail);
        Assert.Contains("OpenRouter", rail);
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { } }
}
