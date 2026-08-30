using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Services.Remote;

public class LocalExecutor : IRemoteExecutor
{
    private readonly ILogger<LocalExecutor> _logger;

    public string ProtocolName => "Local";

    public LocalExecutor(ILogger<LocalExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteExecutionResult> TestConnectivityAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var os = Environment.OSVersion.ToString();
            var machine = Environment.MachineName;
            return new RemoteExecutionResult
            {
                Success = true,
                ExitCode = 0,
                StandardOutput = $"Local Host Verified: {machine} ({os})"
            };
        }, cancellationToken);
    }

    public async Task<RemoteExecutionResult> ExecutePowerShellScriptAsync(string script, RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            _logger.LogInformation("Executing local PowerShell script via Windows PowerShell...");

            var tempScriptPath = Path.Combine(Path.GetTempPath(), $"mcp_exec_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(tempScriptPath, script, Encoding.UTF8, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancellationToken);

                var exitCode = process.ExitCode;
                var hasError = exitCode != 0;

                return new RemoteExecutionResult
                {
                    Success = !hasError,
                    ExitCode = exitCode,
                    StandardOutput = outputBuilder.ToString(),
                    StandardError = errorBuilder.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute local PowerShell process");
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = ex.Message
                };
            }
            finally
            {
                try
                {
                    if (File.Exists(tempScriptPath))
                        File.Delete(tempScriptPath);
                }
                catch { }
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

                var dir = Path.GetDirectoryName(remoteDestinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(localFilePath, remoteDestinationPath, true);
                _logger.LogInformation("Copied {Source} to {Destination}", localFilePath, remoteDestinationPath);

                return new RemoteExecutionResult
                {
                    Success = true,
                    ExitCode = 0,
                    StandardOutput = $"File copied locally to {remoteDestinationPath}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy file locally");
                return new RemoteExecutionResult
                {
                    Success = false,
                    ExitCode = 1,
                    StandardError = ex.Message
                };
            }
        }, cancellationToken);
    }
}
