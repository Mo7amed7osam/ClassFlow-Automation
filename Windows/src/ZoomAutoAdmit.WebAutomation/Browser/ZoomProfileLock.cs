using ZoomAutoAdmit.Core.Formatting;
using ZoomAutoAdmit.Core.Sessions;

namespace ZoomAutoAdmit.WebAutomation.Browser;

/// <summary>
/// Which browser profile a launch can actually use. Chromium opens one user-data directory once:
/// a second browser on the same directory refuses to start ("the profile is already in use by
/// another instance of Chromium"), and a browser that was killed leaves its lock files behind, so
/// the directory looks taken when nothing holds it.
/// </summary>
public static class ZoomProfileLock
{
    /// <summary>Chromium's own marks that a browser is running on a profile.</summary>
    private static readonly string[] LockFiles = ["SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile"];

    /// <summary>How many copies of one profile may exist, as the allocator also allows.</summary>
    public const int MostCopies = 8;

    /// <summary>
    /// The profile to launch: the one asked for when it is free or only stale locks remain, else
    /// the first of its copies that is. <paramref name="said"/> is what to log, when anything
    /// happened. The requested profile is returned unchanged when every copy is busy - the launch
    /// then fails with Chromium's own message rather than silently using something else.
    /// </summary>
    public static ZoomBrowserProfile Free(ZoomBrowserProfile profile, out string? said)
    {
        said = null;
        if (!IsHeld(profile.DirectoryPath))
        {
            if (ClearStaleLocks(profile.DirectoryPath)) said = $"'{profile.Name}' had a lock left by a browser that is gone; it was cleared.";
            return profile;
        }

        string root = Path.GetDirectoryName(profile.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty;
        string baseName = AccountWebProfile.BaseProfileOf(profile.Name) ?? profile.Name;
        if (root.Length == 0) return profile;
        var manager = new ZoomProfileManager(root);
        for (int instance = 2; instance <= MostCopies; instance++)
        {
            string name = AccountWebProfile.ForProfileInstance(baseName, instance);
            if (string.Equals(name, profile.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var copy = manager.GetOrCreate(name);
            if (IsHeld(copy.DirectoryPath)) continue;
            ClearStaleLocks(copy.DirectoryPath);
            said = $"'{profile.Name}' is open in another browser; this meeting uses '{copy.Name}' instead.";
            return copy;
        }
        said = $"every copy of '{baseName}' is open in another browser.";
        return profile;
    }

    /// <summary>Whether a browser is running on this profile right now: its lock cannot be removed.</summary>
    public static bool IsHeld(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (string name in LockFiles)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                // Held by a live browser: Windows refuses to open it for writing. Gone: it opens.
                using var open = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    /// <summary>Removes the marks of a browser that is no longer there. True when any were removed.</summary>
    private static bool ClearStaleLocks(string directory)
    {
        bool removed = false;
        foreach (string name in LockFiles)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); removed = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ConsoleLogger.Info($"[WEB_PROFILE] {name} in '{Path.GetFileName(directory)}' is still held; it was left alone.");
            }
        }
        return removed;
    }
}
