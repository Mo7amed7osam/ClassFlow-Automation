using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Meetings;
using ZoomAutoAdmit.Core.Models;
using ZoomAutoAdmit.Inspector.Runtime;
using ZoomAutoAdmit.WebAutomation;
using ZoomAutoAdmit.WindowsRuntime.Scheduling;

namespace ZoomAutoAdmit.Inspector.Commands;

public static class MeetingStartCommand
{
    public static async Task<int> ExecuteAsync(
        CliOptions options,
        CancellationToken cancellationToken = default)
    {
        // A class started by a Windows task (or by hand from the command line) runs with no window:
        // everything it logs also goes to Logs\meetings\<account>-<date>.log, so it can be followed.
        using var logFile = MeetingLogFile.Start(options.AccountId);
        await using var bootstrapper = new WindowsRuntimeBootstrapper();
        MeetingSchedule? loadedSchedule = null;

        if (options.ScheduleId.HasValue && options.ScheduleId.Value != Guid.Empty)
        {
            try
            {
                var schedules = await bootstrapper.ScheduleStore.ListAsync(cancellationToken);
                var schedule = schedules.FirstOrDefault(s => s.Id == options.ScheduleId.Value);
                if (schedule != null)
                {
                    loadedSchedule = schedule;
                    // Same early start the in-app scheduler uses, so both paths open a meeting
                    // at the same moment.
                    if (schedule.OccurrenceDate.HasValue &&
                        (!schedule.Enabled || schedule.OccurrenceDate.Value != DateOnly.FromDateTime(DateTime.Now) ||
                         schedule.LastTriggeredDate == DateOnly.FromDateTime(DateTime.Now) ||
                         DateTime.Now < schedule.LaunchMoment(schedule.OccurrenceDate.Value)))
                    {
                        ConsoleLogger.Info("[SCHEDULER] One-time schedule is disabled, already claimed, or not due today.");
                        return 0;
                    }
                    if (string.IsNullOrWhiteSpace(options.AccountId)) options.AccountId = schedule.AccountId;
                    if (string.IsNullOrWhiteSpace(options.MeetingUrl)) options.MeetingUrl = schedule.MeetingUrl;
                    // Marked opened only once it has opened (ScheduledClassStarter), so a failed
                    // start is tried again - here and by the app's own scheduler.
                }
            }
            catch (Exception ex)
            {
                ConsoleLogger.Warn($"Failed to load schedule metadata: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(options.AccountId))
        {
            const string msg = "meeting-start requires --account-id <ID> or a valid --schedule-id.";
            ConsoleLogger.Error(msg);
            WindowsSchedulerLog.Write("ERROR", msg);
            return 1;
        }
        if (string.IsNullOrWhiteSpace(options.MeetingUrl))
        {
            const string msg = "meeting-start requires --meeting-url <Zoom URL> or a valid --schedule-id.";
            ConsoleLogger.Error(msg);
            WindowsSchedulerLog.Write("ERROR", msg);
            return 1;
        }

        Uri meetingUrl;
        try { meetingUrl = ZoomWebMeetingController.ValidateMeetingUrl(options.MeetingUrl); }
        catch (ArgumentException ex)
        {
            ConsoleLogger.Error(ex.Message);
            WindowsSchedulerLog.Write("ERROR", ex.Message);
            return 1;
        }

        WindowsSchedulerLog.Write("SCHEDULE_TRIGGERED", $"Account: {options.AccountId}, Url: {options.MeetingUrl}, ScheduleId: {options.ScheduleId}");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.TimeoutExplicitlySet && options.TimeoutSeconds > 0)
            linkedCancellation.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            linkedCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        MeetingSession? session = null;
        try
        {

            if (loadedSchedule != null)
            {
                // A scheduled class: opened once across every process, retried with the other
                // engine until it opens (see ScheduledClassStarter).
                var day = loadedSchedule.OccurrenceDate ?? DateOnly.FromDateTime(DateTime.Now);
                session = await new ScheduledClassStarter(new OrchestratedScheduledMeetingRunner(bootstrapper.Orchestrator),
                        bootstrapper.ScheduleStore, message => WindowsSchedulerLog.Write("SCHEDULER", message))
                    .StartAsync(loadedSchedule, day, DateTimeOffset.Now, linkedCancellation.Token);
                if (session == null) return 0;       // opened (or being opened) by the app itself
            }
            else
                session = await bootstrapper.Orchestrator.RunAsync(
                    new ScheduledMeeting(meetingUrl, options.AccountId, DateTimeOffset.UtcNow, GroupId: options.AccountId,
                        // "--engine web" opens it in the browser; otherwise the account's own choice.
                        PreferredEngine: options.Engine == "web" ? ZoomAutoAdmit.Core.Sessions.SessionEngineType.Web : null),
                    linkedCancellation.Token);
            if (session.State == MeetingState.Failed)
            {
                string reason = session.FailureReason ?? "Meeting startup failed.";
                ConsoleLogger.Error($"[MEETING] Failed: {reason}");
                WindowsSchedulerLog.Write("ERROR", reason);
                return linkedCancellation.IsCancellationRequested ? 0 : 1;
            }

            ConsoleLogger.Success($"[ALLOCATOR] Selected engine: {session.Allocation!.EngineType}");
            ConsoleLogger.Success("[MEETING] Started");
            WindowsSchedulerLog.Write("MEETING_STARTED", $"Session: {session.SessionId}, Engine: {session.Allocation!.EngineType}, Account: {options.AccountId}, Url: {options.MeetingUrl}");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested) { }
            return 0;
        }
        catch (Exception ex)
        {
            WindowsSchedulerLog.Write("ERROR", ex.Message);
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (session is { State: not MeetingState.Failed and not MeetingState.Ended })
                await bootstrapper.Orchestrator.EndAsync(session, CancellationToken.None);
        }
    }
}

/// <summary>Appends every log line of this process to a per-meeting file under Logs\meetings.</summary>
internal sealed class MeetingLogFile : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    private MeetingLogFile(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        ConsoleLogger.EntryWritten += Write;
    }

    public static MeetingLogFile? Start(string? accountId)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Logs", "meetings");
            Directory.CreateDirectory(folder);
            string name = string.Concat((string.IsNullOrWhiteSpace(accountId) ? "meeting" : accountId).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return new MeetingLogFile(Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd}.log"));
        }
        catch { return null; }
    }

    private void Write(LogEntry entry)
    {
        try { lock (_sync) _writer.WriteLine($"[{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{entry.Level}] {entry.Message}"); }
        catch { }
    }

    public void Dispose()
    {
        ConsoleLogger.EntryWritten -= Write;
        try { _writer.Dispose(); } catch { }
    }
}
