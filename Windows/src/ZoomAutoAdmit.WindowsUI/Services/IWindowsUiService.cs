using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Sessions;
using ZoomAutoAdmit.WindowsRuntime;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.WindowsUI.Services;

public enum EnginePreference
{
    Auto,
    Desktop,
    Web
}

public sealed record SessionDisplayInfo(
    Guid SessionId,
    string AccountId,
    string AccountName,
    SessionEngineType EngineType,
    string State,
    DateTimeOffset StartTime);

public sealed record UiOperationResult(bool IsSuccess, string Message);

public sealed record UiActionStatus(
    string LastAction,
    string CurrentOperation,
    string ErrorMessage,
    bool IsBusy,
    DateTimeOffset UpdatedAt);

/// <summary>A meeting that has just gone live, however it was started.</summary>
public sealed record LiveMeeting(string GroupId, DateTimeOffset ScheduledStart);

public interface IWindowsUiService
{
    event Action<UiActionStatus>? StatusChanged;
    /// <summary>
    /// Raised once per meeting as it becomes active, for the button and the scheduler alike, so
    /// anything that has to happen when a class starts is hooked up in one place.
    /// </summary>
    event Action<LiveMeeting>? MeetingBecameLive;
    UiActionStatus CurrentStatus { get; }
    Task<IReadOnlyList<WindowsMeetingAccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task SaveAccountAsync(WindowsMeetingAccountMetadata account, CancellationToken cancellationToken = default);
    Task<bool> DeleteAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<UiOperationResult> SwitchAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<SessionDisplayInfo> StartMeetingAsync(
        string accountId,
        string meetingUrl,
        EnginePreference preference,
        CancellationToken cancellationToken = default);
    Task<bool> StopMeetingAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionDisplayInfo>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingSchedule>> GetSchedulesAsync(CancellationToken cancellationToken = default);
    Task SaveScheduleAsync(MeetingSchedule schedule, CancellationToken cancellationToken = default);
    Task<bool> DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default);
}
