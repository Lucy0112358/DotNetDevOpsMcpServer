using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Build;
using DotNetDevOpsMcpServer.Services.Database;
using DotNetDevOpsMcpServer.Tools.Database;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Tools.Server;

public class ServerAutoDeployTool : IDevOpsTool
{
    private readonly IProjectBuildService _buildService;
    private readonly IDatabaseService _databaseService;
    private readonly ServerDeployIisTool _iisTool;
    private readonly ServerDeployWindowsServiceTool _serviceTool;
    private readonly ILogger<ServerAutoDeployTool> _logger;

    public string Name => "server_auto_deploy";
    public string Description => "End-to-end all-in-one DevOps pipeline: Auto-detects project type, builds/publishes .NET application, compares & generates DB migration script, deploys to IIS or Windows Service, and opens firewall ports.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["projectPath"] = new()
            {
                Type = "string",
                Description = "Path to .NET project (.csproj, .sln, or folder)."
            },
            ["serverHost"] = new()
            {
                Type = "string",
                Description = "Target Windows Server host/IP (or 'localhost')."
            },
            ["username"] = new()
            {
                Type = "string",
                Description = "Remote Administrator username."
            },
            ["password"] = new()
            {
                Type = "string",
                Description = "Remote Administrator password."
            },
            ["sourceDbConnectionString"] = new()
            {
                Type = "string",
                Description = "Optional: Source SQL Server connection string (e.g. Dev DB) for schema comparison."
            },
            ["targetDbConnectionString"] = new()
            {
                Type = "string",
                Description = "Optional: Target SQL Server connection string (e.g. Prod DB) to generate/apply sync script against."
            },
            ["autoApplyDbMigration"] = new()
            {
                Type = "boolean",
                Description = "Whether to immediately apply the generated DB migration script (default: false - generates script for review).",
                Default = false
            },
            ["port"] = new()
            {
                Type = "number",
                Description = "Port for IIS website binding (default: 80).",
                Default = 80
            },
            ["deployTargetOverride"] = new()
            {
                Type = "string",
                Description = "Optional manual override for deploy target: 'IIS' or 'WindowsService'. If omitted, auto-detected from project.",
                Enum = new List<string> { "IIS", "WindowsService" }
            }
        },
        Required = new List<string> { "projectPath", "serverHost" }
    };

    public ServerAutoDeployTool(
        IProjectBuildService buildService,
        IDatabaseService databaseService,
        ServerDeployIisTool iisTool,
        ServerDeployWindowsServiceTool serviceTool,
        ILogger<ServerAutoDeployTool> logger)
    {
        _buildService = buildService;
        _databaseService = databaseService;
        _iisTool = iisTool;
        _serviceTool = serviceTool;
        _logger = logger;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var projectPath = arguments.GetString("projectPath")!;
        var host = arguments.GetString("serverHost", "localhost")!;
        var username = arguments.GetString("username");
        var password = arguments.GetString("password");
        var sourceDb = arguments.GetString("sourceDbConnectionString");
        var targetDb = arguments.GetString("targetDbConnectionString");
        var autoApplyDb = arguments.GetBool("autoApplyDbMigration", false);
        var port = arguments.GetInt("port", 80) ?? 80;
        var overrideTarget = arguments.GetString("deployTargetOverride");

        var stepsLog = new List<string>();

        try
        {
            // Step 1: Inspect Project
            stepsLog.Add("Step 1: Inspecting project structure...");
            var inspection = await _buildService.InspectProjectAsync(projectPath, cancellationToken);
            if (!inspection.Success)
            {
                return McpCallToolResult.Error($"Project inspection failed: {inspection.TargetDescription}");
            }
            stepsLog.Add($"-> Detected project '{inspection.ProjectName}' with target: {inspection.DetectedDeployTarget}");

            // Step 2: Database Schema Sync (if DB connection strings provided)
            string? migrationScriptPath = null;
            if (!string.IsNullOrWhiteSpace(sourceDb) && !string.IsNullOrWhiteSpace(targetDb))
            {
                stepsLog.Add("Step 2: Performing DB-First DacFx Schema Comparison & Script Generation...");
                var scriptResult = await _databaseService.GenerateMigrationScriptAsync(sourceDb, targetDb, null, true, false, cancellationToken);
                migrationScriptPath = scriptResult.ScriptPath;
                stepsLog.Add($"-> Migration script generated at: {migrationScriptPath}");

                if (autoApplyDb)
                {
                    stepsLog.Add("-> Auto-applying DB migration script to target DB...");
                    var applyResult = await _databaseService.ApplyMigrationAsync(targetDb, scriptResult.ScriptContent, null, cancellationToken);
                    stepsLog.Add($"-> DB Migration applied: {applyResult.Message}");
                }
                else
                {
                    stepsLog.Add("-> DB migration script ready for review (autoApplyDbMigration = false).");
                }
            }
            else
            {
                stepsLog.Add("Step 2: Skipping DB sync (no database connection strings provided).");
            }

            // Step 3: Build & Publish Application
            stepsLog.Add("Step 3: Compiling and publishing .NET project...");
            var buildResult = await _buildService.BuildAndPublishAsync(projectPath, "Release", "win-x64", null, false, true, cancellationToken);
            if (!buildResult.Success || string.IsNullOrEmpty(buildResult.ZipPackagePath))
            {
                return McpCallToolResult.Error($"Build & publish failed: {buildResult.ErrorMessage}\n{buildResult.OutputLogs}");
            }
            stepsLog.Add($"-> Published package created at: {buildResult.ZipPackagePath}");

            // Step 4: Determine Deploy Target
            var targetType = DeploymentTargetType.Unknown;
            if (!string.IsNullOrWhiteSpace(overrideTarget))
            {
                Enum.TryParse(overrideTarget, true, out targetType);
            }
            if (targetType == DeploymentTargetType.Unknown)
            {
                targetType = inspection.DetectedDeployTarget;
            }

            // Step 5: Execute Deployment
            stepsLog.Add($"Step 4: Deploying package to host '{host}' as {targetType}...");
            McpCallToolResult deployResult;

            if (targetType == DeploymentTargetType.IIS)
            {
                var iisArgs = new Dictionary<string, JsonElement>
                {
                    ["host"] = JsonSerializer.SerializeToElement(host),
                    ["siteName"] = JsonSerializer.SerializeToElement(inspection.ProjectName),
                    ["port"] = JsonSerializer.SerializeToElement(port),
                    ["packagePath"] = JsonSerializer.SerializeToElement(buildResult.ZipPackagePath),
                    ["openFirewallPort"] = JsonSerializer.SerializeToElement(true)
                };
                if (!string.IsNullOrEmpty(username)) iisArgs["username"] = JsonSerializer.SerializeToElement(username);
                if (!string.IsNullOrEmpty(password)) iisArgs["password"] = JsonSerializer.SerializeToElement(password);

                deployResult = await _iisTool.ExecuteAsync(iisArgs, cancellationToken);
            }
            else
            {
                var serviceArgs = new Dictionary<string, JsonElement>
                {
                    ["host"] = JsonSerializer.SerializeToElement(host),
                    ["serviceName"] = JsonSerializer.SerializeToElement(inspection.ProjectName),
                    ["packagePath"] = JsonSerializer.SerializeToElement(buildResult.ZipPackagePath),
                    ["startupType"] = JsonSerializer.SerializeToElement("Automatic")
                };
                if (!string.IsNullOrEmpty(username)) serviceArgs["username"] = JsonSerializer.SerializeToElement(username);
                if (!string.IsNullOrEmpty(password)) serviceArgs["password"] = JsonSerializer.SerializeToElement(password);

                deployResult = await _serviceTool.ExecuteAsync(serviceArgs, cancellationToken);
            }

            stepsLog.Add(deployResult.IsError ? "-> Deployment encountered issues." : "-> Deployment completed successfully.");

            var finalResult = new
            {
                status = deployResult.IsError ? "partial_success_or_failure" : "success",
                project = inspection.ProjectName,
                target = targetType.ToString(),
                host,
                migrationScript = migrationScriptPath,
                package = buildResult.ZipPackagePath,
                steps = stepsLog,
                deploymentOutput = deployResult.Content.FirstOrDefault()?.Text
            };

            return McpCallToolResult.Text(JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true }), deployResult.IsError);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Auto deploy pipeline failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
