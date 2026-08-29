using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;

namespace DotNetDevOpsMcpServer.Tools;

public interface IDevOpsTool
{
    string Name { get; }
    string Description { get; }
    McpJsonSchema InputSchema { get; }
    Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default);
}
