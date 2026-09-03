using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZoomAutoAdmit.SessionRoles;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// A small notification shown on the desktop, outside the app window: bottom-right, on top, never
/// stealing focus, fading away on its own. Purely informational — nothing depends on it.
/// </summary>
public partial class DesktopToast : Window
{
    private static readonly List<DesktopToast> Live = [];
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(9);
    private DispatcherTimer? _timer;

    public DesktopToast() => InitializeComponent();

    public static void Show(SessionRoleNotice notice)
    {
        var (accent, _) = notice.Kind switch
        {
            SessionRoleNoticeKind.CoHostAssigned => ("#2ED9A0", 0),
            SessionRoleNoticeKind.NoCoHostFound => ("#F5A524", 0),
            _ => ("#F4525F", 0)
        };
        Show(notice.Title, notice.Message, accent);
    }

    public static void Show(string title, string message, string accentColor = "#6F9DFF")
    {
        var toast = new DesktopToast();
        toast.TitleText.Text = title;
        toast.MessageText.Text = message;
        toast.Accent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor));
        toast.Loaded += (_, _) => toast.Place();
        Live.Add(toast);
        toast.Closed += (_, _) => { Live.Remove(toast); Restack(); };
        toast.Show();
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        toast._timer = new DispatcherTimer { Interval = Lifetime };
        toast._timer.Tick += (_, _) => toast.Dismiss();
        toast._timer.Start();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12 - StackOffset(this);
    }

    private static double StackOffset(DesktopToast toast)
    {
        double offset = 0;
        foreach (var other in Live)
        {
            if (ReferenceEquals(other, toast)) break;
            offset += other.ActualHeight;
        }
        return offset;
    }

    private static void Restack()
    {
        var area = SystemParameters.WorkArea;
        double offset = 0;
        foreach (var toast in Live)
        {
            toast.Top = area.Bottom - toast.ActualHeight - 12 - offset;
            offset += toast.ActualHeight;
        }
    }

    private void Dismiss()
    {
        _timer?.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Dismiss();
}
