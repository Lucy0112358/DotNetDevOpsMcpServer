using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class DbApplyMigrationTool : IDevOpsTool
{
    private readonly IDatabaseService _databaseService;

    public string Name => "db_apply_migration";
    public string Description => "Applies a T-SQL schema migration script (from file or string) against the specified target SQL Server database.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["targetConnectionString"] = new()
            {
                Type = "string",
                Description = "SQL Server connection string for the Target database where the migration will be applied."
            },
            ["scriptPath"] = new()
            {
                Type = "string",
                Description = "Path to the .sql migration script file to execute (optional if scriptContent is provided)."
            },
            ["scriptContent"] = new()
            {
                Type = "string",
                Description = "Raw T-SQL migration script content (optional if scriptPath is provided)."
            }
        },
        Required = new List<string> { "targetConnectionString" }
    };

    public DbApplyMigrationTool(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var targetConn = arguments.GetString("targetConnectionString");
        var scriptPath = arguments.GetString("scriptPath");
        var scriptContent = arguments.GetString("scriptContent");

        if (string.IsNullOrWhiteSpace(targetConn))
        {
            return McpCallToolResult.Error("'targetConnectionString' is required.");
        }

        if (string.IsNullOrWhiteSpace(scriptPath) && string.IsNullOrWhiteSpace(scriptContent))
        {
            return McpCallToolResult.Error("Either 'scriptPath' or 'scriptContent' must be provided.");
        }

        try
        {
            var result = await _databaseService.ApplyMigrationAsync(
                targetConn,
                scriptContent,
                scriptPath,
                cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = result.Success ? "success" : "failure",
                statementsExecuted = result.StatementsExecuted,
                message = result.Message,
                logs = result.Logs
            }, new JsonSerializerOptions { WriteIndented = true });

            return result.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Migration application failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
