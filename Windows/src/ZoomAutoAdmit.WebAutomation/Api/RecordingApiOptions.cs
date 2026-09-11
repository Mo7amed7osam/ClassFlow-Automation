using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ZoomAutoAdmit.WebAutomation.Api;

/// <summary>
/// Where the recording API listens, who may connect, and the key it accepts - read from the
/// environment, and refused outright when a setting would widen who can reach it.
///
/// Two ways to listen, and no third:
///
///   Loopback (the default). The API listens on 127.0.0.1 and [::1] only, so nothing off this PC can
///   even open a connection to it. A tunnel connector running on this PC - cloudflared, Tailscale
///   serve, a local reverse proxy - reaches it here and is how it is reached from anywhere else.
///
///   One private address of this PC, for a reverse proxy on another machine on a private network
///   (a LAN, a Tailscale tailnet). It must be an address this PC actually has, it must be private
///   (never a public one), and it only answers the client addresses listed in
///   ZOOM_AUTO_ADMIT_API_ALLOWED_CLIENTS - which must then be set.
///
/// "All addresses" (0.0.0.0, ::, *, +) and host names are refused: they are how an API ends up on the
/// internet by accident. The key is never kept as text: only its SHA-256, compared in constant time.
/// </summary>
public sealed class RecordingApiOptions
{
    public const string KeyVariable = "ZOOM_AUTO_ADMIT_API_KEY";
    public const string HostVariable = "ZOOM_AUTO_ADMIT_API_HOST";
    public const string PortVariable = "ZOOM_AUTO_ADMIT_API_PORT";
    public const string AllowedClientsVariable = "ZOOM_AUTO_ADMIT_API_ALLOWED_CLIENTS";
    public const string LockWaitVariable = "ZOOM_AUTO_ADMIT_API_LOCK_WAIT_SECONDS";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 47821;
    public const int MinimumKeyLength = 20;

    private readonly byte[] _keyHash;

    private RecordingApiOptions(IPAddress? privateAddress, int port, IReadOnlyList<IPNetwork> allowedClients,
        byte[] keyHash, TimeSpan lockWait)
    {
        PrivateAddress = privateAddress;
        Port = port;
        AllowedClients = allowedClients;
        _keyHash = keyHash;
        LockWait = lockWait;
    }

    /// <summary>Null when listening on loopback; otherwise the one private address listened on.</summary>
    public IPAddress? PrivateAddress { get; }
    public bool IsLoopbackOnly => PrivateAddress == null;
    public int Port { get; }
    /// <summary>Who may connect when listening on a private address. Unused on loopback.</summary>
    public IReadOnlyList<IPNetwork> AllowedClients { get; }
    /// <summary>How long a request waits for a busy browser profile before it is answered with 409.</summary>
    public TimeSpan LockWait { get; }

    /// <summary>
    /// What is registered with Windows' HTTP server. IP literals only: http.sys then accepts a
    /// connection only on that address, whatever Host header it carries - so a tunnel's public host
    /// name is fine, and a forged Host header from elsewhere cannot get in.
    /// </summary>
    public IReadOnlyList<string> Prefixes => IsLoopbackOnly
        ? [$"http://127.0.0.1:{Port}/", $"http://[::1]:{Port}/"]
        : [$"http://{Literal(PrivateAddress!)}:{Port}/"];

    /// <summary>The address to call from this PC, for the log and for tests.</summary>
    public Uri LocalUrl => new(IsLoopbackOnly ? $"http://127.0.0.1:{Port}/" : $"http://{Literal(PrivateAddress!)}:{Port}/");

    public string Describe() => IsLoopbackOnly
        ? $"http://127.0.0.1:{Port} and http://[::1]:{Port} (loopback only)"
        : $"http://{Literal(PrivateAddress!)}:{Port} (private address; clients allowed: {string.Join(", ", AllowedClients)})";

    /// <summary>
    /// Whether a connection's source may be served. On loopback only loopback; on a private address
    /// only the listed clients. Decided by the connection itself - never by a forwarded header,
    /// which anyone can write.
    /// </summary>
    public bool IsClientAllowed(IPAddress remote)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IsLoopbackOnly) return IPAddress.IsLoopback(remote);
        return AllowedClients.Any(network => network.Contains(remote));
    }

    /// <summary>
    /// Reads the settings. Any problem is an error for the caller to surface: the message names the
    /// variable, never the key's value.
    /// </summary>
    /// <param name="localAddresses">This PC's addresses; the network adapters' by default.</param>
    public static RecordingApiOptions FromEnvironment(
        Func<string, string?>? readVariable = null, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;

        string? key = read(KeyVariable);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"{KeyVariable} is not set. The recording API will not run without a key; see RECORDING-API.md.");
        if (key.Trim().Length < MinimumKeyLength)
            throw new InvalidOperationException($"{KeyVariable} is too short. Use at least {MinimumKeyLength} random characters.");

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

        IPAddress? privateAddress = ParseHost(read(HostVariable), localAddresses ?? AdapterAddresses);
        IReadOnlyList<IPNetwork> allowed = [];
        if (privateAddress != null)
        {
            allowed = ParseAllowedClients(read(AllowedClientsVariable));
            if (allowed.Count == 0)
                throw new InvalidOperationException(
                    $"{HostVariable} is a network address, so {AllowedClientsVariable} must list who may connect " +
                    "(for example the reverse proxy's address, or 100.64.0.0/10 for a Tailscale tailnet).");
        }
        return new RecordingApiOptions(privateAddress, port, allowed, Hash(key.Trim()), lockWait);
    }

    /// <summary>
    /// The host setting: null for loopback, or the one private address to listen on. Everything
    /// that would listen more widely than that is refused with the reason.
    /// </summary>
    public static IPAddress? ParseHost(string? value, Func<IEnumerable<IPAddress>> localAddresses)
    {
        string host = (value ?? string.Empty).Trim();
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        if (host is "*" or "+" or "0.0.0.0" or "::" or "[::]")
            throw new InvalidOperationException(
                $"{HostVariable}={host} would listen on every address of this PC. That is refused: keep the default " +
                "127.0.0.1 and put a tunnel in front, or name one private address of this PC.");

        string literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (!IPAddress.TryParse(literal, out var address))
            throw new InvalidOperationException(
                $"{HostVariable} must be an IP address (or localhost). Host names are refused: they cannot say which network the API is on.");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return null;

        if (!IsPrivate(address))
            throw new InvalidOperationException(
                $"{HostVariable}={host} is not a private address. The API is never put on a public address; " +
                "use 127.0.0.1 with a tunnel instead.");
        if (!localAddresses().Any(local => (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).Equals(address)))
            throw new InvalidOperationException($"{HostVariable}={host} is not an address of this PC.");
        return address;
    }

    /// <summary>
    /// "10.0.0.5, 100.64.0.0/10; fd7a:115c:a1e0::/48" - addresses or ranges, separated by commas,
    /// semicolons or spaces. A range that is everyone (/0) is refused.
    /// </summary>
    public static IReadOnlyList<IPNetwork> ParseAllowedClients(string? value)
    {
        var networks = new List<IPNetwork>();
        foreach (string entry in (value ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            IPNetwork network;
            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out network))
                    throw new InvalidOperationException($"{AllowedClientsVariable}: '{entry}' is not an address or a range like 10.0.0.0/24.");
            }
            else if (IPAddress.TryParse(entry, out var single))
            {
                if (single.IsIPv4MappedToIPv6) single = single.MapToIPv4();
                network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            }
            else throw new InvalidOperationException($"{AllowedClientsVariable}: '{entry}' is not an address or a range like 10.0.0.0/24.");

            if (network.PrefixLength == 0)
                throw new InvalidOperationException($"{AllowedClientsVariable}: '{entry}' allows everyone, which is refused.");
            networks.Add(network);
        }
        return networks;
    }

    /// <summary>Private ranges only: RFC 1918, the shared range Tailscale uses, and IPv6 unique-local.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);   // 100.64.0.0/10, carrier-grade NAT / Tailscale
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;    // fc00::/7, unique local
        return false;
    }

    /// <summary>For tests: fixed settings without touching the environment.</summary>
    public static RecordingApiOptions ForTesting(int port, string key, TimeSpan? lockWait = null,
        IPAddress? privateAddress = null, IReadOnlyList<IPNetwork>? allowedClients = null) =>
        new(privateAddress, port, allowedClients ?? [], Hash(key), lockWait ?? TimeSpan.FromSeconds(5));

    /// <summary>Whether a presented key is the configured one. Constant time; nothing is logged.</summary>
    public bool KeyMatches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(presented.Trim()), _keyHash);
    }

    /// <summary>
    /// What to tell the person when Windows refuses to listen on a private address: a normal user may
    /// only listen on loopback until an administrator reserves the address once.
    /// </summary>
    public string ReservationAdvice() =>
        $"Windows needs a one-time reservation before a normal user may listen on {Literal(PrivateAddress!)}. " +
        $"In an administrator terminal: netsh http add urlacl url={Prefixes[0]} user=\"{Environment.UserDomainName}\\{Environment.UserName}\"";

    private static string Literal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static IEnumerable<IPAddress> AdapterAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address);

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
