using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace DotNetDevOpsMcpServer.Services.Build;

public class ProjectBuildService : IProjectBuildService
{
    private readonly ILogger<ProjectBuildService> _logger;

    public ProjectBuildService(ILogger<ProjectBuildService> logger)
    {
        _logger = logger;
    }

    public async Task<ProjectInspectionResult> InspectProjectAsync(string projectOrSolutionPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var result = new ProjectInspectionResult();

            var csprojPath = FindCsproj(projectOrSolutionPath);
            if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
            {
                result.Success = false;
                result.TargetDescription = $"No valid .csproj found at '{projectOrSolutionPath}'.";
                return result;
            }

            result.ProjectPath = Path.GetFullPath(csprojPath);
            result.ProjectName = Path.GetFileNameWithoutExtension(csprojPath);

            try
            {
                var doc = XDocument.Load(csprojPath);
                var root = doc.Root;
                if (root == null)
                {
                    result.Success = false;
                    result.TargetDescription = "Failed to parse .csproj XML content.";
                    return result;
                }

                result.Sdk = root.Attribute("Sdk")?.Value ?? string.Empty;

                var propertyGroups = root.Elements("PropertyGroup").ToList();
                foreach (var pg in propertyGroups)
                {
                    var tf = pg.Element("TargetFramework")?.Value;
                    if (!string.IsNullOrWhiteSpace(tf))
                        result.TargetFramework = tf;

                    var ot = pg.Element("OutputType")?.Value;
                    if (!string.IsNullOrWhiteSpace(ot))
                        result.OutputType = ot;
                }

                var itemGroups = root.Elements("ItemGroup").ToList();
                foreach (var ig in itemGroups)
                {
                    foreach (var pkg in ig.Elements("PackageReference"))
                    {
                        var name = pkg.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            result.PackageReferences.Add(name);
                    }

                    foreach (var proj in ig.Elements("ProjectReference"))
                    {
                        var name = proj.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            result.ProjectReferences.Add(name);
                    }
                }

                result.IsAspNetCore = result.Sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                                     result.PackageReferences.Any(p => p.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));

                result.HasWindowsServiceSupport = result.PackageReferences.Any(p =>
                    p.Contains("Microsoft.Extensions.Hosting.WindowsServices", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Microsoft.Windows.Compatibility", StringComparison.OrdinalIgnoreCase));

                if (result.IsAspNetCore)
                {
                    result.DetectedDeployTarget = DeploymentTargetType.IIS;
                    result.TargetDescription = "ASP.NET Core Web/API project -> Configured for IIS / Kestrel reverse-proxy deployment.";
                }
                else if (result.HasWindowsServiceSupport || result.Sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
                {
                    result.DetectedDeployTarget = DeploymentTargetType.WindowsService;
                    result.TargetDescription = "Worker / Background Service -> Configured for Windows Service deployment.";
                }
                else
                {
                    result.DetectedDeployTarget = DeploymentTargetType.ConsoleApplication;
                    result.TargetDescription = "Standard .NET Console Application / Library.";
                }

                result.Success = true;
                _logger.LogInformation("Inspected project {ProjectName}: Target {Target}", result.ProjectName, result.DetectedDeployTarget);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inspecting project XML");
                result.Success = false;
                result.TargetDescription = $"Error analyzing project: {ex.Message}";
                return result;
            }
        }, cancellationToken);
    }

    public async Task<ProjectBuildResult> BuildAndPublishAsync(
        string projectPath,
        string configuration = "Release",
        string runtime = "win-x64",
        string? outputDirectory = null,
        bool isSelfContained = false,
        bool createZip = true,
        CancellationToken cancellationToken = default)
    {
        var csprojPath = FindCsproj(projectPath);
        if (string.IsNullOrWhiteSpace(csprojPath) || !File.Exists(csprojPath))
        {
            return new ProjectBuildResult
            {
                Success = false,
                ErrorMessage = $"Could not locate .csproj at '{projectPath}'."
            };
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            var projDir = Path.GetDirectoryName(csprojPath) ?? AppContext.BaseDirectory;
            outputDirectory = Path.Combine(projDir, "bin", "Publish", configuration, runtime);
        }

        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory))
        {
            try { Directory.Delete(outputDirectory, true); } catch { /* ignore */ }
        }
        Directory.CreateDirectory(outputDirectory);

        var args = $"publish \"{csprojPath}\" -c {configuration} -r {runtime} --self-contained {isSelfContained.ToString().ToLowerInvariant()} -o \"{outputDirectory}\"";

        _logger.LogInformation("Executing: dotnet {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        var logs = outputBuilder.ToString() + "\n" + errorBuilder.ToString();
        var success = process.ExitCode == 0;

        string? zipPath = null;
        if (success && createZip)
        {
            var zipName = $"{Path.GetFileNameWithoutExtension(csprojPath)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";
            zipPath = Path.Combine(Path.GetDirectoryName(outputDirectory) ?? outputDirectory, zipName);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            ZipFile.CreateFromDirectory(outputDirectory, zipPath, CompressionLevel.Optimal, false);
            _logger.LogInformation("Created published zip package: {ZipPath}", zipPath);
        }

        return new ProjectBuildResult
        {
            Success = success,
            Configuration = configuration,
            Runtime = runtime,
            OutputDirectory = outputDirectory,
            ZipPackagePath = zipPath,
            OutputLogs = logs,
            ErrorMessage = success ? null : $"dotnet publish failed with exit code {process.ExitCode}. See logs."
        };
    }

    private static string? FindCsproj(string path)
    {
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Directory.Exists(path))
        {
            var csprojFiles = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
                return csprojFiles[0];

            var subCsproj = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories);
            if (subCsproj.Length > 0)
                return subCsproj[0];
        }

        if (File.Exists(path) && path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                var files = Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
        }

        return null;
    }
}
