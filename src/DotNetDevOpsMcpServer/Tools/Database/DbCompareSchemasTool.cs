using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class DbCompareSchemasTool : IDevOpsTool
{
    private readonly IDatabaseService _databaseService;

    public string Name => "db_compare_schemas";
    public string Description => "Compares two SQL Server database schemas (e.g., Dev vs Staging/Prod) using DacFx and outputs a structural difference report (added/altered/dropped tables, views, SPs, indexes).";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["sourceConnectionString"] = new()
            {
                Type = "string",
                Description = "SQL Server connection string for the Source database (e.g. Server=dev-sql;Database=MyDb;User Id=sa;Password=secret;TrustServerCertificate=True;)"
            },
            ["targetConnectionString"] = new()
            {
                Type = "string",
                Description = "SQL Server connection string for the Target database to compare against."
            },
            ["ignorePermissions"] = new()
            {
                Type = "boolean",
                Description = "Whether to ignore database permissions/roles during schema comparison (default: true).",
                Default = true
            },
            ["dropObjectsNotInSource"] = new()
            {
                Type = "boolean",
                Description = "Whether objects in the target not present in the source should be marked to drop (default: false).",
                Default = false
            }
        },
        Required = new List<string> { "sourceConnectionString", "targetConnectionString" }
    };

    public DbCompareSchemasTool(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var sourceConn = arguments.GetString("sourceConnectionString");
        var targetConn = arguments.GetString("targetConnectionString");

        if (string.IsNullOrWhiteSpace(sourceConn) || string.IsNullOrWhiteSpace(targetConn))
        {
            return McpCallToolResult.Error("Both 'sourceConnectionString' and 'targetConnectionString' are required.");
        }

        var ignorePermissions = arguments.GetBool("ignorePermissions", true);
        var dropObjectsNotInSource = arguments.GetBool("dropObjectsNotInSource", false);

        try
        {
            var diffResult = await _databaseService.CompareSchemasAsync(
                sourceConn,
                targetConn,
                ignorePermissions,
                dropObjectsNotInSource,
                cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = "success",
                hasDifferences = diffResult.HasDifferences,
                operationsCount = diffResult.Operations.Count,
                operations = diffResult.Operations,
                warnings = diffResult.Warnings,
                summary = diffResult.Summary
            }, new JsonSerializerOptions { WriteIndented = true });

            return McpCallToolResult.Text(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Schema comparison failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
