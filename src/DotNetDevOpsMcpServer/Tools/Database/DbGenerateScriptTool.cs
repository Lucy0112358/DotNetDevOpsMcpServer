using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class DbGenerateScriptTool : IDevOpsTool
{
    private readonly IDatabaseService _databaseService;

    public string Name => "db_generate_migration_script";
    public string Description => "Generates a transactional, safe T-SQL drift/migration script that synchronizes the target database schema to match the source database schema using DacFx.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["sourceConnectionString"] = new()
            {
                Type = "string",
                Description = "SQL Server connection string for the Source database."
            },
            ["targetConnectionString"] = new()
            {
                Type = "string",
                Description = "SQL Server connection string for the Target database."
            },
            ["outputPath"] = new()
            {
                Type = "string",
                Description = "Optional file path where the generated .sql migration script will be saved. If omitted, a timestamped file in the migrations/ directory is created."
            },
            ["ignorePermissions"] = new()
            {
                Type = "boolean",
                Description = "Whether to ignore database permissions/roles during script generation (default: true).",
                Default = true
            },
            ["dropObjectsNotInSource"] = new()
            {
                Type = "boolean",
                Description = "Whether objects in the target not present in the source should be dropped in the migration script (default: false).",
                Default = false
            }
        },
        Required = new List<string> { "sourceConnectionString", "targetConnectionString" }
    };

    public DbGenerateScriptTool(IDatabaseService databaseService)
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

        var outputPath = arguments.GetString("outputPath");
        var ignorePermissions = arguments.GetBool("ignorePermissions", true);
        var dropObjectsNotInSource = arguments.GetBool("dropObjectsNotInSource", false);

        try
        {
            var genResult = await _databaseService.GenerateMigrationScriptAsync(
                sourceConn,
                targetConn,
                outputPath,
                ignorePermissions,
                dropObjectsNotInSource,
                cancellationToken);

            var previewLines = genResult.ScriptContent
                .Split('\n')
                .Take(40)
                .ToArray();

            var responseJson = JsonSerializer.Serialize(new
            {
                status = "success",
                scriptPath = genResult.ScriptPath,
                scriptSizeBytes = genResult.ScriptContent.Length,
                totalLines = genResult.ScriptContent.Split('\n').Length,
                warnings = genResult.Warnings,
                message = genResult.Message,
                scriptPreview = string.Join("\n", previewLines) + (genResult.ScriptContent.Split('\n').Length > 40 ? "\n... [Full script saved to file]" : "")
            }, new JsonSerializerOptions { WriteIndented = true });

            return McpCallToolResult.Text(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Migration script generation failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
