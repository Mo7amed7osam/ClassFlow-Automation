using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace ZoomAutoAdmit.CentralAgent;

/// <summary>One text message per frame, in and out.</summary>
public interface IAgentSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken);
    /// <summary>The next text message, or null once the backend closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
    /// <summary>How the backend closed it, when it did.</summary>
    int? CloseStatus { get; }
}

public interface IAgentSocketFactory
{
    /// <summary>Connects with the device token. <see cref="AgentUnauthorizedException"/> if it is refused.</summary>
    Task<IAgentSocket> ConnectAsync(Uri uri, string deviceToken, CancellationToken cancellationToken);
}

/// <summary>The backend refused this device (revoked, re-registered elsewhere, or a wrong token).</summary>
public sealed class AgentUnauthorizedException(string message) : Exception(message);

/// <summary>The real connection: <see cref="ClientWebSocket"/>, outbound, with the token in a header.</summary>
public sealed class ClientWebSocketFactory : IAgentSocketFactory
{
    public const int MaximumMessageBytes = 64 * 1024;

    public async Task<IAgentSocket> ConnectAsync(Uri uri, string deviceToken, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        // In a header, never in the URL, so it cannot end up in a proxy's access log.
        socket.Options.SetRequestHeader("Authorization", $"Bearer {deviceToken}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return new Connection(socket);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            socket.Dispose();
            throw new AgentUnauthorizedException($"The backend refused this device ({(int)socket.HttpStatusCode}).");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Connection(ClientWebSocket socket) : IAgentSocket
    {
        private readonly byte[] _buffer = new byte[8 * 1024];

        public int? CloseStatus => socket.CloseStatus is { } status ? (int)status : null;

        public Task SendAsync(string text, CancellationToken cancellationToken) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(_buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return null;
                }
                message.Write(_buffer, 0, result.Count);
                if (message.Length > MaximumMessageBytes)
                    throw new InvalidDataException("The backend sent a message larger than 64 KB.");
                if (!result.EndOfMessage) continue;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    message.SetLength(0);
                    continue;
                }
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", timeout.Token);
                }
            }
            catch (Exception) { /* the connection is going away either way */ }
            socket.Dispose();
        }
    }
}
