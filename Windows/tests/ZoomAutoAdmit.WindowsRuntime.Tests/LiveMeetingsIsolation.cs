using System.Runtime.CompilerServices;

namespace ZoomAutoAdmit.Tests.Isolation;

internal static class LiveMeetingsIsolation
{
    /// <summary>Tests never see, or write, the live-meeting markers of classes really running on this PC.</summary>
    [ModuleInitializer]
    internal static void Isolate() =>
        ZoomAutoAdmit.Core.Meetings.LiveMeetings.Folder = Path.Combine(Path.GetTempPath(), "ZoomAutoAdmit.Tests", "live-" + Guid.NewGuid().ToString("N"));
}
