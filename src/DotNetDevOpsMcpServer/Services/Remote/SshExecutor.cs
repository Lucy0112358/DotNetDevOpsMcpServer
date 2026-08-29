using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace DotNetDevOpsMcpServer.Services.Remote;

public class SshExecutor : IRemoteExecutor
{
    private readonly ILogger<SshExecutor> _logger;

    public string ProtocolName => "SSH";

    public SshExecutor(ILogger<SshExecutor> logger)
    {
        _logger = logger;
    }

    private Renci.SshNet.ConnectionInfo CreateConnectionInfo(RemoteConnectionConfig config)
    {
        var port = config.Port ?? 22;
        var username = config.Username ?? "Administrator";
        var password = config.Password ?? string.Empty;

        return new Renci.SshNet.ConnectionInfo(
            config.Host,
            port,
            username,
            new PasswordAuthenticationMethod(username, password)
        )
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
    }

    public async Task<RemoteExecutionResult> TestConnectivityAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var connInfo = CreateConnectionInfo(config);
                using var client = new SshClient(connInfo);
                client.Connect();
                var cmd = client.RunCommand("powershell.exe -Command \"[System.Environment]::OSVersion.VersionString\"");
                client.Disconnect();

                var exitStatus = cmd.ExitStatus ?? (string.IsNullOrEmpty(cmd.Error) ? 0 : 1);
                return new RemoteExecutionResult
                {
                    Success = exitStatus == 0,
                    ExitCode = exitStatus,
                    StandardOutput = $"SSH Connected to {config.Host}. OS: {cmd.Result.Trim()}",
                    StandardError = cmd.Error
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH Connection test failed for {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"SSH Connection error: {ex.Message}"
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
                using var client = new SshClient(connInfo);
                client.Connect();

                // Encode script to base64 for safe PowerShell execution on Windows OpenSSH
                var bytes = Encoding.Unicode.GetBytes(script);
                var encodedScript = Convert.ToBase64String(bytes);
                var commandText = $"powershell.exe -NoProfile -NonInteractive -EncodedCommand {encodedScript}";

                _logger.LogInformation("Executing script via SSH on {Host}...", config.Host);
                var cmd = client.CreateCommand(commandText);
                cmd.CommandTimeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
                cmd.Execute();
                client.Disconnect();

                var exitStatus = cmd.ExitStatus ?? (string.IsNullOrEmpty(cmd.Error) ? 0 : 1);
                var success = exitStatus == 0;
                return new RemoteExecutionResult
                {
                    Success = success,
                    ExitCode = exitStatus,
                    StandardOutput = cmd.Result,
                    StandardError = cmd.Error
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH execution failed on {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"SSH Execution Exception: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    public async Task<RemoteExecutionResult> TransferFileAsync(string localFilePath, string remoteDestinationPath, RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
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

                var connInfo = CreateConnectionInfo(config);
                using var sftp = new SftpClient(connInfo);
                sftp.Connect();

                using var fs = File.OpenRead(localFilePath);
                _logger.LogInformation("Uploading {LocalFile} via SFTP to {Host}:{RemotePath}...", localFilePath, config.Host, remoteDestinationPath);
                
                // Ensure target directory exists on remote if possible
                var remoteDir = Path.GetDirectoryName(remoteDestinationPath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(remoteDir) && !sftp.Exists(remoteDir))
                {
                    try { sftp.CreateDirectory(remoteDir); } catch { /* ignore if already exists */ }
                }

                sftp.UploadFile(fs, remoteDestinationPath.Replace('\\', '/'));
                sftp.Disconnect();

                return new RemoteExecutionResult
                {
                    Success = true,
                    ExitCode = 0,
                    StandardOutput = $"Uploaded {localFilePath} to {remoteDestinationPath} on {config.Host}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SFTP File transfer failed to {Host}", config.Host);
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = $"SFTP Transfer Error: {ex.Message}"
                };
            }
        }, cancellationToken);
    }
}
