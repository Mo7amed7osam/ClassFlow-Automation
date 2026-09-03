namespace ZoomAutoAdmit.Attendance;

public enum AttendanceSource { Desktop, Web }
public enum SnapshotTrigger { MeetingStart, Interval, Admission, Manual, MeetingEnd }

// Names are raw observations, not student identities. Duplicate names are intentional.
public sealed record ParticipantPresence(string Name);

public sealed record ParticipantReadResult(
    IReadOnlyList<ParticipantPresence> Participants,
    bool IsComplete = false,
    string? Note = null);

public sealed record AttendanceSnapshot(
    Guid SessionId,
    DateTimeOffset Timestamp,
    AttendanceSource Source,
    IReadOnlyList<ParticipantPresence> Participants,
    SnapshotTrigger Trigger,
    bool IsComplete,
    string? Note)
{
    public AttendanceMeetingMetadata? Meeting { get; init; }
    public string Reason => Trigger switch
    {
        SnapshotTrigger.Admission => "AdmitEvent",
        SnapshotTrigger.Interval => "Scheduled",
        _ => Trigger.ToString()
    };
}

// Deliberately excludes credentials, Zoom login email, and meeting URL query/passcode.
public sealed record AttendanceMeetingMetadata(
    string AccountId, string AccountDisplayName, string MeetingUrl,
    DateTimeOffset ScheduledStart, string Engine);

public interface IAttendanceParticipantSource
{
    AttendanceSource Source { get; }
    Task<ParticipantReadResult> ReadAsync(CancellationToken cancellationToken);
}

// A capture that could not be read at all. Never a snapshot: zero names read is not zero names present.
public sealed record AttendanceCaptureIssue(Guid SessionId, DateTimeOffset Timestamp, SnapshotTrigger Trigger, string Reason);

public interface IAttendanceSnapshotStore
{
    Task SaveAsync(AttendanceSnapshot snapshot, CancellationToken cancellationToken);
    /// <summary>Records a failed read so the interface can show that a capture was attempted, and why it failed.</summary>
    Task SaveIssueAsync(AttendanceCaptureIssue issue, CancellationToken cancellationToken) => Task.CompletedTask;
}
