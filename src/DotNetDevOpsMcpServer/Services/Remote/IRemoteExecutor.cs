namespace DotNetDevOpsMcpServer.Services.Remote;

public interface IRemoteExecutor
{
    string ProtocolName { get; }
    Task<RemoteExecutionResult> ExecutePowerShellScriptAsync(string script, RemoteConnectionConfig config, CancellationToken cancellationToken = default);
    Task<RemoteExecutionResult> TransferFileAsync(string localFilePath, string remoteDestinationPath, RemoteConnectionConfig config, CancellationToken cancellationToken = default);
    Task<RemoteExecutionResult> TestConnectivityAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default);
}
