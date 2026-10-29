using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// The JSON-RPC 2.0 core of slaw's Model Context Protocol server, shared by the
/// stdio and HTTP transports. Implements initialize, ping, tools/list and
/// tools/call; everything else is politely declined. Hand-rolled, because an
/// SDK would imply someone else wrote the protocol layer on purpose.
/// </summary>
internal sealed class McpProtocolDispatcher
{
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = new[] { "2025-06-18", "2025-03-26", "2024-11-05" };

    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InvalidRequest = -32600;
    private const int ParseError = -32700;

    private readonly SlawMcpToolset toolset;

    public McpProtocolDispatcher(SlawMcpToolset toolset)
    {
        this.toolset = toolset;
    }

    /// <summary>The name the connected client gave in initialize, for attribution in the studio.</summary>
    public string ClientName { get; private set; } = "an MCP client";

    /// <summary>
    /// Handles one raw message (a request, a notification, or a batch). Returns
    /// the serialized response, or null when nothing should be sent back.
    /// </summary>
    public async Task<string?> HandleAsync(string rawMessage, CancellationToken cancellationToken)
    {
        JsonNode? parsedMessage;
        try
        {
            parsedMessage = JsonNode.Parse(rawMessage);
        }
        catch (JsonException parseRemediationOpportunity)
        {
            return ErrorResponse(null, ParseError, $"parse error: {parseRemediationOpportunity.Message}").ToJsonString();
        }

        if (parsedMessage is JsonArray batchedMessages)
        {
            var batchedResponses = new JsonArray();
            foreach (var batchedMessage in batchedMessages)
            {
                if (await HandleMessageAsync(batchedMessage as JsonObject, cancellationToken) is { } batchedResponse)
                {
                    batchedResponses.Add(batchedResponse);
                }
            }
            return batchedResponses.Count == 0 ? null : batchedResponses.ToJsonString();
        }
        return (await HandleMessageAsync(parsedMessage as JsonObject, cancellationToken))?.ToJsonString();
    }

    private async Task<JsonObject?> HandleMessageAsync(JsonObject? requestMessage, CancellationToken cancellationToken)
    {
        if (requestMessage is null)
        {
            return ErrorResponse(null, InvalidRequest, "a JSON-RPC message must be an object");
        }
        var requestIdentifier = requestMessage["id"]?.DeepClone();
        var requestedMethod = requestMessage["method"]?.GetValue<string>();
        var isNotification = requestIdentifier is null;

        if (requestedMethod is null)
        {
            // A response from the client to something we never asked. Ignore it.
            return null;
        }
        if (isNotification)
        {
            // notifications/initialized, notifications/cancelled, …: nothing to answer.
            return null;
        }

        try
        {
            JsonNode responseResult = requestedMethod switch
            {
                "initialize" => Initialize(requestMessage["params"] as JsonObject),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = toolset.DescribeTools() },
                "tools/call" => await CallToolAsync(requestMessage["params"] as JsonObject, cancellationToken),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => throw new McpMethodNotFoundException(requestedMethod),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestIdentifier, ["result"] = responseResult };
        }
        catch (McpMethodNotFoundException)
        {
            return ErrorResponse(requestIdentifier, MethodNotFound, $"method not found: {requestedMethod}");
        }
        catch (McpInvalidParamsException invalidParamsRemediationOpportunity)
        {
            return ErrorResponse(requestIdentifier, InvalidParams, invalidParamsRemediationOpportunity.Message);
        }
    }

    private JsonObject Initialize(JsonObject? initializeParameters)
    {
        var requestedVersion = initializeParameters?["protocolVersion"]?.GetValue<string>();
        ClientName = initializeParameters?["clientInfo"]?["name"]?.GetValue<string>() ?? ClientName;
        return new JsonObject
        {
            ["protocolVersion"] = requestedVersion is not null && SupportedProtocolVersions.Contains(requestedVersion)
                ? requestedVersion
                : SupportedProtocolVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "slaw",
                ["title"] = "slaw — beyond deterministic production",
                ["version"] = typeof(McpProtocolDispatcher).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
            ["instructions"] = SlawMcpToolset.ServerInstructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? callParameters, CancellationToken cancellationToken)
    {
        var toolName = callParameters?["name"]?.GetValue<string>()
            ?? throw new McpInvalidParamsException("tools/call needs a tool name");
        var toolArguments = callParameters["arguments"] as JsonObject ?? new JsonObject();
        if (!toolset.HasTool(toolName))
        {
            throw new McpInvalidParamsException($"unknown tool: {toolName}");
        }
        try
        {
            var toolText = await toolset.InvokeAsync(toolName, toolArguments, ClientName, cancellationToken);
            return ToolResult(toolText, isError: false);
        }
        catch (Exception toolRemediationOpportunity) when (toolRemediationOpportunity is not OperationCanceledException)
        {
            // Tool failures are results the model should read, not protocol errors.
            return ToolResult(toolRemediationOpportunity.Message, isError: true);
        }
    }

    private static JsonObject ToolResult(string toolText, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = toolText }),
        ["isError"] = isError,
    };

    private static JsonObject ErrorResponse(JsonNode? requestIdentifier, int errorCode, string errorMessage) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = requestIdentifier,
        ["error"] = new JsonObject { ["code"] = errorCode, ["message"] = errorMessage },
    };

    private sealed class McpMethodNotFoundException : Exception
    {
        public McpMethodNotFoundException(string methodName)
            : base(methodName)
        {
        }
    }
}

/// <summary>Raised by tools for malformed arguments; reported to the model as a tool error.</summary>
internal sealed class McpInvalidParamsException : Exception
{
    public McpInvalidParamsException(string misalignmentDescription)
        : base(misalignmentDescription)
    {
    }
}
