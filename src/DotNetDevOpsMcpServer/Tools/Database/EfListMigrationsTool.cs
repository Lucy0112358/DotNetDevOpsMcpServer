using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Database;

namespace DotNetDevOpsMcpServer.Tools.Database;

public class EfListMigrationsTool : IDevOpsTool
{
    private readonly IEfCoreMigrationService _efService;

    public string Name => "ef_list_migrations";
    public string Description => "Lists all applied and pending Entity Framework Core Code-First migrations for a given project and database.";

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
            ["connectionString"] = new()
            {
                Type = "string",
                Description = "Optional database connection string to check migration status against."
            }
        },
        Required = new List<string> { "projectPath" }
    };

    public EfListMigrationsTool(IEfCoreMigrationService efService)
    {
        _efService = efService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = arguments.GetString("projectPath");
        var startupProjectPath = arguments.GetString("startupProjectPath");
        var dbContext = arguments.GetString("dbContext");
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

        var result = await _efService.ListMigrationsAsync(
            projectPath,
            startupProjectPath,
            dbContext,
            connectionString,
            cancellationToken);

        var payload = new
        {
            status = result.Success ? "success" : "error",
            appliedMigrations = result.AppliedMigrations,
            pendingMigrations = result.PendingMigrations,
            appliedCount = result.AppliedMigrations.Count,
            pendingCount = result.PendingMigrations.Count,
            message = result.Message,
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
