using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class EfGenerateMigrationScriptTool : IDevOpsTool
{
    private readonly IEfCoreMigrationService _efService;

    public string Name => "ef_generate_migration_script";
    public string Description => "Generates a pure Entity Framework Core T-SQL migration script directly from C# DbContext & migrations (Code-First) using dotnet ef.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["projectPath"] = new()
            {
                Type = "string",
                Description = "Path to the .NET project file (.csproj) containing the EF Core DbContext and migrations."
            },
            ["startupProjectPath"] = new()
            {
                Type = "string",
                Description = "Optional path to the startup project file (.csproj) containing Program.cs / appsettings.json."
            },
            ["dbContext"] = new()
            {
                Type = "string",
                Description = "Optional name of the DbContext class to use (if multiple DbContexts exist)."
            },
            ["fromMigration"] = new()
            {
                Type = "string",
                Description = "Optional starting migration name (e.g. '0' or a previous migration name). If omitted, scripts from the beginning."
            },
            ["toMigration"] = new()
            {
                Type = "string",
                Description = "Optional target migration name. If omitted, defaults to the latest migration."
            },
            ["idempotent"] = new()
            {
                Type = "boolean",
                Description = "Whether to generate an idempotent script that checks __EFMigrationsHistory before applying each migration (default: true).",
                Default = true
            },
            ["outputPath"] = new()
            {
                Type = "string",
                Description = "Optional file path where the generated .sql migration script will be saved."
            }
        },
        Required = new List<string> { "projectPath" }
    };

    public EfGenerateMigrationScriptTool(IEfCoreMigrationService efService)
    {
        _efService = efService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = arguments.GetString("projectPath");
        var startupProjectPath = arguments.GetString("startupProjectPath");
        var dbContext = arguments.GetString("dbContext");
        var fromMigration = arguments.GetString("fromMigration");
        var toMigration = arguments.GetString("toMigration");
        var idempotent = arguments.GetBool("idempotent", true);
        var outputPath = arguments.GetString("outputPath");

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return new McpCallToolResult
            {
                IsError = true,
                Content = new List<McpContent>
                {
                    new() { Type = "text", Text = "Error: 'projectPath' is required." }
                }
            };
        }

        var result = await _efService.GenerateScriptAsync(
            projectPath,
            startupProjectPath,
            dbContext,
            fromMigration,
            toMigration,
            idempotent,
            outputPath,
            cancellationToken);

        var preview = result.ScriptContent.Length > 2000
            ? result.ScriptContent[..2000] + "\r\n... [Full script saved to file]"
            : result.ScriptContent;

        var payload = new
        {
            status = result.Success ? "success" : "error",
            scriptPath = result.ScriptPath,
            scriptSizeBytes = result.ScriptContent.Length,
            totalLines = result.ScriptContent.Split('\n').Length,
            message = result.Message,
            scriptPreview = preview,
            logs = result.OutputLogs
        };

        return new McpCallToolResult
        {
            IsError = !result.Success,
            Content = new List<McpContent>
            {
                new()
                {
                    Type = "text",
                    Text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
                }
            }
        };
    }
}
