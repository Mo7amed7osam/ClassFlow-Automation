using System.Windows;
using System.Windows.Threading;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.WindowsUI.Views;

/// <summary>
/// The minutes before a finished class is ended for everyone, counted down on the desktop with the
/// two answers that matter: end it now, or keep it and end it by hand. It never steals focus, and
/// saying nothing lets the countdown run out - the class ends exactly as it would have.
/// The ✕ only hides it; the class is not changed either way.
/// </summary>
public partial class EndCountdownToast : Window
{
    private readonly PendingMeetingEnds _ends;
    private readonly TimeSpan _total;
    private bool _answered;

    public Guid SessionId { get; }

    public EndCountdownToast(PendingMeetingEndNotice notice, PendingMeetingEnds ends)
    {
        InitializeComponent();
        _ends = ends;
        SessionId = notice.SessionId;
        var left = notice.Left(DateTimeOffset.Now);
        _total = left > TimeSpan.FromSeconds(1) ? left : PendingMeetingEnds.Warning;
        MessageText.Text = $"{notice.Group} at {notice.ClassStart:HH:mm} — {notice.Reason}.";
        Loaded += (_, _) => Place();
        Tick(notice);
    }

    /// <summary>The time left, as often as the watcher looks.</summary>
    public void Tick(PendingMeetingEndNotice notice)
    {
        if (_answered) return;
        var left = notice.Left(DateTimeOffset.Now);
        // Minutes while there are minutes left, seconds when it gets close.
        TitleText.Text = left <= TimeSpan.Zero ? "Ending this class…"
            : left >= TimeSpan.FromMinutes(1) ? $"Ending this class in {left:m\\:ss}"
            : $"Ending this class in {left.TotalSeconds:0}s";
        double done = _total.TotalSeconds <= 0 ? 1 : 1 - (left.TotalSeconds / _total.TotalSeconds);
        Bar.Width = Math.Clamp(done, 0, 1) * Math.Max(0, ActualWidth - 52);
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void EndNowClicked(object sender, RoutedEventArgs e) => Answer(PendingEndAnswer.EndNow);

    private void EndManuallyClicked(object sender, RoutedEventArgs e) => Answer(PendingEndAnswer.EndManually);

    private void CloseClicked(object sender, RoutedEventArgs e) => Dismiss();

    private void Answer(PendingEndAnswer answer)
    {
        _answered = true;
        NowButton.IsEnabled = ManualButton.IsEnabled = false;
        TitleText.Text = answer == PendingEndAnswer.EndNow ? "Ending it now…" : "Left open — end it yourself.";
        _ends.Answer(SessionId, answer);
        // Whatever is watching the class acts on the answer and withdraws the countdown, which closes
        // this window. It also closes itself after a moment: an answered countdown must never be left
        // sitting on the desktop, not even when the process that was watching has gone.
        var closing = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        closing.Tick += (_, _) => { closing.Stop(); Dismiss(); };
        closing.Start();
    }

    private void Dismiss()
    {
        try { Close(); }
        catch (InvalidOperationException) { }
    }
}
