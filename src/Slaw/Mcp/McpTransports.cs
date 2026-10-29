using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// MCP over stdio: one JSON-RPC message per line on stdin, one per line on
/// stdout. Nothing else may touch stdout while this runs — slaw's banners and
/// spinners already live on stderr, so for once the architecture was ready.
/// </summary>
internal static class McpStdioTransport
{
    public static async Task RunAsync(McpProtocolDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var utf8WithoutPreamble = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var standardInput = new StreamReader(Console.OpenStandardInput(), utf8WithoutPreamble);
        await using var standardOutput = new StreamWriter(ProtocolStandardOutput.Claim(), utf8WithoutPreamble) { AutoFlush = true, NewLine = "\n" };

        while (!cancellationToken.IsCancellationRequested && await standardInput.ReadLineAsync(cancellationToken) is { } incomingLine)
        {
            if (string.IsNullOrWhiteSpace(incomingLine))
            {
                continue;
            }
            if (await dispatcher.HandleAsync(incomingLine, cancellationToken) is { } outgoingMessage)
            {
                await standardOutput.WriteLineAsync(outgoingMessage);
            }
        }
    }
}

/// <summary>
/// MCP's Streamable HTTP transport, minimally: POST a JSON-RPC message to
/// <c>/mcp</c>, receive the JSON response. Bound to 127.0.0.1 only, and requests
/// carrying a non-local Origin are refused, so a web page cannot quietly
/// remix your song (the spec's DNS-rebinding warning, taken personally).
/// </summary>
internal sealed class McpHttpTransport : IDisposable
{
    private const string SessionHeaderName = "Mcp-Session-Id";

    private readonly HttpListener httpListener = new();
    private readonly Func<McpProtocolDispatcher> dispatcherFactory;
    private readonly ConcurrentDictionary<string, McpProtocolDispatcher> dispatchersBySession = new();
    private readonly CancellationTokenSource listenerLifetime = new();
    private readonly McpProtocolDispatcher sessionlessDispatcher;

    public McpHttpTransport(int port, Func<McpProtocolDispatcher> dispatcherFactory)
    {
        Port = port;
        this.dispatcherFactory = dispatcherFactory;
        sessionlessDispatcher = dispatcherFactory();
        httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int Port { get; }

    public string EndpointUrl => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> if the port is taken.</summary>
    public void Start()
    {
        httpListener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!listenerLifetime.IsCancellationRequested)
        {
            HttpListenerContext listenerContext;
            try
            {
                listenerContext = await httpListener.GetContextAsync();
            }
            catch (Exception listenerRemediationOpportunity) when (listenerRemediationOpportunity is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleRequestAsync(listenerContext));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext listenerContext)
    {
        var httpRequest = listenerContext.Request;
        var httpResponse = listenerContext.Response;
        try
        {
            if (!IsLocalOrigin(httpRequest.Headers["Origin"]))
            {
                await RespondAsync(httpResponse, HttpStatusCode.Forbidden, "non-local origins may not touch the mix");
                return;
            }
            if (httpRequest.Url?.AbsolutePath.TrimEnd('/') != "/mcp")
            {
                await RespondAsync(httpResponse, HttpStatusCode.NotFound, "slaw's MCP endpoint lives at /mcp");
                return;
            }
            switch (httpRequest.HttpMethod)
            {
                case "POST":
                    break;
                case "DELETE":
                    if (httpRequest.Headers[SessionHeaderName] is { } endedSession)
                    {
                        dispatchersBySession.TryRemove(endedSession, out _);
                    }
                    await RespondAsync(httpResponse, HttpStatusCode.OK, string.Empty);
                    return;
                default:
                    // No server-initiated stream on offer: slaw only speaks when spoken to (unlike its co-pilot).
                    httpResponse.Headers["Allow"] = "POST, DELETE";
                    await RespondAsync(httpResponse, HttpStatusCode.MethodNotAllowed, string.Empty);
                    return;
            }

            string requestBody;
            using (var bodyReader = new StreamReader(httpRequest.InputStream, Encoding.UTF8))
            {
                requestBody = await bodyReader.ReadToEndAsync();
            }

            var sessionIdentifier = httpRequest.Headers[SessionHeaderName];
            McpProtocolDispatcher sessionDispatcher;
            if (requestBody.Contains("\"initialize\"", StringComparison.Ordinal))
            {
                sessionIdentifier = Guid.NewGuid().ToString("N");
                sessionDispatcher = dispatcherFactory();
                dispatchersBySession[sessionIdentifier] = sessionDispatcher;
            }
            else if (sessionIdentifier is null || !dispatchersBySession.TryGetValue(sessionIdentifier, out sessionDispatcher!))
            {
                sessionDispatcher = sessionlessDispatcher;
            }

            var responseBody = await sessionDispatcher.HandleAsync(requestBody, listenerLifetime.Token);
            if (sessionIdentifier is not null)
            {
                httpResponse.Headers[SessionHeaderName] = sessionIdentifier;
            }
            if (responseBody is null)
            {
                await RespondAsync(httpResponse, HttpStatusCode.Accepted, string.Empty);
                return;
            }
            httpResponse.ContentType = "application/json";
            await RespondAsync(httpResponse, HttpStatusCode.OK, responseBody);
        }
        catch (Exception requestRemediationOpportunity) when (requestRemediationOpportunity is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client hung up. Its loss.
        }
    }

    private static bool IsLocalOrigin(string? originHeader)
    {
        if (string.IsNullOrEmpty(originHeader))
        {
            return true;
        }
        return Uri.TryCreate(originHeader, UriKind.Absolute, out var originUri) &&
               (originUri.IsLoopback || string.Equals(originUri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RespondAsync(HttpListenerResponse httpResponse, HttpStatusCode statusCode, string responseBody)
    {
        httpResponse.StatusCode = (int)statusCode;
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        httpResponse.ContentLength64 = responseBytes.Length;
        if (responseBytes.Length > 0)
        {
            await httpResponse.OutputStream.WriteAsync(responseBytes);
        }
        httpResponse.Close();
    }

    public void Dispose()
    {
        listenerLifetime.Cancel();
        try
        {
            httpListener.Stop();
            httpListener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already gone; the stakeholders have left the call.
        }
    }
}
