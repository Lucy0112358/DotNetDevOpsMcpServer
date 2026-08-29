using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerConfigureFirewallTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_configure_firewall";
    public string Description => "Configures or opens ports in Windows Firewall on a local or remote Windows server idempotently.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["host"] = new()
            {
                Type = "string",
                Description = "Target server hostname/IP (or 'localhost')."
            },
            ["username"] = new()
            {
                Type = "string",
                Description = "Remote Administrator username."
            },
            ["password"] = new()
            {
                Type = "string",
                Description = "Remote Administrator password."
            },
            ["ruleName"] = new()
            {
                Type = "string",
                Description = "Name of the firewall rule (e.g. 'Allow-Port-5000')."
            },
            ["port"] = new()
            {
                Type = "number",
                Description = "Port number to open/configure (e.g. 80, 443, 5000)."
            },
            ["protocolType"] = new()
            {
                Type = "string",
                Description = "Network protocol: 'TCP' or 'UDP' (default: TCP).",
                Enum = new List<string> { "TCP", "UDP" },
                Default = "TCP"
            },
            ["direction"] = new()
            {
                Type = "string",
                Description = "Traffic direction: 'Inbound' or 'Outbound' (default: Inbound).",
                Enum = new List<string> { "Inbound", "Outbound" },
                Default = "Inbound"
            },
            ["action"] = new()
            {
                Type = "string",
                Description = "Firewall action: 'Allow' or 'Block' (default: Allow).",
                Enum = new List<string> { "Allow", "Block" },
                Default = "Allow"
            },
            ["protocol"] = new()
            {
                Type = "string",
                Description = "Remote protocol: 'Auto', 'WinRM', 'SSH', or 'Local'.",
                Default = "Auto"
            }
        },
        Required = new List<string> { "host", "ruleName", "port" }
    };

    public ServerConfigureFirewallTool(RemoteExecutorFactory executorFactory)
    {
        _executorFactory = executorFactory;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var host = arguments.GetString("host", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var ruleName = arguments.GetString("ruleName")!;
        var port = arguments.GetInt("port");
        var protocolType = arguments.GetString("protocolType", "TCP")!;
        var direction = arguments.GetString("direction", "Inbound")!;
        var action = arguments.GetString("action", "Allow")!;
        var protocol = arguments.GetString("protocol", "Auto")!;

        if (!port.HasValue)
        {
            return McpCallToolResult.Error("'port' is required.");
        }

        var config = new RemoteConnectionConfig
        {
            Host = host,
            Username = username,
            Password = password,
            Protocol = protocol
        };

        try
        {
            var executor = _executorFactory.GetExecutor(config);

            var script = $@"
$ruleName = '{ruleName.Replace("'", "''")}'
$port = {port.Value}
$proto = '{protocolType.Replace("'", "''")}'
$dir = '{direction.Replace("'", "''")}'
$act = '{action.Replace("'", "''")}'

$existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if (-not $existing) {{
    New-NetFirewallRule -DisplayName $ruleName -Direction $dir -LocalPort $port -Protocol $proto -Action $act | Out-Null
    Write-Output ""Successfully created firewall rule '$ruleName' for $proto port $port ($dir, $act).""
}} else {{
    Set-NetFirewallRule -DisplayName $ruleName -Direction $dir -Action $act | Out-Null
    Write-Output ""Updated existing firewall rule '$ruleName' for $proto port $port.""
}}
";

            var executionResult = await executor.ExecutePowerShellScriptAsync(script, config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = executionResult.Success ? "success" : "failure",
                host = config.Host,
                ruleName,
                port = port.Value,
                protocol = protocolType,
                output = executionResult.StandardOutput,
                error = executionResult.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return executionResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Firewall configuration failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
