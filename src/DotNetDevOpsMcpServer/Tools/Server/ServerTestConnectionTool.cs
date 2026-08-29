using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerTestConnectionTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_test_connection";
    public string Description => "Tests remote management connectivity (WinRM / SSH / Local) to a Windows Server and reports operating system and environment details.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["host"] = new()
            {
                Type = "string",
                Description = "Hostname or IP address of the target Windows server (or 'localhost')."
            },
            ["username"] = new()
            {
                Type = "string",
                Description = "Administrator username for remote authentication."
            },
            ["password"] = new()
            {
                Type = "string",
                Description = "Password for remote authentication."
            },
            ["protocol"] = new()
            {
                Type = "string",
                Description = "Protocol to use: 'Auto', 'WinRM', 'SSH', or 'Local' (default: Auto).",
                Enum = new List<string> { "Auto", "WinRM", "SSH", "Local" },
                Default = "Auto"
            },
            ["port"] = new()
            {
                Type = "number",
                Description = "Custom port (e.g., 5985/5986 for WinRM, 22 for SSH)."
            },
            ["useSsl"] = new()
            {
                Type = "boolean",
                Description = "Whether to use HTTPS/SSL for WinRM (port 5986).",
                Default = false
            }
        },
        Required = new List<string> { "host" }
    };

    public ServerTestConnectionTool(RemoteExecutorFactory executorFactory)
    {
        _executorFactory = executorFactory;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var host = arguments.GetString("host", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var protocol = arguments.GetString("protocol", "Auto")!;
        var port = arguments.GetInt("port");
        var useSsl = arguments.GetBool("useSsl", false);

        var config = new RemoteConnectionConfig
        {
            Host = host,
            Username = username,
            Password = password,
            Protocol = protocol,
            Port = port,
            UseSsl = useSsl
        };

        try
        {
            var executor = _executorFactory.GetExecutor(config);
            var result = await executor.TestConnectivityAsync(config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = result.Success ? "connected" : "failed",
                protocolUsed = executor.ProtocolName,
                host = config.Host,
                output = result.StandardOutput,
                error = result.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return result.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Connection test exception: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
