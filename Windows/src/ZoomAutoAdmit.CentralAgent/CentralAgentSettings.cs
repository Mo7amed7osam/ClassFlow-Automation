using System.Net;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>
/// How the agent reaches the central backend. The agent only ever connects out: nothing here opens
/// a port on this PC.
/// </summary>
public sealed record CentralAgentSettings
{
    /// <summary>The backend's base address, e.g. https://central.example.com/.</summary>
    public required Uri BackendUrl { get; init; }
    public required string Version { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = DefaultCapabilities;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// With no message at all from the backend for this long (it answers every heartbeat), the
    /// connection is taken for dead - after sleep, a network change or a silent drop - and replaced.
    /// </summary>
    public TimeSpan ServerSilenceTimeout { get; init; } = TimeSpan.FromSeconds(75);
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumBackoff { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>± this fraction of every delay is random, so many PCs never reconnect in step.</summary>
    public double BackoffJitter { get; init; } = 0.2;

    /// <summary>The job types this agent runs, as capabilities: the recording step and the LMS it drives.</summary>
    public static readonly IReadOnlyList<string> DefaultCapabilities = ["recording_processing", "lms"];

    public Uri WebSocketUri => new UriBuilder(BackendUrl)
    {
        Scheme = BackendUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        Path = BackendUrl.AbsolutePath.TrimEnd('/') + "/ws/agent",
    }.Uri;

    public Uri RegisterUri => new(BackendUrl, "api/v1/agents/register");

    /// <summary>
    /// The backend address, checked: absolute, https - plain http only for this PC itself, for local
    /// development - with no user name, password, query or fragment. Ends with '/'.
    /// </summary>
    public static Uri ParseBackendUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url))
            throw new ArgumentException("The backend address must be an absolute URL, like https://central.example.com.");
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new ArgumentException("The backend address must not contain a user name, password, query or fragment.");
        bool loopback = url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && loopback))
            throw new ArgumentException("The backend address must use https:// (plain http:// is only allowed for a backend on this PC).");
        string path = url.AbsolutePath.EndsWith('/') ? url.AbsolutePath : url.AbsolutePath + "/";
        return new UriBuilder(url) { Path = path }.Uri;
    }
}
