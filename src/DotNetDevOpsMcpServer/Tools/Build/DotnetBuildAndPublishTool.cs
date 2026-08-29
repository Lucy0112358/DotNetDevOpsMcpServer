using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Build;

namespace DotNetDevOpsMcpServer.Tools.Build;

public class DotnetBuildAndPublishTool : IDevOpsTool
{
    private readonly IProjectBuildService _buildService;

    public string Name => "dotnet_build_and_publish";
    public string Description => "Compiles and publishes a .NET project using `dotnet publish`, targeting a specific runtime (e.g. win-x64), configuration (Release), and optionally packaging the output into a .zip archive ready for deployment.";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["projectPath"] = new()
            {
                Type = "string",
                Description = "Path to the .csproj or project directory to publish."
            },
            ["configuration"] = new()
            {
                Type = "string",
                Description = "Build configuration, e.g. Release or Debug (default: Release).",
                Default = "Release"
            },
            ["runtime"] = new()
            {
                Type = "string",
                Description = "Target runtime identifier, e.g. win-x64, win-x86, linux-x64 (default: win-x64).",
                Default = "win-x64"
            },
            ["outputDirectory"] = new()
            {
                Type = "string",
                Description = "Optional custom output directory for published files."
            },
            ["isSelfContained"] = new()
            {
                Type = "boolean",
                Description = "Whether to publish as self-contained executable (includes .NET runtime) (default: false).",
                Default = false
            },
            ["createZip"] = new()
            {
                Type = "boolean",
                Description = "Whether to generate a compressed .zip archive of published files (default: true).",
                Default = true
            }
        },
        Required = new List<string> { "projectPath" }
    };

    public DotnetBuildAndPublishTool(IProjectBuildService buildService)
    {
        _buildService = buildService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments.GetString("projectPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            return McpCallToolResult.Error("'projectPath' is required.");
        }

        var configuration = arguments.GetString("configuration", "Release")!;
        var runtime = arguments.GetString("runtime", "win-x64")!;
        var outputDir = arguments.GetString("outputDirectory");
        var isSelfContained = arguments.GetBool("isSelfContained", false);
        var createZip = arguments.GetBool("createZip", true);

        try
        {
            var buildResult = await _buildService.BuildAndPublishAsync(
                path,
                configuration,
                runtime,
                outputDir,
                isSelfContained,
                createZip,
                cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = buildResult.Success ? "success" : "failure",
                configuration = buildResult.Configuration,
                runtime = buildResult.Runtime,
                outputDirectory = buildResult.OutputDirectory,
                zipPackagePath = buildResult.ZipPackagePath,
                errorMessage = buildResult.ErrorMessage,
                logsSnippet = buildResult.OutputLogs.Length > 2000
                    ? buildResult.OutputLogs[..2000] + "\n... [truncated]"
                    : buildResult.OutputLogs
            }, new JsonSerializerOptions { WriteIndented = true });

            return buildResult.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"dotnet build & publish failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
