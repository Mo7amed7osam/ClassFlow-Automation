using ZoomAutoAdmit.Core.Formatting;

namespace ZoomAutoAdmit.Core.Meetings;

/// <summary>
/// Keeps a meeting's admission monitor alive for as long as the meeting is.
///
/// An unexpected error inside the monitor used to end it for good, silently: the meeting went on
/// in its window or browser while nobody was admitted, nobody was made co-host and no attendance
/// was taken, and nothing was written down about why (2026-09-16, S8 and another coordinator's
/// class). Now the error is logged and the monitor is started again after a short pause. Only
/// stopping the session ends it - or the monitor finishing on its own, which means the meeting did.
/// </summary>
public static class MonitorSupervisor
{
    public static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(1);

    /// <returns>The monitor's own result when it finished by itself, or the fallback when stopped.</returns>
    public static async Task<T> RunAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> monitor,
        T whenStopped,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var pause = FirstPause;
        int failures = 0;
        while (true)
        {
            try
            {
                var result = await monitor(token);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return whenStopped;
            }
            catch (Exception ex)
            {
                failures++;
                string first = (ex.Message ?? "").Split('\n')[0].Trim();
                ConsoleLogger.Error($"[AUTO_ADMIT] {name} monitor failed ({ex.GetType().Name}: {first}); starting it again in {pause.TotalSeconds:0} s (failure {failures}).");
            }
            try { await delay(pause, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return whenStopped; }
            pause = TimeSpan.FromTicks(Math.Min(LongestPause.Ticks, pause.Ticks * 2));
            ConsoleLogger.Info($"[AUTO_ADMIT] {name} monitor starting again");
        }
    }

    public static Task RunAsync(string name, Func<CancellationToken, Task> monitor, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        RunAsync(name, async t => { await monitor(t); return true; }, true, token, delay);
}
