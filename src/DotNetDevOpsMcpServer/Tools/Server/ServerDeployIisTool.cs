using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerDeployIisTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_deploy_iis";
    public string Description => "Deploys an ASP.NET Core application to IIS on a local or remote Windows Server. Handles file transfer, AppPool lifecycle, backup, No-Managed-Code configuration, and optional firewall opening.";

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
            ["siteName"] = new()
            {
                Type = "string",
                Description = "Name of the IIS Website (e.g. 'MyWebApiSite')."
            },
            ["appPoolName"] = new()
            {
                Type = "string",
                Description = "Name of the IIS Application Pool (defaults to siteName if omitted)."
            },
            ["port"] = new()
            {
                Type = "number",
                Description = "Binding port for the IIS site (default: 80).",
                Default = 80
            },
            ["packagePath"] = new()
            {
                Type = "string",
                Description = "Local path to the published .zip archive or directory to deploy."
            },
            ["physicalPath"] = new()
            {
                Type = "string",
                Description = "Destination folder path on the remote server (e.g. 'C:\\inetpub\\wwwroot\\MyWebApi')."
            },
            ["openFirewallPort"] = new()
            {
                Type = "boolean",
                Description = "Whether to automatically open the binding port in the Windows Firewall (default: true).",
                Default = true
            },
            ["clrVersion"] = new()
            {
                Type = "string",
                Description = "IIS AppPool CLR version: '' (No Managed Code for ASP.NET Core) or 'v4.0' (for .NET Framework 4.x).",
                Default = ""
            },
            ["protocol"] = new()
            {
                Type = "string",
                Description = "Remote protocol: 'Auto', 'WinRM', 'SSH', or 'Local'.",
                Default = "Auto"
            }
        },
        Required = new List<string> { "host", "siteName", "packagePath" }
    };

    public ServerDeployIisTool(RemoteExecutorFactory executorFactory)
    {
        _executorFactory = executorFactory;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var host = arguments.GetString("host", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var siteName = arguments.GetString("siteName")!;
        var appPoolName = arguments.GetString("appPoolName", siteName)!;
        var port = arguments.GetInt("port", 80) ?? 80;
        var packagePath = arguments.GetString("packagePath")!;
        var physicalPath = arguments.GetString("physicalPath", $@"C:\inetpub\wwwroot\{siteName}")!;
        var openFirewall = arguments.GetBool("openFirewallPort", true);
        var clrVersion = arguments.GetString("clrVersion", "") ?? "";
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

            // Step 1: Transfer package to remote server temp folder
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

            var firewallSnippet = openFirewall ? @"
Write-Output ""[5/5] Configuring Windows Firewall rule for port $port...""
$ruleName = ""IIS-$siteName-$port""
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -LocalPort $port -Protocol TCP -Action Allow | Out-Null
    Write-Output ""Created firewall rule $ruleName for port $port""
} else {
    Write-Output ""Firewall rule $ruleName already exists.""
}
" : @"
Write-Output ""[5/5] Skipping firewall configuration as requested.""
";

            var deployScript = $@"
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction SilentlyContinue

$siteName = '{siteName.Replace("'", "''")}'
$appPoolName = '{appPoolName.Replace("'", "''")}'
$port = {port}
$physicalPath = '{physicalPath.Replace("'", "''")}'
$stagingFile = '{remoteStagingFile.Replace("'", "''")}'
$clrVersion = '{clrVersion.Replace("'", "''")}'

Write-Output ""[1/5] Stopping IIS Site and AppPool if running...""
$hasWebAdmin = $false
try {{
    Import-Module WebAdministration -ErrorAction Stop
    $hasWebAdmin = Test-Path ""IIS:\Sites""
}} catch {{}}

$appcmd = ""$env:SystemRoot\System32\inetsrv\appcmd.exe""

if ($hasWebAdmin) {{
    if (Test-Path ""IIS:\Sites\$siteName"") {{
        try {{ Stop-WebSite -Name $siteName -ErrorAction SilentlyContinue }} catch {{}}
    }}
    if (Test-Path ""IIS:\AppPools\$appPoolName"") {{
        try {{ Stop-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue }} catch {{}}
    }}
}} elseif (Test-Path $appcmd) {{
    & $appcmd stop site ""$siteName"" 2>$null
    & $appcmd stop apppool ""$appPoolName"" 2>$null
}}

Write-Output ""[2/5] Backing up and updating binaries at $physicalPath...""
if (Test-Path $physicalPath) {{
    $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backupPath = ""$($physicalPath)_backup_$timestamp""
    Copy-Item -Path $physicalPath -Destination $backupPath -Recurse -Force
    Write-Output ""Created backup at $backupPath""
}} else {{
    New-Item -ItemType Directory -Path $physicalPath -Force | Out-Null
}}

if ($stagingFile.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {{
    Expand-Archive -Path $stagingFile -DestinationPath $physicalPath -Force
    $publishedSubdir = Get-ChildItem -Path ""$physicalPath\_PublishedWebsites"" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($publishedSubdir) {{
        Copy-Item -Path ""$($publishedSubdir.FullName)\*"" -Destination $physicalPath -Recurse -Force
        Remove-Item -Path ""$physicalPath\_PublishedWebsites"" -Recurse -Force -ErrorAction SilentlyContinue
    }}
}} else {{
    Copy-Item -Path ""$stagingFile\*"" -Destination $physicalPath -Recurse -Force
}}

Write-Output ""[3/5] Configuring IIS AppPool and Website...""
if ($hasWebAdmin) {{
    # Check if AppPool exists, create if not
    if (-not (Test-Path ""IIS:\AppPools\$appPoolName"")) {{
        New-WebAppPool -Name $appPoolName | Out-Null
        Write-Output ""Created new AppPool: $appPoolName""
    }} else {{
        Write-Output ""AppPool $appPoolName already exists. Updating...""
    }}
    Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name ""managedRuntimeVersion"" -Value $clrVersion
    Set-ItemProperty ""IIS:\AppPools\$appPoolName"" -Name ""startMode"" -Value ""AlwaysRunning""

    # Check if Site exists, create if not
    if (-not (Test-Path ""IIS:\Sites\$siteName"")) {{
        New-WebSite -Name $siteName -Port $port -PhysicalPath $physicalPath -ApplicationPool $appPoolName | Out-Null
        Write-Output ""Created new IIS Website: $siteName on port $port""
    }} else {{
        Write-Output ""IIS Website $siteName already exists. Updating physical path and AppPool...""
        Set-ItemProperty ""IIS:\Sites\$siteName"" -Name ""physicalPath"" -Value $physicalPath
        Set-ItemProperty ""IIS:\Sites\$siteName"" -Name ""applicationPool"" -Value $appPoolName
    }}
}} elseif (Test-Path $appcmd) {{
    $existingPool = & $appcmd list apppool ""$appPoolName"" 2>$null
    if (-not $existingPool) {{
        & $appcmd add apppool /name:""$appPoolName""
        Write-Output ""Created new AppPool via appcmd: $appPoolName""
    }}
    if ($clrVersion) {{
        & $appcmd set apppool /apppool.name:""$appPoolName"" /managedRuntimeVersion:""$clrVersion""
    }} else {{
        & $appcmd set apppool /apppool.name:""$appPoolName"" /managedRuntimeVersion:""""
    }}

    $existingSite = & $appcmd list site ""$siteName"" 2>$null
    if (-not $existingSite) {{
        & $appcmd add site /name:""$siteName"" /bindings:""http/*:$($port):"" /physicalPath:""$physicalPath""
        & $appcmd set site /site.name:""$siteName"" /[path='/'].applicationPool:""$appPoolName""
        Write-Output ""Created new IIS Website via appcmd: $siteName on port $port""
    }} else {{
        & $appcmd set site /site.name:""$siteName"" /[path='/'].physicalPath:""$physicalPath""
        & $appcmd set site /site.name:""$siteName"" /[path='/'].applicationPool:""$appPoolName""
        Write-Output ""Updated IIS Website $siteName via appcmd""
    }}
}}

Write-Output ""[4/5] Starting IIS AppPool and Website...""
if ($hasWebAdmin) {{
    Start-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue
    Start-WebSite -Name $siteName -ErrorAction SilentlyContinue
}} elseif (Test-Path $appcmd) {{
    & $appcmd start apppool ""$appPoolName"" 2>$null
    & $appcmd start site ""$siteName"" 2>$null
}}

{firewallSnippet}

Write-Output ""Deployment completed successfully for site $siteName on port $port.""
";

            var executionResult = await executor.ExecutePowerShellScriptAsync(deployScript, config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = executionResult.Success ? "success" : "failure",
                host = config.Host,
                siteName,
                appPoolName,
                port,
                physicalPath,
                logs = executionResult.StandardOutput,
                errors = executionResult.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return executionResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"IIS deployment failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
