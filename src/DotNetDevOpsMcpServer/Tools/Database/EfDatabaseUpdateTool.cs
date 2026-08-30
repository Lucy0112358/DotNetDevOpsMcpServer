using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class EfDatabaseUpdateTool : IDevOpsTool
{
    private readonly IEfCoreMigrationService _efService;

    public string Name => "ef_database_update";
    public string Description => "Applies Entity Framework Core Code-First migrations directly to a target database using dotnet ef database update.";

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
            ["targetMigration"] = new()
            {
                Type = "string",
                Description = "Optional target migration to update to (e.g. '0' to rollback everything, or a specific migration name). Defaults to latest."
            },
            ["connectionString"] = new()
            {
                Type = "string",
                Description = "Optional database connection string override to update a remote or staging/prod database."
            }
        },
        Required = new List<string> { "projectPath" }
    };

    public EfDatabaseUpdateTool(IEfCoreMigrationService efService)
    {
        _efService = efService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = arguments.GetString("projectPath");
        var startupProjectPath = arguments.GetString("startupProjectPath");
        var dbContext = arguments.GetString("dbContext");
        var targetMigration = arguments.GetString("targetMigration");
        var connectionString = arguments.GetString("connectionString");

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

        var result = await _efService.UpdateDatabaseAsync(
            projectPath,
            startupProjectPath,
            dbContext,
            targetMigration,
            connectionString,
            cancellationToken);

        var payload = new
        {
            status = result.Success ? "success" : "error",
            message = result.Message,
            targetMigration = result.TargetMigration,
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
