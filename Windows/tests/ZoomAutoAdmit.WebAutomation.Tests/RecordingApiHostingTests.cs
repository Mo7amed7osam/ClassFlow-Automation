using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZoomAutoAdmit.WebAutomation.Api;
using ZoomAutoAdmit.WebAutomation.Recordings;
using Xunit;

namespace ZoomAutoAdmit.WebAutomation.Tests;

/// <summary>Where the API listens, who may connect, and how it behaves behind a tunnel.</summary>
public sealed class RecordingApiHostingTests
{
    private const string Key = "a-long-enough-random-key-0001";
    private static readonly IPAddress[] ThisPc =
        [IPAddress.Loopback, IPAddress.Parse("10.0.0.5"), IPAddress.Parse("100.101.102.103"), IPAddress.Parse("fd7a:115c:a1e0::1")];

    private static RecordingApiOptions Options(string? host = null, string? allowed = null, string? port = null) =>
        RecordingApiOptions.FromEnvironment(
            name => name switch
            {
                RecordingApiOptions.KeyVariable => Key,
                RecordingApiOptions.HostVariable => host,
                RecordingApiOptions.AllowedClientsVariable => allowed,
                RecordingApiOptions.PortVariable => port,
                _ => null,
            },
            () => ThisPc);

    // ------------------------------------------------------------------------------ the default

    [Fact]
    public void ByDefaultItListensOnLoopbackAndNothingElse()
    {
        var options = Options();
        Assert.True(options.IsLoopbackOnly);
        Assert.Equal(RecordingApiOptions.DefaultPort, options.Port);
        Assert.Equal(["http://127.0.0.1:47821/", "http://[::1]:47821/"], options.Prefixes);
        Assert.Equal(new Uri("http://127.0.0.1:47821/"), options.LocalUrl);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("   ")]
    public void EveryWayOfSayingLoopbackIsLoopback(string host)
    {
        var options = Options(host);
        Assert.True(options.IsLoopbackOnly);
        Assert.Equal(["http://127.0.0.1:47821/", "http://[::1]:47821/"], options.Prefixes);
    }

    [Fact]
    public void ThePortIsConfigurable()
    {
        Assert.Equal(["http://127.0.0.1:50123/", "http://[::1]:50123/"], Options(port: "50123").Prefixes);
        Assert.Throws<InvalidOperationException>(() => Options(port: "443"));
    }

    // ------------------------------------------------------------------------------ what is refused

    [Theory]
    [InlineData("0.0.0.0", "every address")]
    [InlineData("::", "every address")]
    [InlineData("[::]", "every address")]
    [InlineData("*", "every address")]
    [InlineData("+", "every address")]
    [InlineData("recordings.example.com", "Host names are refused")]
    [InlineData("8.8.8.8", "not a private address")]
    [InlineData("2001:4860:4860::8888", "not a private address")]
    [InlineData("192.168.50.50", "not an address of this PC")]
    public void AnythingWiderThanOnePrivateAddressIsRefused(string host, string reason)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options(host, allowed: "10.0.0.0/24"));
        Assert.Contains(reason, ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public void APrivateAddressIsRefusedUntilItIsSaidWhoMayConnect()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options("10.0.0.5"));
        Assert.Contains(RecordingApiOptions.AllowedClientsVariable, ex.Message);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.0/24, 0.0.0.0/0")]
    public void AnAllowListThatIsEveryoneIsRefused(string allowed)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Options("10.0.0.5", allowed));
        Assert.Contains("allows everyone", ex.Message);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/99")]
    public void AGarbledAllowListIsRefused(string allowed) =>
        Assert.Throws<InvalidOperationException>(() => Options("10.0.0.5", allowed));

    // ------------------------------------------------------------------------------ a private address

    [Fact]
    public void OnePrivateAddressCanBeChosenWithTheClientsAllowedToUseIt()
    {
        var options = Options("100.101.102.103", "100.64.0.0/10; 10.0.0.7");
        Assert.False(options.IsLoopbackOnly);
        Assert.Equal(["http://100.101.102.103:47821/"], options.Prefixes);
        Assert.Equal(2, options.AllowedClients.Count);
        Assert.Contains("100.64.0.0/10", options.Describe());
    }

    [Fact]
    public void AnIpv6UniqueLocalAddressWorksToo()
    {
        var options = Options("fd7a:115c:a1e0::1", "fd7a:115c:a1e0::/48");
        Assert.Equal(["http://[fd7a:115c:a1e0::1]:47821/"], options.Prefixes);
    }

    [Fact]
    public void TheReservationAdviceIsTheExactCommandAndCarriesNoSecret()
    {
        string advice = Options("10.0.0.5", "10.0.0.7").ReservationAdvice();
        Assert.Contains("netsh http add urlacl url=http://10.0.0.5:47821/", advice);
        Assert.DoesNotContain(Key, advice);
    }

    // ------------------------------------------------------------------------------ who may connect

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.9", false)]
    [InlineData("100.101.102.104", false)]
    public void OnLoopbackOnlyThisPcIsServed(string client, bool allowed) =>
        Assert.Equal(allowed, Options().IsClientAllowed(IPAddress.Parse(client)));

    [Theory]
    [InlineData("100.100.1.1", true)]      // inside 100.64.0.0/10
    [InlineData("10.0.0.7", true)]         // listed on its own
    [InlineData("10.0.0.8", false)]
    [InlineData("127.0.0.1", false)]       // not listed: loopback cannot reach this address anyway
    [InlineData("::ffff:10.0.0.7", true)]
    public void OnAPrivateAddressOnlyTheListedClientsAreServed(string client, bool allowed) =>
        Assert.Equal(allowed, Options("100.101.102.103", "100.64.0.0/10, 10.0.0.7").IsClientAllowed(IPAddress.Parse(client)));

    // ------------------------------------------------------------------------------ logging behind a tunnel

    [Theory]
    [InlineData("203.0.113.7", " (from 127.0.0.1 via 203.0.113.7)")]
    [InlineData("203.0.113.7, 10.0.0.1", " (from 127.0.0.1 via 203.0.113.7)")]
    [InlineData(null, " (from 127.0.0.1)")]
    [InlineData("127.0.0.1", " (from 127.0.0.1)")]
    public void TheForwardedAddressIsLoggedButOnlyAsAnAddress(string? forwarded, string expected) =>
        Assert.Equal(expected, RecordingApiServer.Origin(IPAddress.Loopback, forwarded));

    [Theory]
    [InlineData("http", null, true)]
    [InlineData("HTTP", null, true)]
    [InlineData("http, https", null, true)]      // the first entry is what the caller used
    [InlineData("https", null, false)]
    [InlineData("https, http", null, false)]
    [InlineData(null, "for=203.0.113.7;proto=http", true)]
    [InlineData(null, "for=203.0.113.7; proto=\"http\"", true)]
    [InlineData(null, "for=203.0.113.7;proto=https, for=10.0.0.1;proto=http", false)]
    [InlineData(null, null, false)]              // straight from this PC
    public void PlainHttpBehindAProxyIsRecognised(string? forwardedProto, string? forwarded, bool plain) =>
        Assert.Equal(plain, RecordingApiServer.ArrivedOverPlainHttp(forwardedProto, forwarded));

    [Fact]
    public void AForwardedHeaderCannotWriteIntoTheLog()
    {
        string origin = RecordingApiServer.Origin(IPAddress.Loopback, "1.2.3.4\r\n[API] POST /forged -> 200 X-API-Key: secret");
        Assert.DoesNotContain('\n', origin);
        Assert.DoesNotContain('\r', origin);
        Assert.DoesNotContain("[API]", origin);
        Assert.DoesNotContain("secret", origin);
        Assert.DoesNotContain(" ", origin.Trim().Replace("(from ", "").Replace(" via ", ""));
    }
}

/// <summary>
/// The real server on loopback, reached the way a tunnel connector on this PC reaches it: from
/// 127.0.0.1 or [::1], carrying the tunnel's public host name and forwarding headers.
/// </summary>
public sealed class RecordingApiBehindATunnelTests : IAsyncLifetime
{
    private const string Key = "tunnel-test-key-0123456789-abcd";
    private const string DriveLink = "https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz012345/view?usp=sharing";
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly Attached _processor = new();
    private RecordingApiServer _server = null!;
    private int _port;

    private sealed class Attached : IRecordingLinkProcessor
    {
        public readonly ConcurrentQueue<ProvidedRecordLinkRequest> Given = new();
        public Task<RecordingLinkOutcome> ProcessAsync(RecordingLinkRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the API must not search Zoom");
        public Task<RecordingLinkOutcome> AttachProvidedLinkAsync(ProvidedRecordLinkRequest request, CancellationToken cancellationToken)
        {
            Given.Enqueue(request);
            return Task.FromResult(new RecordingLinkOutcome(RecordingLinkStatus.Attached, "saved")
            {
                Group = request.Group,
                Date = request.Date ?? new DateOnly(2026, 9, 3),
                Profile = RecordingLinkProcessor.DashboardProfile,
            });
        }
    }

    public Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _server = new RecordingApiServer(RecordingApiOptions.ForTesting(_port, Key), _processor, _logs.Enqueue);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>One raw HTTP/1.1 exchange, so the Host and forwarding headers are exactly a tunnel's.</summary>
    private async Task<(int Status, string Headers, string Body)> SendAsync(IPAddress address, string method, string path,
        string host, string? body = null, params string[] extraHeaders)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, _port);
        using var stream = client.GetStream();
        byte[] payload = Encoding.UTF8.GetBytes(body ?? string.Empty);
        var request = new StringBuilder()
            .Append($"{method} {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n");
        foreach (string header in extraHeaders) request.Append(header).Append("\r\n");
        if (body != null) request.Append($"Content-Type: application/json\r\nContent-Length: {payload.Length}\r\n");
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));
        if (body != null) await stream.WriteAsync(payload);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string response = await reader.ReadToEndAsync();
        int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = response[..split];
        return (int.Parse(head.Split(' ')[1]), head, response[(split + 4)..]);
    }

    private static readonly string[] TunnelHeaders =
        ["X-Forwarded-For: 203.0.113.7", "X-Forwarded-Proto: https", "CF-Connecting-IP: 203.0.113.7"];

    [Fact]
    public async Task HealthAnswersThroughATunnelWithItsPublicHostName()
    {
        var (status, _, body) = await SendAsync(IPAddress.Loopback, "GET", "/health", "recordings.example.com", null, TunnelHeaders);
        Assert.Equal(200, status);
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task TheRecordingEndpointWorksThroughATunnelAndStillNeedsTheKey()
    {
        string body = $$"""{"group":"AST5_DAT1_S1","recordLink":"{{DriveLink}}","date":"2026-09-03"}""";

        var (refused, _, _) = await SendAsync(IPAddress.Loopback, "POST", RecordingApiServer.ProcessPath, "recordings.example.com", body, TunnelHeaders);
        Assert.Equal(401, refused);
        Assert.Empty(_processor.Given);

        var (status, _, answer) = await SendAsync(IPAddress.Loopback, "POST", RecordingApiServer.ProcessPath, "recordings.example.com", body,
            [.. TunnelHeaders, $"X-API-Key: {Key}"]);
        Assert.Equal(200, status);
        Assert.Contains("\"success\":true", answer);
        Assert.Equal(DriveLink, Assert.Single(_processor.Given).RecordLink);
    }

    [Fact]
    public async Task ACallerThatReachedTheTunnelOverPlainHttpIsRefusedBeforeTheKeyIsLookedAt()
    {
        string body = $$"""{"group":"AST5_DAT1_S1","recordLink":"{{DriveLink}}"}""";
        var (status, _, answer) = await SendAsync(IPAddress.Loopback, "POST", RecordingApiServer.ProcessPath, "recordings.example.com", body,
            "X-Forwarded-Proto: http", $"X-API-Key: {Key}");
        Assert.Equal(403, status);
        Assert.Contains("HTTPS required", answer);
        Assert.Empty(_processor.Given);

        // Logged like every other answer, so a proxy set up without HTTPS is visible on the PC.
        await WaitForAsync(() => _logs.Any(line => line.Contains("POST /api/recordings/process -> 403")));
    }

    private async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 50 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Log:\n" + string.Join("\n", _logs));
    }

    [Fact]
    public async Task TheIpv6LoopbackIsServedToo()
    {
        // Node (and so n8n) may resolve "localhost" to ::1 first.
        var (status, _, _) = await SendAsync(IPAddress.IPv6Loopback, "GET", "/health", $"localhost:{_port}");
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task NoCorsIsOfferedAndContentIsNotSniffed()
    {
        var (health, headers, _) = await SendAsync(IPAddress.Loopback, "GET", "/health", "recordings.example.com", null,
            "Origin: https://evil.example");
        Assert.Equal(200, health);
        Assert.DoesNotContain("Access-Control-Allow-Origin", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Content-Type-Options: nosniff", headers, StringComparison.OrdinalIgnoreCase);

        var (preflight, preflightHeaders, _) = await SendAsync(IPAddress.Loopback, "OPTIONS", RecordingApiServer.ProcessPath, "recordings.example.com", null,
            "Origin: https://evil.example", "Access-Control-Request-Method: POST");
        Assert.Equal(405, preflight);
        Assert.DoesNotContain("Access-Control-Allow", preflightHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheLogRecordsTheForwardedClientButNoSecretOrFullLink()
    {
        string body = $$"""{"group":"AST5_DAT1_S1","recordLink":"{{DriveLink}}"}""";
        await SendAsync(IPAddress.Loopback, "POST", RecordingApiServer.ProcessPath, "recordings.example.com", body,
            [.. TunnelHeaders, $"X-API-Key: {Key}"]);
        await SendAsync(IPAddress.Loopback, "POST", RecordingApiServer.ProcessPath, "recordings.example.com", body,
            [.. TunnelHeaders, "X-API-Key: wrong-wrong-wrong-wrong-wrong"]);
        string log = string.Join("\n", _logs);

        Assert.Contains("(from 127.0.0.1 via 203.0.113.7)", log);
        Assert.Contains("loopback only", log);
        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain("wrong-wrong", log);
        Assert.DoesNotContain("1AbCdEfGhIjKlMnOpQrStUvWxYz012345", log);
    }
}
