namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// 1, 2, 4, 8, 16, 30, 30, ... seconds between reconnect attempts, each ± a random 20 %, so a backend
/// restart is not answered by every PC at the same instant. Reset after a connection that lasted.
/// </summary>
public sealed class ReconnectBackoff(TimeSpan initial, TimeSpan maximum, double jitter = 0.2, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;
    private int _attempt;

    public int Attempt => _attempt;

    /// <summary>The delay before attempt <paramref name="attempt"/> (0-based), without jitter.</summary>
    public static TimeSpan BaseDelay(int attempt, TimeSpan initial, TimeSpan maximum)
    {
        double factor = Math.Pow(2, Math.Min(attempt, 30));
        double milliseconds = Math.Min(initial.TotalMilliseconds * factor, maximum.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public TimeSpan Next()
    {
        var delay = BaseDelay(_attempt, initial, maximum);
        _attempt++;
        double spread = 1 + jitter * (_random.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(0, delay.TotalMilliseconds * spread));
    }

    public void Reset() => _attempt = 0;
}
