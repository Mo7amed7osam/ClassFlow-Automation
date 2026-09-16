using System.IO;
using System.Windows.Threading;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.WindowsUI.Infrastructure;
using ZoomAutoAdmit.WindowsUI.Views;

namespace ZoomAutoAdmit.WindowsUI.Services;

/// <summary>
/// Shows the countdown of any class that is about to be ended for everyone - whichever process is
/// watching it. A class opened by a Windows task is watched by that task's own process, which has
/// no window at all, so the countdown is announced as a file and picked up here.
///
/// It only reads and answers; it never decides. Closing the app simply means nobody answers, and
/// the class ends as it would have.
/// </summary>
public sealed class PendingEndNotifier : IDisposable
{
    private readonly PendingMeetingEnds _ends;
    private readonly Dictionary<Guid, EndCountdownToast> _shown = [];
    private readonly DispatcherTimer _timer;

    public PendingEndNotifier(PendingMeetingEnds? ends = null)
    {
        _ends = ends ?? new PendingMeetingEnds();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Look();
    }

    public void Start() => _timer.Start();

    private void Look()
    {
        IReadOnlyList<PendingMeetingEndNotice> waiting;
        try { waiting = _ends.Waiting(DateTimeOffset.Now); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var notice in waiting)
        {
            if (_shown.TryGetValue(notice.SessionId, out var open)) { open.Tick(notice); continue; }
            try
            {
                var toast = new EndCountdownToast(notice, _ends);
                _shown[notice.SessionId] = toast;
                toast.Show();
            }
            catch (Exception ex)
            {
                WindowsUiErrorLog.Write("The end-of-class countdown could not be shown.", ex);
            }
        }

        // Gone means the class was ended, or the countdown was called off: nothing left to answer.
        foreach (var session in _shown.Keys.Where(id => waiting.All(n => n.SessionId != id)).ToArray())
        {
            if (_shown.Remove(session, out var toast)) { try { toast.Close(); } catch (InvalidOperationException) { } }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        foreach (var toast in _shown.Values) { try { toast.Close(); } catch (InvalidOperationException) { } }
        _shown.Clear();
    }
}
