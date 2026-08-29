using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Services.Remote;

public class WinRmExecutor : IRemoteExecutor
{
    private readonly ILogger<WinRmExecutor> _logger;

    public string ProtocolName => "WinRM";

    public WinRmExecutor(ILogger<WinRmExecutor> logger)
    {
        _logger = logger;
    }

    private WSManConnectionInfo CreateConnectionInfo(RemoteConnectionConfig config)
    {
        var scheme = config.UseSsl ? "https" : "http";
        var port = config.Port ?? (config.UseSsl ? 5986 : 5985);
        var uri = new Uri($"{scheme}://{config.Host}:{port}/wsman");

        PSCredential? credential = null;
        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            var securePassword = new SecureString();
            if (!string.IsNullOrEmpty(config.Password))
            {
                foreach (var c in config.Password)
                    securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();
            credential = new PSCredential(config.Username, securePassword);
        }

        var wsMan = credential != null
            ? new WSManConnectionInfo(uri, "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", credential)
            : new WSManConnectionInfo(uri) { AuthenticationMechanism = AuthenticationMechanism.Default };

        wsMan.OperationTimeout = (int)TimeSpan.FromSeconds(config.TimeoutSeconds).TotalMilliseconds;
        wsMan.OpenTimeout = (int)TimeSpan.FromSeconds(config.TimeoutSeconds).TotalMilliseconds;

        if (config.UseSsl)
        {
            wsMan.SkipCACheck = true;
            wsMan.SkipCNCheck = true;
            wsMan.SkipRevocationCheck = true;
        }

        return wsMan;
    }

    public async Task<RemoteExecutionResult> TestConnectivityAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var connInfo = CreateConnectionInfo(config);
                using var runspace = RunspaceFactory.CreateRunspace(connInfo);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript("$env:COMPUTERNAME + ' | ' + (Get-CimInstance Win32_OperatingSystem).Caption");

                var results = ps.Invoke();
                runspace.Close();

                var output = string.Join("\n", results.Select(r => r?.ToString() ?? ""));
                return new RemoteExecutionResult
                {
                    Success = !ps.HadErrors,
                    ExitCode = ps.HadErrors ? 1 : 0,
                    StandardOutput = $"WinRM Connected to {config.Host}. System: {output.Trim()}",
                    StandardError = string.Join("\n", ps.Streams.Error.Select(e => e.ToString()))
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WinRM Connection test failed for {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"WinRM Connection error: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    public async Task<RemoteExecutionResult> ExecutePowerShellScriptAsync(string script, RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var connInfo = CreateConnectionInfo(config);
                using var runspace = RunspaceFactory.CreateRunspace(connInfo);
                runspace.Open();

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;
                ps.AddScript(script);

                _logger.LogInformation("Executing script via WinRM on {Host}...", config.Host);
                var results = ps.Invoke();
                runspace.Close();

                var outputBuilder = new StringBuilder();
                foreach (var item in results)
                {
                    if (item != null)
                        outputBuilder.AppendLine(item.ToString());
                }

                var errorBuilder = new StringBuilder();
                if (ps.Streams.Error.Count > 0)
                {
                    foreach (var err in ps.Streams.Error)
                    {
                        errorBuilder.AppendLine(err.ToString());
                    }
                }

                var hasError = ps.HadErrors;

                return new RemoteExecutionResult
                {
                    Success = !hasError,
                    ExitCode = hasError ? 1 : 0,
                    StandardOutput = outputBuilder.ToString(),
                    StandardError = errorBuilder.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WinRM execution failed on {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"WinRM Exception: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    public async Task<RemoteExecutionResult> TransferFileAsync(string localFilePath, string remoteDestinationPath, RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(localFilePath))
                {
                    return new RemoteExecutionResult
                    {
                        Success = false,
                        ExitCode = 1,
                        StandardError = $"Local file '{localFilePath}' not found."
                    };
                }

                // Transfer via base64 encoded stream over WinRM session
                var fileBytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
                var base64 = Convert.ToBase64String(fileBytes);

                _logger.LogInformation("Transferring file ({Length} bytes) to {Host}:{RemotePath} over WinRM...", fileBytes.Length, config.Host, remoteDestinationPath);

                var script = $@"
$dest = '{remoteDestinationPath.Replace("'", "''")}'
$dir = [System.IO.Path]::GetDirectoryName($dest)
if (-not (Test-Path $dir)) {{
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}}
$bytes = [System.Convert]::FromBase64String('{base64}')
[System.IO.File]::WriteAllBytes($dest, $bytes)
Write-Output ""Transferred $(($bytes.Length)) bytes to $dest""
";

                return await ExecutePowerShellScriptAsync(script, config, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WinRM File transfer failed to {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"WinRM Transfer Error: {ex.Message}"
                };
            }
        }, cancellationToken);
    }
}
