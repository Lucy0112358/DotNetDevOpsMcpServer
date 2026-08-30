using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Services.Database;

public class EfCoreMigrationService : IEfCoreMigrationService
{
    private readonly ILogger<EfCoreMigrationService> _logger;

    public EfCoreMigrationService(ILogger<EfCoreMigrationService> logger)
    {
        _logger = logger;
    }

    public async Task<EfMigrationScriptResult> GenerateScriptAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? fromMigration = null,
        string? toMigration = null,
        bool idempotent = true,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        var result = new EfMigrationScriptResult();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            result.Success = false;
            result.Message = "ProjectPath is required.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            outputPath = Path.Combine(AppContext.BaseDirectory, "migrations", $"ef_migration_{timestamp}.sql");
        }

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var args = new StringBuilder("ef migrations script");
        if (!string.IsNullOrWhiteSpace(fromMigration))
            args.Append($" \"{fromMigration}\"");
        if (!string.IsNullOrWhiteSpace(toMigration))
            args.Append($" \"{toMigration}\"");

        if (idempotent)
            args.Append(" --idempotent");

        args.Append($" --project \"{projectPath}\"");

        if (!string.IsNullOrWhiteSpace(startupProjectPath))
            args.Append($" --startup-project \"{startupProjectPath}\"");

        if (!string.IsNullOrWhiteSpace(dbContext))
            args.Append($" --context \"{dbContext}\"");

        args.Append($" --output \"{outputPath}\"");

        _logger.LogInformation("Executing dotnet {Args}...", args);
        var execResult = await RunDotnetProcessAsync(args.ToString(), Path.GetDirectoryName(projectPath), cancellationToken);

        result.OutputLogs = execResult.Logs;
        if (!execResult.Success)
        {
            result.Success = false;
            result.Message = $"dotnet ef command failed (Exit code {execResult.ExitCode}): {execResult.Error}";
            return result;
        }

        if (File.Exists(outputPath))
        {
            result.Success = true;
            result.ScriptPath = outputPath;
            result.ScriptContent = await File.ReadAllTextAsync(outputPath, cancellationToken);
            result.Message = $"Successfully generated EF Core migration script ({result.ScriptContent.Split('\n').Length} lines).";
            _logger.LogInformation("{Message} Output at {Path}", result.Message, outputPath);
        }
        else
        {
            result.Success = false;
            result.Message = "dotnet ef completed but output script file was not found.";
        }

        return result;
    }

    public async Task<EfDatabaseUpdateResult> UpdateDatabaseAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? targetMigration = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var result = new EfDatabaseUpdateResult();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            result.Success = false;
            result.Message = "ProjectPath is required.";
            return result;
        }

        var args = new StringBuilder("ef database update");
        if (!string.IsNullOrWhiteSpace(targetMigration))
        {
            args.Append($" \"{targetMigration}\"");
            result.TargetMigration = targetMigration;
        }

        args.Append($" --project \"{projectPath}\"");

        if (!string.IsNullOrWhiteSpace(startupProjectPath))
            args.Append($" --startup-project \"{startupProjectPath}\"");

        if (!string.IsNullOrWhiteSpace(dbContext))
            args.Append($" --context \"{dbContext}\"");

        if (!string.IsNullOrWhiteSpace(connectionString))
            args.Append($" --connection \"{connectionString}\"");

        _logger.LogInformation("Executing dotnet {Args}...", args);
        var execResult = await RunDotnetProcessAsync(args.ToString(), Path.GetDirectoryName(projectPath), cancellationToken);

        result.OutputLogs = execResult.Logs;
        result.Success = execResult.Success;
        result.Message = execResult.Success
            ? "Successfully applied EF Core database migrations."
            : $"Failed to apply EF Core database migrations: {execResult.Error}";

        return result;
    }

    public async Task<EfListMigrationsResult> ListMigrationsAsync(
        string projectPath,
        string? startupProjectPath = null,
        string? dbContext = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var result = new EfListMigrationsResult();

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            result.Success = false;
            result.Message = "ProjectPath is required.";
            return result;
        }

        var args = new StringBuilder("ef migrations list");
        args.Append($" --project \"{projectPath}\"");

        if (!string.IsNullOrWhiteSpace(startupProjectPath))
            args.Append($" --startup-project \"{startupProjectPath}\"");

        if (!string.IsNullOrWhiteSpace(dbContext))
            args.Append($" --context \"{dbContext}\"");

        if (!string.IsNullOrWhiteSpace(connectionString))
            args.Append($" --connection \"{connectionString}\"");

        _logger.LogInformation("Executing dotnet {Args}...", args);
        var execResult = await RunDotnetProcessAsync(args.ToString(), Path.GetDirectoryName(projectPath), cancellationToken);

        result.OutputLogs = execResult.Logs;
        result.Success = execResult.Success;

        if (execResult.Success)
        {
            foreach (var line in execResult.Logs)
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith("(Pending)", StringComparison.OrdinalIgnoreCase))
                {
                    result.PendingMigrations.Add(trimmed.Replace("(Pending)", "").Trim());
                }
                else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("Build") && !trimmed.StartsWith("info:"))
                {
                    result.AppliedMigrations.Add(trimmed);
                }
            }
            result.Message = $"Found {result.AppliedMigrations.Count} applied and {result.PendingMigrations.Count} pending migrations.";
        }
        else
        {
            result.Message = $"Failed to list EF Core migrations: {execResult.Error}";
        }

        return result;
    }

    private async Task<(bool Success, int ExitCode, string Output, string Error, List<string> Logs)> RunDotnetProcessAsync(
        string arguments,
        string? workingDir,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var output = new StringBuilder();
        var error = new StringBuilder();
        var logs = new List<string>();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
                logs.Add(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                error.AppendLine(e.Data);
                logs.Add($"ERROR: {e.Data}");
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode == 0, process.ExitCode, output.ToString(), error.ToString(), logs);
        }
        catch (Exception ex)
        {
            return (false, -1, output.ToString(), ex.Message, logs);
        }
    }
}
