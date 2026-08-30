using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Remote;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerUndeployIisTool : IDevOpsTool
{
    private readonly RemoteExecutorFactory _executorFactory;

    public string Name => "server_undeploy_iis";

    public string Description =>
        "Stops, unregisters, and removes an IIS Website and its Application Pool from a local or remote Windows server, with options to delete physical files and firewall rules.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["host"] = new() { Type = "string", Description = "Target server hostname or IP. Defaults to 'localhost'." },
            ["username"] = new() { Type = "string", Description = "Username for remote credentials (WinRM/SSH)." },
            ["password"] = new() { Type = "string", Description = "Password for remote credentials." },
            ["siteName"] = new() { Type = "string", Description = "The name of the IIS Website to remove." },
            ["appPoolName"] = new() { Type = "string", Description = "The name of the AppPool to remove. Defaults to siteName." },
            ["deleteFiles"] = new() { Type = "boolean", Description = "Whether to delete the website's physical directory on disk. Defaults to false." },
            ["removeFirewallRule"] = new() { Type = "boolean", Description = "Whether to remove associated firewall rules. Defaults to true." },
            ["protocol"] = new() { Type = "string", Description = "Remote protocol: 'Auto', 'Local', 'WinRM', or 'SSH'. Defaults to 'Auto'." }
        },
        Required = new List<string> { "siteName" }
    };

    public ServerUndeployIisTool(RemoteExecutorFactory executorFactory)
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
        var deleteFiles = arguments.GetBool("deleteFiles", false);
        var removeFirewall = arguments.GetBool("removeFirewallRule", true);
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
Import-Module WebAdministration -ErrorAction SilentlyContinue

$siteName = '{siteName.Replace("'", "''")}'
$appPoolName = '{appPoolName.Replace("'", "''")}'
$deleteFiles = {(deleteFiles ? "$true" : "$false")}
$removeFirewall = {(removeFirewall ? "$true" : "$false")}

$hasWebAdmin = $false
try {{
    Import-Module WebAdministration -ErrorAction Stop
    $hasWebAdmin = Test-Path ""IIS:\Sites""
}} catch {{}}

$appcmd = ""$env:SystemRoot\System32\inetsrv\appcmd.exe""
$physicalPath = """"

if ($hasWebAdmin) {{
    if (Test-Path ""IIS:\Sites\$siteName"") {{
        try {{
            $physicalPath = (Get-ItemProperty ""IIS:\Sites\$siteName"" -Name physicalPath).Value
        }} catch {{}}
        Write-Output ""[1/4] Stopping and removing IIS Website: $siteName...""
        try {{ Stop-WebSite -Name $siteName -ErrorAction SilentlyContinue }} catch {{}}
        Remove-WebSite -Name $siteName -ErrorAction SilentlyContinue
        Write-Output ""Removed IIS Website: $siteName""
    }} else {{
        Write-Output ""[1/4] IIS Website $siteName does not exist.""
    }}

    if (Test-Path ""IIS:\AppPools\$appPoolName"") {{
        Write-Output ""[2/4] Stopping and removing AppPool: $appPoolName...""
        try {{ Stop-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue }} catch {{}}
        Remove-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue
        Write-Output ""Removed AppPool: $appPoolName""
    }} else {{
        Write-Output ""[2/4] AppPool $appPoolName does not exist.""
    }}
}} elseif (Test-Path $appcmd) {{
    Write-Output ""[1/4] Stopping and removing IIS Website via appcmd: $siteName...""
    & $appcmd stop site ""$siteName"" 2>$null
    & $appcmd delete site ""$siteName"" 2>$null
    Write-Output ""[2/4] Stopping and removing AppPool via appcmd: $appPoolName...""
    & $appcmd stop apppool ""$appPoolName"" 2>$null
    & $appcmd delete apppool ""$appPoolName"" 2>$null
}}

if ($deleteFiles -and $physicalPath -and (Test-Path $physicalPath)) {{
    Write-Output ""[3/4] Deleting physical files at $physicalPath...""
    Remove-Item -Path $physicalPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output ""Physical files deleted.""
}} else {{
    Write-Output ""[3/4] Physical files preserved.""
}}

if ($removeFirewall) {{
    Write-Output ""[4/4] Removing firewall rules for $siteName...""
    Get-NetFirewallRule -DisplayName ""IIS-$siteName-*"" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
}} else {{
    Write-Output ""[4/4] Skipping firewall rule removal.""
}}

$stillExists = $false
if ($hasWebAdmin) {{
    $stillExists = Test-Path ""IIS:\Sites\$siteName""
}} elseif (Test-Path $appcmd) {{
    $chk = & $appcmd list site ""$siteName"" 2>$null
    if ($chk -and -not ($chk -match 'ERROR')) {{
        $stillExists = $true
    }}
}}

if ($stillExists) {{
    throw ""Failed to remove IIS site '$siteName'. Check that the process has permissions to modify IIS configuration.""
}}

Write-Output ""Undeployment completed successfully for IIS site $siteName.""
";

            var executionResult = await executor.ExecutePowerShellScriptAsync(undeployScript, config, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = executionResult.Success ? "success" : "failure",
                host = config.Host,
                siteName,
                appPoolName,
                logs = executionResult.StandardOutput,
                errors = executionResult.StandardError
            }, new JsonSerializerOptions { WriteIndented = true });

            return executionResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"IIS undeployment failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
