using Xunit;

namespace ZoomAutoAdmit.WindowsRuntime.Tests;

/// <summary>
/// A test that creates and deletes a real Windows scheduled task. It is skipped unless
/// ZOOMAUTOADMIT_LIVE_SCHEDULER_TESTS=1, so running the suite never touches the Task Scheduler
/// of the machine it runs on. When it does run, it must register under <see cref="TaskFolder"/>,
/// never under the \ZoomAutoAdmit\ folder that holds the user's meetings.
/// </summary>
public sealed class LiveSchedulerFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "ZOOMAUTOADMIT_LIVE_SCHEDULER_TESTS";

    public const string TaskFolder = "ZoomAutoAdmitTests";

    public LiveSchedulerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Creates a real Windows scheduled task (in \\{TaskFolder}\\). " +
                   $"Set {EnvironmentVariable}=1 to run it.";
        }
    }
}
