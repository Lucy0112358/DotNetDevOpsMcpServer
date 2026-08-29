namespace DotNetDevOpsMcpServer.Transport;

public interface ITransport
{
    Task RunAsync(CancellationToken cancellationToken);
}
