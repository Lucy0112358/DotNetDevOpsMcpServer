namespace DotNetDevOpsMcpServer.Services.Database;

public class SchemaDiffResult
{
    public bool HasDifferences { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string RawReportXml { get; set; } = string.Empty;
    public List<string> Operations { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class ScriptGenerationResult
{
    public bool Success { get; set; }
    public string ScriptPath { get; set; } = string.Empty;
    public string ScriptContent { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

public class MigrationExecutionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int StatementsExecuted { get; set; }
    public List<string> Logs { get; set; } = new();
}

public interface IDatabaseService
{
    Task<SchemaDiffResult> CompareSchemasAsync(string sourceConnectionString, string targetConnectionString, bool ignorePermissions = true, bool dropObjectsNotInSource = false, CancellationToken cancellationToken = default);
    Task<ScriptGenerationResult> GenerateMigrationScriptAsync(string sourceConnectionString, string targetConnectionString, string? outputPath = null, bool ignorePermissions = true, bool dropObjectsNotInSource = false, CancellationToken cancellationToken = default);
    Task<MigrationExecutionResult> ApplyMigrationAsync(string targetConnectionString, string? scriptContent = null, string? scriptPath = null, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default);
}
