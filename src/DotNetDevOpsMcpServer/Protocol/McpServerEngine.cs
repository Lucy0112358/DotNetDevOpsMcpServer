using System.Text.Json;
using DotNetDevOpsMcpServer.Tools;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Protocol;

public class McpServerEngine
{
    private readonly Dictionary<string, IDevOpsTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<McpServerEngine> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public McpServerEngine(IEnumerable<IDevOpsTool> tools, ILogger<McpServerEngine> logger)
    {
        _logger = logger;
        foreach (var tool in tools)
        {
            _tools[tool.Name] = tool;
            _logger.LogInformation("Registered MCP tool: {ToolName}", tool.Name);
        }
    }

    public async Task<JsonRpcResponse?> HandleMessageAsync(string messageJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
            return null;

        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(messageJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JSON-RPC request");
            return JsonRpcResponse.CreateError(null, -32700, "Parse error: " + ex.Message);
        }

        if (request == null)
            return null;

        return await ProcessRequestAsync(request, cancellationToken);
    }

    public async Task<JsonRpcResponse?> ProcessRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Received method: {Method}, ID: {Id}", request.Method, request.Id?.ToString());

        switch (request.Method)
        {
            case "initialize":
                var initResult = new McpInitializeResult
                {
                    ProtocolVersion = "2024-11-05",
                    ServerInfo = new Implementation
                    {
                        Name = "dotnet-devops-mcp",
                        Version = "1.0.0"
                    },
                    Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability { ListChanged = false },
                        Logging = new LoggingCapability()
                    },
                    Instructions = "MCP Server for .NET project build, DacFx DB-First schema compare & migration script generation, and remote deployment to IIS / Windows Services."
                };
                return JsonRpcResponse.CreateSuccess(request.Id, initResult);

            case "notifications/initialized":
            case "initialized":
                _logger.LogInformation("MCP client initialized successfully.");
                return null; // Notifications don't send responses

            case "ping":
                return JsonRpcResponse.CreateSuccess(request.Id, new { });

            case "tools/list":
                var toolsList = _tools.Values.Select(t => new McpTool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = t.InputSchema
                }).ToList();

                return JsonRpcResponse.CreateSuccess(request.Id, new McpListToolsResult { Tools = toolsList });

            case "tools/call":
                if (!request.Params.HasValue)
                {
                    return JsonRpcResponse.CreateError(request.Id, -32602, "Missing params for tools/call");
                }

                try
                {
                    var callRequest = JsonSerializer.Deserialize<McpCallToolRequest>(request.Params.Value.GetRawText(), JsonOptions);
                    if (callRequest == null || string.IsNullOrWhiteSpace(callRequest.Name))
                    {
                        return JsonRpcResponse.CreateError(request.Id, -32602, "Tool name is required");
                    }

                    if (!_tools.TryGetValue(callRequest.Name, out var tool))
                    {
                        return JsonRpcResponse.CreateError(request.Id, -32601, $"Tool '{callRequest.Name}' not found.");
                    }

                    _logger.LogInformation("Executing tool '{ToolName}'...", callRequest.Name);
                    var toolResult = await tool.ExecuteAsync(callRequest.Arguments, cancellationToken);
                    return JsonRpcResponse.CreateSuccess(request.Id, toolResult);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while invoking tool in tools/call");
                    return JsonRpcResponse.CreateSuccess(request.Id, McpCallToolResult.Error($"Tool execution failed: {ex.Message}\n{ex.StackTrace}"));
                }

            default:
                _logger.LogWarning("Unknown method: {Method}", request.Method);
                return JsonRpcResponse.CreateError(request.Id, -32601, $"Method '{request.Method}' not found.");
        }
    }
}
