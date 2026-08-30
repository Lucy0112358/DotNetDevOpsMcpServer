using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerUndeployWindowsServiceTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_undeploy_windows_service";

    public string Description =>
        "Stops, unregisters, and removes a Windows Service from a local or remote Windows server, with options to delete service binaries.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["host"] = new() { Type = "string", Description = "Target server hostname or IP. Defaults to 'localhost'." },
            ["username"] = new() { Type = "string", Description = "Username for remote credentials (WinRM/SSH)." },
            ["password"] = new() { Type = "string", Description = "Password for remote credentials." },
            ["serviceName"] = new() { Type = "string", Description = "The name of the Windows Service to remove." },
            ["deleteFiles"] = new() { Type = "boolean", Description = "Whether to delete the service's directory and binaries on disk. Defaults to false." },
            ["protocol"] = new() { Type = "string", Description = "Remote protocol: 'Auto', 'Local', 'WinRM', or 'SSH'. Defaults to 'Auto'." }
        },
        Required = new List<string> { "serviceName" }
    };

    public ServerUndeployWindowsServiceTool(RemoteExecutorFactory executorFactory)
    {
        _executorFactory = executorFactory;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var host = arguments.GetString("host", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var serviceName = arguments.GetString("serviceName")!;
        var deleteFiles = arguments.GetBool("deleteFiles", false);
        var protocol = arguments.GetString("protocol", "Auto")!;

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

            var undeployScript = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{serviceName.Replace("'", "''")}'
$deleteFiles = ${(deleteFiles ? "$true" : "$false")}

Write-Output ""[1/3] Stopping service $serviceName if running...""
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$binaryDir = """"

if ($svc) {{
    if ($svc.Status -eq 'Running') {{
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        Write-Output ""Service $serviceName stopped.""
    }}

    try {{
        $wmiSvc = Get-CimInstance Win32_Service -Filter ""Name='$serviceName'"" -ErrorAction SilentlyContinue
        if ($wmiSvc -and $wmiSvc.PathName) {{
            $rawPath = $wmiSvc.PathName.Trim('""').Trim()
            $binaryDir = Split-Path $rawPath -Parent
        }}
    }} catch {{}}

    Write-Output ""[2/3] Deleting service $serviceName...""
    & sc.exe delete ""$serviceName""
    Write-Output ""Service $serviceName unregistered successfully.""
}} else {{
    Write-Output ""[1/3] Service $serviceName does not exist.""
}}

if ($deleteFiles -and $binaryDir -and (Test-Path $binaryDir)) {{
    Write-Output ""[3/3] Deleting service files at $binaryDir...""
    Remove-Item -Path $binaryDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output ""Deleted service files at $binaryDir.""
}} else {{
    Write-Output ""[3/3] Service files preserved.""
}}

Write-Output ""Windows Service undeployment completed for $serviceName.""
";

            var executionResult = await executor.ExecutePowerShellScriptAsync(undeployScript, config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = executionResult.Success ? "success" : "failure",
                host = config.Host,
                serviceName,
                logs = executionResult.StandardOutput,
                errors = executionResult.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return executionResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Windows Service undeployment failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
