using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.WindowsUI.Services;

public sealed record MeetingActivity(DateTimeOffset Timestamp, Guid SessionId, string AccountId, string Engine, string Event);
public interface IMeetingActivitySource { IReadOnlyList<MeetingActivity> GetMeetingActivity(); }

// Observes existing session-scoped lifecycle notifications; never issues admission actions.
public sealed class MeetingActivityFeed : IMeetingActivitySource, IDisposable
{
    private readonly MeetingLifecycleEvents _events;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, (string Account, string Engine)> _owners = new();
    private readonly Queue<MeetingActivity> _history = new();
    public MeetingActivityFeed(MeetingLifecycleEvents events)
    {
        _events = events; events.Lifecycle += OnLifecycle; events.AdmissionVerified += OnAdmission;
    }
    private Task OnLifecycle(MeetingLifecycleEvent message)
    {
        lock (_sync)
        {
            var id = message.Context.Session.SessionId;
            var owner = (message.Context.Account.AccountId, message.Context.EngineType.ToString());
            if (message.Kind == MeetingLifecycleEventKind.Active)
            {
                if (_owners.ContainsKey(id)) return Task.CompletedTask;
                _owners[id] = owner;
            }
            else if (!_owners.Remove(id)) return Task.CompletedTask;
            Add(new(DateTimeOffset.Now, id, owner.AccountId, owner.Item2,
                message.Kind == MeetingLifecycleEventKind.Active ? "Meeting active" : "Monitor ended"));
        }
        return Task.CompletedTask;
    }
    private void OnAdmission(Guid id)
    {
        lock (_sync)
            if (_owners.TryGetValue(id, out var owner))
                Add(new(DateTimeOffset.Now, id, owner.Account, owner.Engine, "Admission verified"));
    }
    private void Add(MeetingActivity item) { _history.Enqueue(item); while (_history.Count > 200) _history.Dequeue(); }
    public IReadOnlyList<MeetingActivity> GetMeetingActivity() { lock (_sync) return _history.Reverse().ToArray(); }
    public void Dispose() { _events.Lifecycle -= OnLifecycle; _events.AdmissionVerified -= OnAdmission; }
}
