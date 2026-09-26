using System.Runtime.CompilerServices;

namespace ZoomAutoAdmit.Tests.Isolation;

internal static class LiveMeetingsIsolation
{
    /// <summary>Tests never see, or write, the live-meeting markers of classes really running on this PC.</summary>
    [ModuleInitializer]
    internal static void Isolate()
    {
        ZoomAutoAdmit.Core.Meetings.LiveMeetings.Folder = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit.Tests", "live-" + Guid.NewGuid().ToString("N"));
        // Nor this PC's remembered AI answers: each test's matching asks its own fake AI.
        ZoomAutoAdmit.WindowsUI.Services.AiMatchingService.DefaultDecisions = () => null;
        // Nor the attendance really taken of this PC's classes, which a match would write over.
        ZoomAutoAdmit.WindowsUI.Services.ExtensionAttendanceFeed.ResultsPath = Path.Combine(
            Path.GetTempPath(), "ZoomAutoAdmit.Tests", "results-" + Guid.NewGuid().ToString("N") + ".json");
        // Nor which of this PC's real classes are held in a room: a test's class is on Zoom unless it says.
        ZoomAutoAdmit.WindowsRuntime.Scheduling.ClassMode.LmsSessions = () => [];
        ZoomAutoAdmit.WindowsRuntime.Scheduling.ClassMode.Schedules = _ => Task.FromResult<IReadOnlyList<ZoomAutoAdmit.WindowsRuntime.Scheduling.MeetingSchedule>>([]);
    }
}
