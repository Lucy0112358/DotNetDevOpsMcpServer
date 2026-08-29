using Microsoft.Extensions.DependencyInjection;

namespace DotNetDevOpsMcpServer.Services.Remote;

public class RemoteExecutorFactory
{
    private readonly IServiceProvider _serviceProvider;

    public RemoteExecutorFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IRemoteExecutor GetExecutor(RemoteConnectionConfig config)
    {
        if (config.IsLocal)
        {
            return _serviceProvider.GetRequiredService<LocalExecutor>();
        }

        if (string.Equals(config.Protocol, "SSH", StringComparison.OrdinalIgnoreCase) || config.Port == 22)
        {
            return _serviceProvider.GetRequiredService<SshExecutor>();
        }

        if (string.Equals(config.Protocol, "WinRM", StringComparison.OrdinalIgnoreCase) || config.Port is 5985 or 5986)
        {
            return _serviceProvider.GetRequiredService<WinRmExecutor>();
        }

        // Auto default: WinRM if Windows environment/default, fallback to SSH
        return _serviceProvider.GetRequiredService<WinRmExecutor>();
    }
}
