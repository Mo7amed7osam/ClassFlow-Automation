using System.Windows;
using System.Windows.Threading;
using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Services;
using ZoomAutoAdmit.WindowsUI.ViewModels;

namespace ZoomAutoAdmit.WindowsUI;

public partial class App : Application
{
    private WindowsUiService? _service;
    private MainViewModel? _viewModel;
    private int _errorDialogActive;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // "Uninstall" in Windows' Apps list: remove the app and stop, without opening it.
        if (e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Services.Uninstaller.Run();
            Shutdown();
            return;
        }
        WindowsUiRuntimeLog.Initialize();
        WindowsUiRuntimeLog.Write("STARTUP", "Application startup entered.");
        // Tier 2 means the GPU draws the window. Tier 0 is software rendering, where every
        // shadow and gradient is paid for on the CPU and the whole app feels slow.
        WindowsUiRuntimeLog.Write("STARTUP",
            $"Render tier: {System.Windows.Media.RenderCapability.Tier >> 16} " +
            $"({(System.Windows.Media.RenderCapability.Tier >> 16 == 0 ? "software" : "hardware")}).");
        SmoothScrolling.Enable();
        RegisterExceptionHandlers();

        // Do not let a service/configuration failure terminate the application before
        // WPF has an authoritative main window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Window window;
        try
        {
            var main = new MainWindow();
            main.RestoreSavedDesign();
            window = main;
            WindowsUiRuntimeLog.Write("STARTUP", "MainWindow created.");
        }
        catch (Exception ex)
        {
            WindowsUiErrorLog.Write("MainWindow construction failed.", ex);
            window = new Window
            {
                Title = "Zoom Auto Admit",
                Width = 720,
                Height = 420,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "The main interface could not be loaded. See windows-ui.log for details.",
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        MainWindow = window;
        window.Show();
        WindowsUiRuntimeLog.Write("STARTUP", "MainWindow assigned and shown.");
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        try
        {
            var bootstrapper = new WindowsRuntimeBootstrapper();
            _service = new WindowsUiService(bootstrapper);
            _service.EnableSessionRoleAi(new AiRoleMatcher(new AiCredentialStore(), new AiMatchingService()));
            _service.SessionRoleNotice += notice =>
                Dispatcher.BeginInvoke(() => { try { Views.DesktopToast.Show(notice); } catch { } });
            WindowsUiRuntimeLog.Write("SERVICES", "Windows runtime services initialized.");
            _viewModel = new MainViewModel(_service);
            // Saving a profile says so on the desktop, naming the group it was saved for: with one
            // type set up per group, seeing which one was written is the whole confirmation.
            _viewModel.SessionRoles.ProfileSaved += (title, message) =>
                Dispatcher.BeginInvoke(() => { try { Views.DesktopToast.Show(title, message, "#2ED9A0"); } catch { } });
            WindowsUiRuntimeLog.Write("VIEWMODELS", "Main view model graph created.");
            // The app opens on the Dashboard: who is signed in, their LMS account, every session.
            _viewModel.SelectedTabIndex = MainViewModel.DashboardPage;
            window.DataContext = _viewModel;
            await _viewModel.InitializeAsync();
            WindowsUiRuntimeLog.Write("VIEWMODELS", "View model initialization completed.");
            // A new coordinator's PC: nothing is set up yet, so the app walks them through it first.
            if (window is MainWindow && Views.WelcomeWindow.IsFirstRun(_viewModel))
            {
                var model = _viewModel;
                _ = Dispatcher.BeginInvoke(() => Views.WelcomeWindow.Open(window, model), DispatcherPriority.ApplicationIdle);
            }
            StartCentralServerInBackground(_viewModel.RecordingsDashboard);
            RepairScheduledMeetingsInBackground(bootstrapper);
            // Opened mid-class (after a crash, or closed by hand): take the open meeting back.
            _ = Task.Run(() => MeetingAdoption.AdoptLiveMeetingAsync(bootstrapper));
        }
        catch (Exception ex)
        {
            ReportNonCriticalError(
                "Windows UI initialization failed. The application will remain open.",
                ex);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
            if (_service != null) await _service.DisposeAsync();
        }
        catch (Exception ex) { WindowsUiErrorLog.Write("Application shutdown failed.", ex); }
        finally
        {
            UnregisterExceptionHandlers();
            WindowsUiRuntimeLog.Shutdown();
            base.OnExit(e);
        }
    }

    /// <summary>
    /// On the server PC, the central backend and its agent start with the app (and end with it:
    /// disposing the view model stops them). A failure is shown on the Recordings page, never here.
    /// </summary>
    private static void StartCentralServerInBackground(RecordingsDashboardViewModel dashboard) =>
        _ = Task.Run(async () =>
        {
            try { await dashboard.StartIfConfiguredAsync(); }
            catch (Exception ex) { WindowsUiErrorLog.Write("The central server could not be started with the app.", ex); }
        });

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnregisterExceptionHandlers()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WindowsUiErrorLog.Write("Unhandled UI thread exception.", e.Exception);
        WindowsUiRuntimeLog.Write("EXCEPTION", e.Exception.ToString());
        e.Handled = true;
        ShowErrorDialog("An unexpected interface error occurred. The application will stay open.", e.Exception);
    }

    private static readonly DateTime StartedAt = DateTime.Now;

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WindowsUiErrorLog.Write("Unhandled application exception.", exception);
        if (e.ExceptionObject is Exception runtimeException)
            WindowsUiRuntimeLog.Write("EXCEPTION", runtimeException.ToString());
        if (!e.IsTerminating) return;
        // The app is going down. A class in progress keeps its admission (in its own process), and
        // the app comes back by itself a few seconds later and takes the meeting back.
        try
        {
            if (!MeetingAdoption.ZoomMeetingIsOpen()) return;
            MeetingAdoption.StartStandaloneAdmission();
            if (DateTime.Now - StartedAt < TimeSpan.FromMinutes(2)) return;     // never a restart loop
            string exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ZoomAutoAdmit.WindowsUI.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c timeout /t 5 /nobreak >nul & start \"\" \"{exe}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            WindowsUiRuntimeLog.Write("EXCEPTION", "The app will reopen by itself in a few seconds; admission continues meanwhile.");
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WindowsUiErrorLog.Write("Unobserved background task exception.", e.Exception);
        WindowsUiRuntimeLog.Write("EXCEPTION", e.Exception.ToString());
        e.SetObserved();
        Dispatcher.BeginInvoke(() =>
            ShowErrorDialog("A background service reported an error. The application will stay open.", e.Exception));
    }

    /// <summary>
    /// A scheduled meeting's task names its program by full path, so moving the app leaves every
    /// one of them pointing at a place that is gone - and they fail without a word, because nobody
    /// is watching a scheduled meeting. Each start checks the upcoming ones and re-points any that
    /// are broken. It runs off the window's thread: a few seconds of checking must not hold it up.
    /// </summary>
    private static void RepairScheduledMeetingsInBackground(WindowsRuntimeBootstrapper bootstrapper)
    {
        if (bootstrapper.TaskScheduler is not WindowsTaskSchedulerService scheduler) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await ScheduleTaskRepair.For(bootstrapper.ScheduleStore, scheduler)
                    .RepairAsync(DateOnly.FromDateTime(DateTime.Now));
                WindowsUiRuntimeLog.Write("SCHEDULE", result.Summary);
                foreach (string line in result.Details) WindowsUiRuntimeLog.Write("SCHEDULE", line);
                // Only something that changed is worth showing in the Logs page.
                if (result.Repaired > 0 || result.Failed > 0) ConsoleLogger.Info($"[SCHEDULE] {result.Summary}");
            }
            catch (Exception ex) { WindowsUiRuntimeLog.Write("SCHEDULE", $"Checking scheduled meetings failed: {ex.GetType().Name}"); }
        });
    }

    private void ReportNonCriticalError(string message, Exception exception)
    {
        WindowsUiErrorLog.Write(message, exception);
        WindowsUiRuntimeLog.Write("INITIALIZATION", $"{message} {exception}");
        ShowErrorDialog(message, exception);
    }

    private void ShowErrorDialog(string message, Exception exception)
    {
        if (Interlocked.Exchange(ref _errorDialogActive, 1) != 0) return;
        try
        {
            string detail = exception is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions[0].Message
                : exception.Message;
            MessageBox.Show(
                MainWindow,
                $"{message}{Environment.NewLine}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}Log: {WindowsUiErrorLog.FilePath}",
                "Zoom Auto Admit",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { Interlocked.Exchange(ref _errorDialogActive, 0); }
    }
}
