using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.Core.Meetings;

public sealed record ScheduledMeeting(
    Uri MeetingUrl,
    string AccountId,
    DateTimeOffset StartTime,
    Guid? SessionId = null,
    SessionEngineType? PreferredEngine = null,
    string? GroupId = null,
    DateTimeOffset? ScheduledStartTime = null);

public sealed class MeetingSession
{
    private readonly object _sync = new();
    private readonly List<MeetingStateTransition> _history;
    private MeetingState _state;
    private ActiveSession? _allocation;
    private string? _failureReason;

    public MeetingSession(Guid sessionId, ScheduledMeeting meeting, DateTimeOffset createdAt)
    {
        SessionId = sessionId;
        MeetingUrl = meeting.MeetingUrl;
        AccountId = meeting.AccountId;
        GroupId = string.IsNullOrWhiteSpace(meeting.GroupId) ? meeting.AccountId : meeting.GroupId.Trim();
        StartTime = meeting.ScheduledStartTime ?? meeting.StartTime;
        HasScheduledStart = meeting.ScheduledStartTime.HasValue;
        CreatedAt = createdAt;
        _state = MeetingState.Scheduled;
        _history = [new(MeetingState.Scheduled, createdAt, "Session created.")];
    }

    public Guid SessionId { get; }
    public Uri MeetingUrl { get; }
    public string AccountId { get; }
    public string GroupId { get; }
    public DateTimeOffset StartTime { get; }
    /// <summary>The class's time was given (a scheduled or taken-back class); false: only when it went live.</summary>
    public bool HasScheduledStart { get; }
    public DateTimeOffset CreatedAt { get; }

    public MeetingState State
    {
        get { lock (_sync) return _state; }
    }

    public ActiveSession? Allocation
    {
        get { lock (_sync) return _allocation; }
    }

    public string? FailureReason
    {
        get { lock (_sync) return _failureReason; }
    }

    public IReadOnlyList<MeetingStateTransition> History
    {
        get { lock (_sync) return _history.ToArray(); }
    }

    internal void SetAllocation(ActiveSession allocation)
    {
        lock (_sync) _allocation = allocation;
    }

    internal void TransitionTo(MeetingState state, string? detail = null)
    {
        lock (_sync)
        {
            if (_state is MeetingState.Ended or MeetingState.Failed) return;
            _state = state;
            _history.Add(new(state, DateTimeOffset.UtcNow, detail));
        }
    }

    internal void Fail(string reason)
    {
        lock (_sync)
        {
            if (_state == MeetingState.Ended) return;
            _failureReason = reason;
            _state = MeetingState.Failed;
            _history.Add(new(MeetingState.Failed, DateTimeOffset.UtcNow, reason));
        }
    }
}
