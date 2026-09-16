namespace ZoomAutoAdmit.UIAutomation.Window;

/// <summary>
/// One user of Zoom's participants list at a time on this PC. Reading the whole list scrolls it;
/// admitting reads rows by position under a header. Seen live (2026-09-16): a scroll during an
/// admission made the host's own row look like a waiting one. Admission and the attendance walk
/// both take this gate. It is a named system semaphore, because a class started by a Windows task
/// runs in its own process beside the app's.
/// </summary>
public static class ParticipantsPanelGate
{
    private const string Name = @"Local\ZoomAutoAdmit.ParticipantsPanel";

    /// <summary>The gate, or null when someone else kept it for longer than <paramref name="wait"/>.</summary>
    public static IDisposable? TryEnter(TimeSpan wait, CancellationToken token = default)
    {
        var semaphore = new Semaphore(1, 1, Name);
        var deadline = DateTime.UtcNow + wait;
        try
        {
            while (true)
            {
                if (semaphore.WaitOne(TimeSpan.FromMilliseconds(200))) return new Release(semaphore);
                if (token.IsCancellationRequested || DateTime.UtcNow >= deadline) break;
            }
        }
        catch (AbandonedMutexException) { return new Release(semaphore); }
        semaphore.Dispose();
        return null;
    }

    private sealed class Release(Semaphore semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            try { semaphore.Release(); } catch (SemaphoreFullException) { }
            semaphore.Dispose();
        }
    }
}
