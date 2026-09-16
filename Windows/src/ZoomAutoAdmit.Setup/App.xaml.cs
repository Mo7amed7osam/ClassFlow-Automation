using System.Windows;
using System.Windows.Controls;

namespace ZoomAutoAdmit.Setup;

public partial class App : Application
{
    /// <summary>
    /// Run by people: the setup window. Run by the app's "Update now" (--update "&lt;folder&gt;"): a
    /// small window that replaces the installed app, then opens it again.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int at = Array.FindIndex(e.Args, a => a.Equals("--update", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < e.Args.Length) { RunUpdate(e.Args[at + 1]); return; }
        new SetupWindow().Show();
    }

    private void RunUpdate(string folder)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var text = new TextBlock { Text = "Updating Zoom Auto Admit…", FontSize = 13, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100 };
        var window = new Window
        {
            Title = $"Zoom Auto Admit {Installer.Version}",
            Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true,
            Content = new StackPanel { Margin = new Thickness(20), Children = { text, bar } },
        };
        window.Show();
        var progress = new Progress<(int Percent, string Text)>(p => { bar.Value = p.Percent; text.Text = p.Text; });

        Task.Run(() => Installer.Update(folder, progress)).ContinueWith(done =>
        {
            if (done.IsFaulted)
            {
                string why = done.Exception?.GetBaseException().Message ?? "unknown";
                Installer.Log($"update failed: {why}");
                MessageBox.Show(window, $"The update could not be installed, so the app you had is kept.\n\n{why}",
                    "Zoom Auto Admit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // The new version, or the one that was kept: the app is opened again either way.
            Installer.Launch(folder);
            Shutdown();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
