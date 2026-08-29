using System.Management.Automation;
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
        return await Task.Run(() =>
        {
            _logger.LogInformation("Executing local PowerShell script...");
            using var ps = PowerShell.Create();
            ps.AddScript(script);

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            var results = ps.Invoke();

            foreach (var item in results)
            {
                if (item != null)
                    outputBuilder.AppendLine(item.ToString());
            }

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
