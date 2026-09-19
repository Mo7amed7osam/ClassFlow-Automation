namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// The device token in a file only this user can read, for a machine with no Credential Manager.
///
/// A container has no Windows account to hang a secret on and no keyring worth the dependency, so
/// the token goes in a file with 0600 permissions on a volume that outlives the container. That is
/// weaker than Credential Manager and is the honest trade: the alternative is enrolling again on
/// every restart, which needs a person each time and defeats running unattended.
///
/// What limits the damage is what the token is. It identifies one device to one backend, it is
/// revoked from the dashboard the moment it is suspect, and it opens nothing else - not the LMS,
/// not Zoom, not the database.
/// </summary>
public sealed class FileDeviceTokenStore : IDeviceTokenStore
{
    private readonly string _path;
    private readonly object _gate = new();

    /// <param name="path">
    /// The file. Defaults to $ZAA_STATE_DIR/device-token, then ~/.local/share/ZoomAutoAdmit/device-token,
    /// so a deployment names one volume and everything else follows.
    /// </param>
    public FileDeviceTokenStore(string? path = null)
    {
        _path = path ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public static string DefaultPath()
    {
        string root = Environment.GetEnvironmentVariable("ZAA_STATE_DIR")?.Trim() is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomAutoAdmit");
        return Path.Combine(root, "device-token");
    }

    public string? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            string token = File.ReadAllText(_path).Trim();
            return token.Length == 0 ? null : token;
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        lock (_gate)
        {
            // Created empty and narrowed before anything is written, so the token is never briefly
            // readable by another account on the machine.
            using (var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RestrictToOwner(_path);
                using var writer = new StreamWriter(stream);
                writer.Write(token.Trim());
            }
            RestrictToOwner(_path);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>Owner-only, where the file system has such a thing; NTFS inherits the folder's rights.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
