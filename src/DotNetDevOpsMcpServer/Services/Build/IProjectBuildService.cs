namespace DotNetDevOpsMcpServer.Services.Build;

public enum DeploymentTargetType
{
    Unknown,
    IIS,
    WindowsService,
    ConsoleApplication
}

public class ProjectInspectionResult
{
    public bool Success { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Sdk { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string OutputType { get; set; } = string.Empty;
    public DeploymentTargetType DetectedDeployTarget { get; set; }
    public string TargetDescription { get; set; } = string.Empty;
    public bool IsAspNetCore { get; set; }
    public bool IsNetFramework { get; set; }
    public string ClrVersion { get; set; } = string.Empty;
    public bool HasWindowsServiceSupport { get; set; }
    public List<string> PackageReferences { get; set; } = new();
    public List<string> ProjectReferences { get; set; } = new();
}

public class ProjectBuildResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string? ZipPackagePath { get; set; }
    public string Configuration { get; set; } = "Release";
    public string Runtime { get; set; } = "win-x64";
    public string OutputLogs { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

public interface IProjectBuildService
{
    Task<ProjectInspectionResult> InspectProjectAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default);
    Task<ProjectBuildResult> BuildAndPublishAsync(
        string projectPath,
        string configuration = "Release",
        string runtime = "win-x64",
        string? outputDirectory = null,
        bool isSelfContained = false,
        bool createZip = true,
        CancellationToken cancellationToken = default);
}
