using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.Dac;

namespace DotNetDevOpsMcpServer.Services.Database;

public class SqlDacFxService : IDatabaseService
{
    private readonly ILogger<SqlDacFxService> _logger;

    public SqlDacFxService(ILogger<SqlDacFxService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION;";
            var version = await cmd.ExecuteScalarAsync(cancellationToken);
            _logger.LogInformation("Successfully connected to SQL Server: {Version}", version?.ToString()?.Split('\n').FirstOrDefault());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SQL Server.");
            return false;
        }
    }

    public async Task<SchemaDiffResult> CompareSchemasAsync(
        string sourceConnectionString,
        string targetConnectionString,
        bool ignorePermissions = true,
        bool dropObjectsNotInSource = false,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var tempDacpacPath = Path.Combine(Path.GetTempPath(), $"schema_source_{Guid.NewGuid():N}.dacpac");
            try
            {
                var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
                var targetBuilder = new SqlConnectionStringBuilder(targetConnectionString);

                var sourceDbName = sourceBuilder.InitialCatalog;
                var targetDbName = targetBuilder.InitialCatalog;

                if (string.IsNullOrWhiteSpace(sourceDbName))
                    throw new ArgumentException("Source connection string must specify Initial Catalog (Database name).");
                if (string.IsNullOrWhiteSpace(targetDbName))
                    throw new ArgumentException("Target connection string must specify Initial Catalog (Database name).");

                _logger.LogInformation("Extracting schema from source database '{SourceDb}'...", sourceDbName);
                var sourceServices = new DacServices(sourceConnectionString);
                sourceServices.Extract(tempDacpacPath, sourceDbName, "SchemaSync", new Version(1, 0, 0));

                _logger.LogInformation("Comparing against target database '{TargetDb}'...", targetDbName);
                using var dacpac = DacPackage.Load(tempDacpacPath);
                var targetServices = new DacServices(targetConnectionString);

                var options = new DacDeployOptions
                {
                    IgnorePermissions = ignorePermissions,
                    DropObjectsNotInSource = dropObjectsNotInSource,
                    BlockOnPossibleDataLoss = false,
                    ScriptDatabaseOptions = false,
                    IgnoreRoleMembership = true
                };

                var reportXml = targetServices.GenerateDeployReport(dacpac, targetDbName, options);

                var result = ParseDeployReport(reportXml);
                return result;
            }
            finally
            {
                if (File.Exists(tempDacpacPath))
                {
                    try { File.Delete(tempDacpacPath); } catch { /* ignore */ }
                }
            }
        }, cancellationToken);
    }

    public async Task<ScriptGenerationResult> GenerateMigrationScriptAsync(
        string sourceConnectionString,
        string targetConnectionString,
        string? outputPath = null,
        bool ignorePermissions = true,
        bool dropObjectsNotInSource = false,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var tempDacpacPath = Path.Combine(Path.GetTempPath(), $"schema_source_{Guid.NewGuid():N}.dacpac");
            try
            {
                var sourceBuilder = new SqlConnectionStringBuilder(sourceConnectionString);
                var targetBuilder = new SqlConnectionStringBuilder(targetConnectionString);

                var sourceDbName = sourceBuilder.InitialCatalog;
                var targetDbName = targetBuilder.InitialCatalog;

                if (string.IsNullOrWhiteSpace(sourceDbName))
                    throw new ArgumentException("Source connection string must specify Initial Catalog (Database name).");
                if (string.IsNullOrWhiteSpace(targetDbName))
                    throw new ArgumentException("Target connection string must specify Initial Catalog (Database name).");

                _logger.LogInformation("Extracting schema from source database '{SourceDb}' for script generation...", sourceDbName);
                var sourceServices = new DacServices(sourceConnectionString);
                sourceServices.Extract(tempDacpacPath, sourceDbName, "SchemaSync", new Version(1, 0, 0));

                _logger.LogInformation("Generating T-SQL deployment script for target database '{TargetDb}'...", targetDbName);
                using var dacpac = DacPackage.Load(tempDacpacPath);
                var targetServices = new DacServices(targetConnectionString);

                var warnings = new List<string>();
                targetServices.Message += (_, e) =>
                {
                    if (e.Message.MessageType == DacMessageType.Warning)
                        warnings.Add(e.Message.Message);
                };

                var options = new DacDeployOptions
                {
                    IgnorePermissions = ignorePermissions,
                    DropObjectsNotInSource = dropObjectsNotInSource,
                    BlockOnPossibleDataLoss = false,
                    ScriptDatabaseOptions = false,
                    IncludeTransactionalScripts = true,
                    IgnoreRoleMembership = true
                };

                var scriptContent = targetServices.GenerateDeployScript(dacpac, targetDbName, options);

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                    outputPath = Path.Combine(AppContext.BaseDirectory, "migrations", $"migration_{sourceDbName}_to_{targetDbName}_{timestamp}.sql");
                }

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(outputPath, scriptContent, Encoding.UTF8);
                _logger.LogInformation("Migration script written to: {OutputPath}", outputPath);

                return new ScriptGenerationResult
                {
                    Success = true,
                    ScriptPath = outputPath,
                    ScriptContent = scriptContent,
                    Warnings = warnings,
                    Message = $"Successfully generated migration script with {scriptContent.Split('\n').Length} lines."
                };
            }
            finally
            {
                if (File.Exists(tempDacpacPath))
                {
                    try { File.Delete(tempDacpacPath); } catch { /* ignore */ }
                }
            }
        }, cancellationToken);
    }

    public async Task<MigrationExecutionResult> ApplyMigrationAsync(
        string targetConnectionString,
        string? scriptContent = null,
        string? scriptPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MigrationExecutionResult();

        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                result.Success = false;
                result.Message = "Either scriptContent or a valid existing scriptPath must be provided.";
                return result;
            }

            scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
        }

        try
        {
            await using var conn = new SqlConnection(targetConnectionString);
            await conn.OpenAsync(cancellationToken);

            // Split into batches separated by GO commands (case insensitive on newline)
            var batches = Regex.Split(scriptContent, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            var count = 0;
            foreach (var batch in batches)
            {
                var trimmed = batch.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = trimmed;
                cmd.CommandTimeout = 300; // 5 minutes per batch

                await cmd.ExecuteNonQueryAsync(cancellationToken);
                count++;
            }

            result.Success = true;
            result.StatementsExecuted = count;
            result.Message = $"Successfully executed {count} T-SQL batches on target database.";
            _logger.LogInformation("{Message}", result.Message);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply migration script.");
            result.Success = false;
            result.Message = $"Migration execution failed: {ex.Message}";
            result.Logs.Add(ex.ToString());
            return result;
        }
    }

    private SchemaDiffResult ParseDeployReport(string reportXml)
    {
        var result = new SchemaDiffResult
        {
            RawReportXml = reportXml
        };

        if (string.IsNullOrWhiteSpace(reportXml))
        {
            result.HasDifferences = false;
            result.Summary = "No differences found between source and target schemas.";
            return result;
        }

        try
        {
            var doc = XDocument.Parse(reportXml);
            var root = doc.Root;
            if (root == null)
            {
                result.Summary = "Empty report XML.";
                return result;
            }

            var operations = new List<string>();
            var warnings = new List<string>();

            var ops = root.Descendants().Where(e => e.Name.LocalName is "Create" or "Alter" or "Drop");
            foreach (var op in ops)
            {
                var type = op.Attribute("Type")?.Value ?? "Object";
                var name = op.Attribute("Name")?.Value ?? "Unnamed";
                var action = op.Name.LocalName;
                operations.Add($"{action} {type}: {name}");
            }

            var alerts = root.Descendants().Where(e => e.Name.LocalName is "Alert" or "Issue" or "Warning");
            foreach (var alert in alerts)
            {
                var val = alert.Attribute("Value")?.Value ?? alert.Value;
                if (!string.IsNullOrWhiteSpace(val))
                    warnings.Add(val);
            }

            result.Operations = operations;
            result.Warnings = warnings;
            result.HasDifferences = operations.Count > 0;

            var sb = new StringBuilder();
            if (result.HasDifferences)
            {
                sb.AppendLine($"Found {operations.Count} schema modification(s):");
                foreach (var op in operations.Take(25))
                    sb.AppendLine($"- {op}");
                if (operations.Count > 25)
                    sb.AppendLine($"... and {operations.Count - 25} more operations.");

                if (warnings.Count > 0)
                {
                    sb.AppendLine($"\nWarnings ({warnings.Count}):");
                    foreach (var w in warnings)
                        sb.AppendLine($"[!] {w}");
                }
            }
            else
            {
                sb.AppendLine("Source and target database schemas are fully in sync (0 differences).");
            }

            result.Summary = sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse deploy report XML, returning raw XML string.");
            result.Summary = "Deploy report generated:\n" + reportXml;
            result.HasDifferences = true;
        }

        return result;
    }
}
