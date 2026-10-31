using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SlopstackLabs.Slaw.Mcp;

namespace SlopstackLabs.Slaw.Surfaces;

/// <summary>
/// A WebSocket for deck plugins at <c>ws://127.0.0.1:port/surface</c>. Loopback only, and
/// browsers from anywhere else are refused, as with MCP. Built on a raw socket because
/// HttpListener's http.sys path on Windows is a permissions meeting nobody asked for.
/// </summary>
internal sealed class ControlSurfaceServer : IDisposable
{
    private const string WebSocketHandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaximumHandshakeBytes = 8 * 1024;
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumSurfaces = 16;
    private const int OutgoingMessageBacklog = 8;

    private readonly TcpListener tcpListener;
    private readonly Func<string, Task<string?>> requestHandler;
    private readonly string commandsMessage;
    private readonly Action? surfaceDisconnected;
    private readonly CancellationTokenSource serverLifetime = new();
    private readonly ConcurrentDictionary<Channel<string>, byte> connectedSurfaces = new();

    private volatile string? latestState;

    /// <param name="requestHandler">Handles one request and returns an error message, or null when it worked.</param>
    /// <param name="surfaceDisconnected">Called on a background thread after a surface leaves, so held notes can be released.</param>
    public ControlSurfaceServer(int port, string commandsMessage, Func<string, Task<string?>> requestHandler, Action? surfaceDisconnected = null)
    {
        this.surfaceDisconnected = surfaceDisconnected;
        this.commandsMessage = commandsMessage;
        this.requestHandler = requestHandler;
        tcpListener = new TcpListener(IPAddress.Loopback, port);
        Port = port;
    }

    public int Port { get; private set; }

    public string EndpointUrl => $"ws://127.0.0.1:{Port}/surface";

    public int ConnectedSurfaceCount => connectedSurfaces.Count;

    /// <summary>Starts listening. Throws <see cref="SocketException"/> if the port is taken.</summary>
    public void Start()
    {
        tcpListener.Start();
        Port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Sends a state to every surface, and to each new one when it connects. Unchanged states are skipped.</summary>
    public void PublishState(string stateMessage)
    {
        if (stateMessage == latestState)
        {
            return;
        }
        latestState = stateMessage;
        foreach (var surfaceOutbox in connectedSurfaces.Keys)
        {
            surfaceOutbox.Writer.TryWrite(stateMessage);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!serverLifetime.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await tcpListener.AcceptTcpClientAsync(serverLifetime.Token);
            }
            catch (Exception listenerRemediationOpportunity) when (listenerRemediationOpportunity is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => ServeConnectionAsync(tcpClient));
        }
    }

    private async Task ServeConnectionAsync(TcpClient tcpClient)
    {
        using (tcpClient)
        {
            try
            {
                var networkStream = tcpClient.GetStream();
                using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(serverLifetime.Token);
                handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var handshakeHeaders = await ReadHandshakeAsync(networkStream, handshakeDeadline.Token);
                if (handshakeHeaders is null)
                {
                    return;
                }
                if (!await TryAcceptHandshakeAsync(networkStream, handshakeHeaders, handshakeDeadline.Token))
                {
                    return;
                }
                using var webSocket = WebSocket.CreateFromStream(networkStream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                await ExchangeMessagesAsync(webSocket);
            }
            catch (Exception connectionRemediationOpportunity) when (connectionRemediationOpportunity is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The deck was unplugged, or its software left the meeting.
            }
        }
    }

    private async Task ExchangeMessagesAsync(WebSocket webSocket)
    {
        var surfaceOutbox = Channel.CreateBounded<string>(new BoundedChannelOptions(OutgoingMessageBacklog)
        {
            // A surface that stops reading only ever needs the newest state.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        surfaceOutbox.Writer.TryWrite(commandsMessage);
        connectedSurfaces[surfaceOutbox] = 0;
        if (latestState is { } stateOnArrival)
        {
            surfaceOutbox.Writer.TryWrite(stateOnArrival);
        }

        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(serverLifetime.Token);
        var sendingTask = SendLoopAsync(webSocket, surfaceOutbox, connectionLifetime.Token);
        try
        {
            await ReceiveLoopAsync(webSocket, surfaceOutbox, connectionLifetime.Token);
        }
        finally
        {
            connectedSurfaces.TryRemove(surfaceOutbox, out _);
            surfaceDisconnected?.Invoke();
            connectionLifetime.Cancel();
            try
            {
                await sendingTask;
            }
            catch (Exception sendRemediationOpportunity) when (sendRemediationOpportunity is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // Nobody is left to tell.
            }
        }
    }

    private static async Task SendLoopAsync(WebSocket webSocket, Channel<string> surfaceOutbox, CancellationToken cancellationToken)
    {
        await foreach (var outgoingMessage in surfaceOutbox.Reader.ReadAllAsync(cancellationToken))
        {
            await webSocket.SendAsync(Encoding.UTF8.GetBytes(outgoingMessage), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(WebSocket webSocket, Channel<string> surfaceOutbox, CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[MaximumRequestBytes];
        while (webSocket.State == WebSocketState.Open)
        {
            var filledBytes = 0;
            WebSocketReceiveResult receiveResult;
            do
            {
                if (filledBytes == receiveBuffer.Length)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "requests are small", cancellationToken);
                    return;
                }
                receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer, filledBytes, receiveBuffer.Length - filledBytes), cancellationToken);
                filledBytes += receiveResult.Count;
            }
            while (!receiveResult.EndOfMessage && receiveResult.MessageType != WebSocketMessageType.Close);

            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);
                return;
            }
            if (receiveResult.MessageType != WebSocketMessageType.Text)
            {
                surfaceOutbox.Writer.TryWrite(ControlSurfaceProtocol.DescribeError("requests must be text frames holding JSON"));
                continue;
            }
            if (await requestHandler(Encoding.UTF8.GetString(receiveBuffer, 0, filledBytes)) is { } errorMessage)
            {
                surfaceOutbox.Writer.TryWrite(ControlSurfaceProtocol.DescribeError(errorMessage));
            }
        }
    }

    private static async Task<Dictionary<string, string>?> ReadHandshakeAsync(NetworkStream networkStream, CancellationToken cancellationToken)
    {
        // One byte at a time: anything after the blank line already belongs to the WebSocket.
        var handshakeBytes = new List<byte>(1024);
        var singleByte = new byte[1];
        while (handshakeBytes.Count < MaximumHandshakeBytes)
        {
            if (await networkStream.ReadAsync(singleByte, cancellationToken) == 0)
            {
                return null;
            }
            handshakeBytes.Add(singleByte[0]);
            if (handshakeBytes.Count >= 4 && handshakeBytes[^4] == '\r' && handshakeBytes[^3] == '\n' && handshakeBytes[^2] == '\r' && handshakeBytes[^1] == '\n')
            {
                var handshakeLines = Encoding.ASCII.GetString(handshakeBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var handshakeHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [":request"] = handshakeLines[0] };
                foreach (var headerLine in handshakeLines.Skip(1))
                {
                    var separatorOffset = headerLine.IndexOf(':');
                    if (separatorOffset > 0)
                    {
                        handshakeHeaders[headerLine[..separatorOffset].Trim()] = headerLine[(separatorOffset + 1)..].Trim();
                    }
                }
                return handshakeHeaders;
            }
        }
        return null;
    }

    private async Task<bool> TryAcceptHandshakeAsync(NetworkStream networkStream, Dictionary<string, string> handshakeHeaders, CancellationToken cancellationToken)
    {
        var requestLineParts = handshakeHeaders[":request"].Split(' ');
        string? refusal = null;
        if (requestLineParts.Length < 2 || requestLineParts[0] != "GET" || requestLineParts[1].TrimEnd('/') != "/surface")
        {
            refusal = "404 Not Found";
        }
        else if (!McpHttpTransport.IsLocalOrigin(handshakeHeaders.GetValueOrDefault("Origin")))
        {
            refusal = "403 Forbidden";
        }
        else if (!handshakeHeaders.TryGetValue("Sec-WebSocket-Key", out _) ||
                 !handshakeHeaders.GetValueOrDefault("Upgrade", string.Empty).Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            refusal = "426 Upgrade Required";
        }
        else if (connectedSurfaces.Count >= MaximumSurfaces)
        {
            refusal = "503 Service Unavailable";
        }

        string handshakeResponse;
        if (refusal is not null)
        {
            handshakeResponse = $"HTTP/1.1 {refusal}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }
        else
        {
            var acceptToken = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(handshakeHeaders["Sec-WebSocket-Key"] + WebSocketHandshakeGuid)));
            handshakeResponse = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptToken}\r\n\r\n";
        }
        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(handshakeResponse), cancellationToken);
        return refusal is null;
    }

    public void Dispose()
    {
        serverLifetime.Cancel();
        tcpListener.Stop();
    }
}
