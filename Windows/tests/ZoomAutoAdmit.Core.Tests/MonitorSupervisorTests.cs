using Xunit;
using ZoomAutoAdmit.Core.Meetings;

namespace ZoomAutoAdmit.Core.Tests;

/// <summary>An error inside the admission monitor must not end admission for the rest of the class.</summary>
public sealed class MonitorSupervisorTests
{
    private static Task NoWait(TimeSpan _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }

    [Fact]
    public async Task AMonitorThatFailsIsStartedAgain()
    {
        int runs = 0;
        var pauses = new List<TimeSpan>();
        int result = await MonitorSupervisor.RunAsync("Web", _ =>
        {
            runs++;
            if (runs < 4) throw new InvalidOperationException("a bad pass");
            return Task.FromResult(7);
        }, whenStopped: -1, CancellationToken.None, (pause, token) => { pauses.Add(pause); return NoWait(pause, token); });

        Assert.Equal(4, runs);
        Assert.Equal(7, result);
        // Waits a little longer each time, never more than a minute.
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20) }, pauses);
    }

    [Fact]
    public async Task StoppingTheSessionStopsTheMonitor()
    {
        using var stop = new CancellationTokenSource();
        int runs = 0;
        int result = await MonitorSupervisor.RunAsync("Web", async token =>
        {
            runs++;
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        }, whenStopped: -1, stop.Token, NoWait);

        Assert.Equal(1, runs);
        Assert.Equal(-1, result);
    }

    [Fact]
    public async Task StoppingWhileWaitingToStartAgainDoesNotStartIt()
    {
        using var stop = new CancellationTokenSource();
        int runs = 0;
        await MonitorSupervisor.RunAsync("Windows", _ =>
        {
            runs++;
            throw new TimeoutException("UIA timeout");
        }, stop.Token, (pause, token) => { stop.Cancel(); return NoWait(pause, token); });

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AMonitorThatFinishesByItselfIsNotRestarted()
    {
        int runs = 0;
        int result = await MonitorSupervisor.RunAsync("Windows", _ => { runs++; return Task.FromResult(0); }, -1, CancellationToken.None, NoWait);
        Assert.Equal(1, runs);
        Assert.Equal(0, result);
    }
}
