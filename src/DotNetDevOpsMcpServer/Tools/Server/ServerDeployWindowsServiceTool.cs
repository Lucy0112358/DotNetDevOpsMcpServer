using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerDeployWindowsServiceTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_deploy_windows_service";
    public string Description => "Deploys a .NET Worker / Background Service as a managed Windows Service on a local or remote Windows Server. Handles service lifecycle (stop/start/registration) and binary upgrades.";

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
            ["serviceName"] = new()
            {
                Type = "string",
                Description = "Unique name of the Windows Service (e.g. 'OrderProcessingWorker')."
            },
            ["displayName"] = new()
            {
                Type = "string",
                Description = "Human-readable display name for the Windows Service."
            },
            ["packagePath"] = new()
            {
                Type = "string",
                Description = "Local path to the published .zip archive or directory to deploy."
            },
            ["serviceDirectory"] = new()
            {
                Type = "string",
                Description = "Destination installation folder on the remote server (e.g. 'C:\\Services\\OrderWorker')."
            },
            ["executableName"] = new()
            {
                Type = "string",
                Description = "Name of the executable binary (e.g. 'OrderProcessing.exe'). If omitted, it will be auto-detected in the package."
            },
            ["startupType"] = new()
            {
                Type = "string",
                Description = "Service start mode: 'Automatic', 'Manual', or 'Disabled' (default: Automatic).",
                Enum = new List<string> { "Automatic", "Manual", "Disabled" },
                Default = "Automatic"
            },
            ["protocol"] = new()
            {
                Type = "string",
                Description = "Remote protocol: 'Auto', 'WinRM', 'SSH', or 'Local'.",
                Default = "Auto"
            }
        },
        Required = new List<string> { "host", "serviceName", "packagePath" }
    };

    public ServerDeployWindowsServiceTool(RemoteExecutorFactory executorFactory)
    {
        _executorFactory = executorFactory;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var host = arguments.GetString("host", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var serviceName = arguments.GetString("serviceName")!;
        var displayName = arguments.GetString("displayName", serviceName)!;
        var packagePath = arguments.GetString("packagePath")!;
        var serviceDir = arguments.GetString("serviceDirectory", $@"C:\Services\{serviceName}")!;
        var executableName = arguments.GetString("executableName");
        var startupType = arguments.GetString("startupType", "Automatic")!;
        var protocol = arguments.GetString("protocol", "Auto")!;

        if (!File.Exists(packagePath) && !Directory.Exists(packagePath))
        {
            return McpCallToolResult.Error($"Package path '{packagePath}' does not exist on the local system.");
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

            var remotePackageName = Path.GetFileName(packagePath);
            var remoteStagingFile = $@"C:\Windows\Temp\{remotePackageName}";

            if (!config.IsLocal)
            {
                var transferResult = await executor.TransferFileAsync(packagePath, remoteStagingFile, config, cancellationToken);
                if (!transferResult.Success)
                {
                    return McpCallToolResult.Error($"Failed to transfer package to remote server: {transferResult.StandardError}");
                }
            }
            else
            {
                remoteStagingFile = packagePath;
            }

            var deployScript = $@"
$ErrorActionPreference = 'Stop'

$serviceName = '{serviceName.Replace("'", "''")}'
$displayName = '{displayName.Replace("'", "''")}'
$serviceDir = '{serviceDir.Replace("'", "''")}'
$stagingFile = '{remoteStagingFile.Replace("'", "''")}'
$exeHint = '{executableName?.Replace("'", "''") ?? ""}'
$startupType = '{startupType.Replace("'", "''")}'

Write-Output ""[1/4] Stopping existing Windows Service '$serviceName' if running...""
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {{
    if ($existing.Status -eq 'Running') {{
        Stop-Service -Name $serviceName -Force
        Start-Sleep -Seconds 2
    }}
}}

Write-Output ""[2/4] Backing up and updating binaries at $serviceDir...""
if (Test-Path $serviceDir) {{
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backupPath = ""$($serviceDir)_backup_$timestamp""
    Copy-Item -Path $serviceDir -Destination $backupPath -Recurse -Force
    Write-Output ""Created backup at $backupPath""
}} else {{
    New-Item -ItemType Directory -Path $serviceDir -Force | Out-Null
}}

if ($stagingFile.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {{
    Expand-Archive -Path $stagingFile -DestinationPath $serviceDir -Force
}} else {{
    Copy-Item -Path ""$stagingFile\*"" -Destination $serviceDir -Recurse -Force
}}

# Locate executable
$exePath = $null
if ($exeHint -and (Test-Path (Join-Path $serviceDir $exeHint))) {{
    $exePath = (Join-Path $serviceDir $exeHint)
}} else {{
    $exes = Get-ChildItem -Path $serviceDir -Filter '*.exe' -File
    if ($exes.Count -gt 0) {{
        $exePath = $exes[0].FullName
    }}
}}

if (-not $exePath) {{
    throw ""No .exe found in $serviceDir to register as a Windows Service.""
}}

Write-Output ""[3/4] Registering/Configuring Windows Service pointing to $exePath...""
if (-not $existing) {{
    New-Service -Name $serviceName -DisplayName $displayName -BinaryPathName $exePath -StartupType $startupType | Out-Null
    Write-Output ""Created new Windows Service '$serviceName'""
}} else {{
    Set-Service -Name $serviceName -DisplayName $displayName -StartupType $startupType -ErrorAction SilentlyContinue
    sc.exe config $serviceName binPath= ""$exePath"" | Out-Null
    Write-Output ""Updated existing Windows Service '$serviceName'""
}}

Write-Output ""[4/4] Starting service '$serviceName'...""
Start-Service -Name $serviceName
Start-Sleep -Seconds 1
$status = (Get-Service -Name $serviceName).Status
Write-Output ""Service '$serviceName' is now in state: $status""
";

            var executionResult = await executor.ExecutePowerShellScriptAsync(deployScript, config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = executionResult.Success ? "success" : "failure",
                host = config.Host,
                serviceName,
                serviceDirectory = serviceDir,
                logs = executionResult.StandardOutput,
                errors = executionResult.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return executionResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Windows Service deployment failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
