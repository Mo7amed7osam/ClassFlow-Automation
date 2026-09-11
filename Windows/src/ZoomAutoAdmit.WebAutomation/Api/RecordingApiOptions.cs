using System.Security.Cryptography;
using System.Text;

namespace ZoomAutoAdmit.WebAutomation.Api;

/// <summary>
/// Where the recording API listens and the key it accepts, read from the environment.
///
/// The key is never kept as text: only its SHA-256 is held, and requests are compared against that
/// in constant time. There is no default key and no way to run without one - a missing or short key
/// stops the API from starting rather than leaving it open.
/// </summary>
public sealed class RecordingApiOptions
{
    public const string KeyVariable = "ZOOM_AUTO_ADMIT_API_KEY";
    public const string PortVariable = "ZOOM_AUTO_ADMIT_API_PORT";
    public const string LockWaitVariable = "ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS";
    public const int DefaultPort = 47821;
    public const int MinimumKeyLength = 20;

    private readonly byte[] _keyHash;

    private RecordingApiOptions(int port, byte[] keyHash, TimeSpan lockWait)
    {
        Port = port;
        _keyHash = keyHash;
        LockWait = lockWait;
    }

    public int Port { get; }
    /// <summary>How long a request waits for a busy browser profile before it is answered with 409.</summary>
    public TimeSpan LockWait { get; }

    /// <summary>Loopback only. The API is never bound to another interface.</summary>
    public IReadOnlyList<string> Prefixes => [$"http://127.0.0.1:{Port}/", $"http://localhost:{Port}/"];

    /// <summary>
    /// Reads the settings. A missing or weak key, or a bad port, is an error the caller has to
    /// surface: the message names the variable, never its value.
    /// </summary>
    public static RecordingApiOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        string? key = read(KeyVariable);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"{KeyVariable} is not set. The recording API will not run without a key; see RECORDING-API.md.");
        if (key.Trim().Length < MinimumKeyLength)
            throw new InvalidOperationException(
                $"{KeyVariable} is too short. Use at least {MinimumKeyLength} random characters.");

        int port = DefaultPort;
        string? portText = read(PortVariable);
        if (!string.IsNullOrWhiteSpace(portText) &&
            (!int.TryParse(portText.Trim(), out port) || port is < 1024 or > 65535))
            throw new InvalidOperationException($"{PortVariable} must be a port number between 1024 and 65535.");

        var lockWait = TimeSpan.FromMinutes(2);
        string? waitText = read(LockWaitVariable);
        if (!string.IsNullOrWhiteSpace(waitText))
        {
            if (!int.TryParse(waitText.Trim(), out int seconds) || seconds is < 0 or > 1800)
                throw new InvalidOperationException($"{LockWaitVariable} must be a number of seconds between 0 and 1800.");
            lockWait = TimeSpan.FromSeconds(seconds);
        }
        return new RecordingApiOptions(port, Hash(key.Trim()), lockWait);
    }

    /// <summary>For tests: fixed settings without touching the environment.</summary>
    public static RecordingApiOptions ForTesting(int port, string key, TimeSpan? lockWait = null) =>
        new(port, Hash(key), lockWait ?? TimeSpan.FromSeconds(5));

    /// <summary>Whether a presented key is the configured one. Constant time; nothing is logged.</summary>
    public bool KeyMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(presented.Trim()), _keyHash);
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
