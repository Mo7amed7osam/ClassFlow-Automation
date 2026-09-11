namespace ZoomAutoAdmit.WebAutomation.Recordings;

/// <summary>Exclusive use of one browser profile for the length of one operation.</summary>
public interface IProfileLock
{
    /// <summary>
    /// Waits up to <paramref name="wait"/> for the profile to be free and takes it. Null means it
    /// stayed busy - another operation, or a browser that already has the profile open.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string profileName, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>
/// One operation per browser profile, across every process on this computer.
///
/// Chromium cannot open one profile folder twice, and two automations driving the same signed-in
/// profile at once can leave it signed out. The app runs in more than one process - the window, the
/// scheduled meetings, the terminal, the API - so an in-memory lock would protect nothing. The lock
/// is a file opened for exclusive use, which Windows releases even if the process dies.
///
/// It also refuses a profile Chromium already holds open (a live Web meeting, a browser left open
/// by hand): Chromium keeps a "lockfile" in the profile folder that it holds exclusively while
/// running. Checking it turns "the browser would not start" into a plain "busy".
///
/// The lock files live in their own folder, never inside a profile: an empty new profile folder is
/// how the app knows to seed it from a signed-in one, and a lock file would stop that.
/// </summary>
public sealed class ProfileOperationLock(string? locksDirectory = null, string? profilesRoot = null) : IProfileLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly string _locks = locksDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Locks");
    private readonly string _profiles = profilesRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit", "Profiles");

    public async Task<IAsyncDisposable?> TryAcquireAsync(string profileName, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        Directory.CreateDirectory(_locks);
        string name = profileName.Trim().ToLowerInvariant();
        string path = Path.Combine(_locks, $"profile-{name}.lock");
        var deadline = DateTimeOffset.UtcNow + (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var held = TryOpen(path);
            if (held != null)
            {
                if (!IsHeldByBrowser(profileName)) return new Held(held);
                await held.DisposeAsync();
            }
            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Whether a running Chromium has this profile folder open right now.</summary>
    public bool IsHeldByBrowser(string profileName)
    {
        string chromiumLock = Path.Combine(_profiles, profileName.Trim(), "lockfile");
        if (!File.Exists(chromiumLock)) return false;
        try
        {
            using var probe = new FileStream(chromiumLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;   // a leftover from a browser that has since exited
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private sealed class Held(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
