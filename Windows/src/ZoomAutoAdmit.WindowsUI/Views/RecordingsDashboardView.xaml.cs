using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.ViewModels;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// The central backend's dashboard, inside the app (WebView2, the Edge engine Windows 11 ships).
/// Only pages of the dashboard's own address open here; any other link (a Drive recording, a Zoom
/// link) opens in the normal browser. Its cookies - the dashboard sign-in - are kept in the app's own
/// WebView2 folder, so a sign-in lasts as long as the backend's session does.
/// </summary>
public partial class RecordingsDashboardView : UserControl
{
    private static readonly string BrowserData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "WebView2");
    // One browser environment (one profile folder) for the whole app: WebView2 refuses a second
    // environment for the same folder, and the page may be loaded again after it was unloaded.
    internal static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment =
        new(() => CoreWebView2Environment.CreateAsync(null, BrowserData));
    private RecordingsDashboardViewModel? _model;
    private Task? _initializing;
    private bool _ready, _failed;

    public RecordingsDashboardView()
    {
        InitializeComponent();
        Root.DataContextChanged += (_, _) => Attach(Root.DataContext as RecordingsDashboardViewModel);
        Loaded += (_, _) =>
        {
            Attach(Root.DataContext as RecordingsDashboardViewModel);
            _initializing ??= InitializeBrowserAsync();                    // once, however often the page is shown
        };
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(RecordingsDashboardViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model != null) { _model.ReloadRequested -= Show; _model.PropertyChanged -= OnModelChanged; }
        _model = model;
        if (_model != null) { _model.ReloadRequested += Show; _model.PropertyChanged += OnModelChanged; Show(); }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingsDashboardViewModel.CanShowDashboard)) Show();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var environment = await SharedEnvironment.Value;
            await Browser.EnsureCoreWebView2Async(environment);
            var core = Browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;       // passwords belong in Credential Manager, not here
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowPlaceholder("The dashboard did not load", $"The server did not answer ({e.WebErrorStatus}). Check that it is running, then press Reload.");
            };
            _ready = true;
            Show();
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException or IOException)
        {
            _failed = true;
            WindowsUiErrorLog.Write("WebView2 could not start for the Recordings page.", ex);
            ShowPlaceholder("This page needs Microsoft Edge WebView2",
                "Install the WebView2 Runtime from Microsoft (it comes with Windows 11), or use \"Open in browser\".");
        }
    }

    /// <summary>Loads (or reloads) the dashboard when it can be shown; otherwise says why not.</summary>
    private void Show()
    {
        if (_model is null || _failed) return;
        if (!_model.CanShowDashboard || _model.DashboardUri is not { } uri)
        {
            ShowPlaceholder(_model.IsServerMode ? "The server is not running" : "No server is set",
                _model.IsServerMode ? _model.StatusDetail : "Enter the server address under Connection.");
            return;
        }
        if (!_ready) return;
        Placeholder.Visibility = Visibility.Collapsed;
        if (Browser.CoreWebView2.Source is { } current && Uri.TryCreate(current, UriKind.Absolute, out var here) && _model.IsInsideDashboard(here))
            Browser.CoreWebView2.Reload();
        else
            Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    private void ShowPlaceholder(string title, string text)
    {
        PlaceholderTitle.Text = title;
        PlaceholderText.Text = text;
        Placeholder.Visibility = Visibility.Visible;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_model is null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)) { e.Cancel = true; return; }
        if (_model.IsInsideDashboard(target)) return;
        e.Cancel = true;
        RecordingsDashboardViewModel.OpenExternally(target);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;                                         // never a second, unmanaged window
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)) return;
        if (_model?.IsInsideDashboard(target) == true) Browser.CoreWebView2.Navigate(target.AbsoluteUri);
        else RecordingsDashboardViewModel.OpenExternally(target);
    }

    private void SavePassword(object sender, RoutedEventArgs e)
    {
        _model?.SavePassword(DatabasePassword.Password);
        DatabasePassword.Clear();
    }
}
