using System.Text;
using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Transport;

public class StdioTransport : ITransport
{
    private readonly McpServerEngine _engine;
    private readonly ILogger<StdioTransport> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public StdioTransport(McpServerEngine engine, ILogger<StdioTransport> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MCP stdio transport loop...");

        // Ensure UTF-8 without BOM
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        using var reader = new StreamReader(Console.OpenStandardInput(), Console.InputEncoding);
        using var writer = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) // EOF
            {
                _logger.LogInformation("Stdio stream reached EOF. Shutting down transport.");
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var response = await _engine.HandleMessageAsync(line, cancellationToken);
                if (response != null)
                {
                    var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                    await writer.WriteLineAsync(responseJson.AsMemory(), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing stdio message");
            }
        }
    }
}
