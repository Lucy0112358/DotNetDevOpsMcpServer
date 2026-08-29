using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Build;

namespace DotNetDevOpsMcpServer.Tools.Build;

public class DotnetInspectProjectTool : IDevOpsTool
{
    private readonly IProjectBuildService _buildService;

    public string Name => "dotnet_inspect_project";
    public string Description => "Inspects a .NET project (.csproj or .sln) to detect Target Framework, Sdk type, dependencies, and whether it is an ASP.NET Core Web Application (for IIS) or a Background Worker Service (for Windows Services).";

    public McpJsonSchema InputSchema => new()
    {
        Type = "object",
        Properties = new Dictionary<string, McpPropertySchema>
        {
            ["projectOrSolutionPath"] = new()
            {
                Type = "string",
                Description = "Path to the .csproj, .sln, or project directory to inspect."
            }
        },
        Required = new List<string> { "projectOrSolutionPath" }
    };

    public DotnetInspectProjectTool(IProjectBuildService buildService)
    {
        _buildService = buildService;
    }

    public async Task<McpCallToolResult> ExecuteAsync(Dictionary<string, JsonElement>? arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments.GetString("projectOrSolutionPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            return McpCallToolResult.Error("'projectOrSolutionPath' is required.");
        }

        try
        {
            var inspection = await _buildService.InspectProjectAsync(path, cancellationToken);

            var responseJson = JsonSerializer.Serialize(new
            {
                status = inspection.Success ? "success" : "failure",
                projectName = inspection.ProjectName,
                projectPath = inspection.ProjectPath,
                sdk = inspection.Sdk,
                targetFramework = inspection.TargetFramework,
                outputType = inspection.OutputType,
                detectedDeployTarget = inspection.DetectedDeployTarget.ToString(),
                targetDescription = inspection.TargetDescription,
                isAspNetCore = inspection.IsAspNetCore,
                hasWindowsServiceSupport = inspection.HasWindowsServiceSupport,
                packageReferences = inspection.PackageReferences,
                projectReferences = inspection.ProjectReferences
            }, new JsonSerializerOptions { WriteIndented = true });

            return inspection.Success
                ? McpCallToolResult.Text(responseJson)
                : McpCallToolResult.Error(responseJson);
        }
        catch (Exception ex)
        {
            return McpCallToolResult.Error($"Project inspection failed: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
