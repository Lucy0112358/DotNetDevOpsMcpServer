namespace DotNetDevOpsMcpServer.Services.Database;

public class EfMigrationScriptResult
{
    public bool Success { get; set; }
    public string ScriptPath { get; set; } = string.Empty;
    public string ScriptContent { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public List<string> OutputLogs { get; set; } = new();
}

public class EfDatabaseUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string TargetMigration { get; set; } = string.Empty;
    public List<string> OutputLogs { get; set; } = new();
}

public class EfListMigrationsResult
{
    public bool Success { get; set; }
    public List<string> AppliedMigrations { get; set; } = new();
    public List<string> PendingMigrations { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public List<string> OutputLogs { get; set; } = new();
}

public interface IEfCoreMigrationService
{
    Task<EfMigrationScriptResult> GenerateScriptAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? fromMigration = null,
        string? toMigration = null,
        bool idempotent = true,
        string? outputPath = null,
        CancellationToken cancellationToken = default);

    Task<EfDatabaseUpdateResult> UpdateDatabaseAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? targetMigration = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);

    Task<EfListMigrationsResult> ListMigrationsAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);
}
