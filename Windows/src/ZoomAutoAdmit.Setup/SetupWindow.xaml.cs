using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ZoomAutoAdmit.Setup;

public partial class SetupWindow : Window
{
    private enum Stage { Form, Installing, Done, Failed }
    private Stage _stage = Stage.Form;

    public SetupWindow()
    {
        InitializeComponent();
        Subtitle.Text = $"Version {Installer.Version} · Setup";
        FolderBox.Text = Installer.InstalledFolder() ?? Installer.DefaultFolder;
        ServerBox.Text = Installer.DefaultServer;
        if (Installer.HasSettings())
        {
            ServerPanel.Visibility = Visibility.Collapsed;
            KeptNote.Visibility = Visibility.Visible;
        }
        if (!Installer.HasWebView2()) WebViewWarning.Visibility = Visibility.Visible;
        if (!Installer.HasPayload())
        {
            MainButton.IsEnabled = false;
            KeptNote.Text = "This setup was built without the app inside it. Build it with installer\\build-setup.ps1.";
            KeptNote.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Where to install Zoom Auto Admit", InitialDirectory = Path.GetDirectoryName(FolderBox.Text) ?? "" };
        if (picker.ShowDialog(this) == true) FolderBox.Text = Path.Combine(picker.FolderName, "Zoom Auto Admit");
    }

    private void OnWebView2(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnMain(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Done)
        {
            Installer.Launch(FolderBox.Text);
            Close();
            return;
        }
        if (_stage != Stage.Form && _stage != Stage.Failed) return;

        string folder = FolderBox.Text.Trim();
        string server = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) { MessageBox.Show(this, "Choose where to install.", Title); return; }
        if (!Installer.HasSettings() && server.Length > 0 &&
            !(Uri.TryCreate(server, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "The server address must start with https:// (for example https://your-server.ts.net).", Title);
            return;
        }
        if (Installer.RunningCopies() > 0 &&
            MessageBox.Show(this, "Zoom Auto Admit is open. Close it to install?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _stage = Stage.Installing;
        FormPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        MainButton.IsEnabled = CloseButton.IsEnabled = false;
        bool desktop = DesktopBox.IsChecked == true, launch = LaunchBox.IsChecked == true;
        var progress = new Progress<(int Percent, string Text)>(p => { Progress.Value = p.Percent; ProgressText.Text = p.Text; });
        try
        {
            await Task.Run(() => Installer.Install(folder, server, desktop, progress));
            _stage = Stage.Done;
            ProgressTitle.Text = "Zoom Auto Admit is installed";
            ProgressText.Text = $"Installed in {folder}. It is in the Start menu{(desktop ? " and on the desktop" : "")}, and can be removed from Windows' Apps list.";
            Progress.Value = 100;
            MainButton.Content = "Open Zoom Auto Admit";
            CloseButton.Content = "Close";
            MainButton.IsEnabled = CloseButton.IsEnabled = true;
            if (launch) { Installer.Launch(folder); Close(); }
        }
        catch (Exception ex)
        {
            _stage = Stage.Failed;
            ProgressTitle.Text = "The install did not finish";
            ProgressText.Text = ex.Message;
            MainButton.Content = "Try again";
            MainButton.IsEnabled = CloseButton.IsEnabled = true;
            FormPage.Visibility = Visibility.Visible;
            ProgressPage.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
