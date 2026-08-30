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

                // Match PropertyGroups regardless of XML namespace
                var propertyGroups = root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup").ToList();
                string projectTypeGuids = string.Empty;
                foreach (var pg in propertyGroups)
                {
                    var tf = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
                    if (!string.IsNullOrWhiteSpace(tf))
                        result.TargetFramework = tf;

                    var tfv = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworkVersion")?.Value;
                    if (!string.IsNullOrWhiteSpace(tfv))
                    {
                        result.TargetFramework = tfv;
                        result.IsNetFramework = true;
                    }

                    var ot = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value;
                    if (!string.IsNullOrWhiteSpace(ot))
                        result.OutputType = ot;

                    var ptg = pg.Elements().FirstOrDefault(e => e.Name.LocalName == "ProjectTypeGuids")?.Value;
                    if (!string.IsNullOrWhiteSpace(ptg))
                        projectTypeGuids = ptg;
                }

                var itemGroups = root.Descendants().Where(e => e.Name.LocalName == "ItemGroup").ToList();
                var rawReferences = new List<string>();
                foreach (var ig in itemGroups)
                {
                    foreach (var pkg in ig.Elements().Where(e => e.Name.LocalName == "PackageReference"))
                    {
                        var name = pkg.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            result.PackageReferences.Add(name);
                    }

                    foreach (var proj in ig.Elements().Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        var name = proj.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            result.ProjectReferences.Add(name);
                    }

                    foreach (var r in ig.Elements().Where(e => e.Name.LocalName == "Reference"))
                    {
                        var name = r.Attribute("Include")?.Value;
                        if (!string.IsNullOrWhiteSpace(name))
                            rawReferences.Add(name);
                    }
                }

                // ASP.NET Core check
                result.IsAspNetCore = result.Sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                                     result.PackageReferences.Any(p => p.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));

                // Classic ASP.NET check (ProjectTypeGuids {349c5851-65df-11da-9384-00065b846f21} or System.Web)
                bool isClassicAspNet = projectTypeGuids.Contains("349c5851-65df-11da-9384-00065b846f21", StringComparison.OrdinalIgnoreCase) ||
                                      rawReferences.Any(r => r.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase) || r.Contains("Microsoft.AspNet.WebApi", StringComparison.OrdinalIgnoreCase));

                // Windows Service check (Modern or Classic)
                result.HasWindowsServiceSupport = result.PackageReferences.Any(p =>
                    p.Contains("Microsoft.Extensions.Hosting.WindowsServices", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Microsoft.Windows.Compatibility", StringComparison.OrdinalIgnoreCase)) ||
                    rawReferences.Any(r => r.StartsWith("System.ServiceProcess", StringComparison.OrdinalIgnoreCase));

                if (result.IsNetFramework)
                {
                    result.ClrVersion = "v4.0";
                }
                else
                {
                    result.ClrVersion = ""; // No Managed Code for ASP.NET Core
                }

                if (result.IsAspNetCore)
                {
                    result.DetectedDeployTarget = DeploymentTargetType.IIS;
                    result.TargetDescription = "ASP.NET Core Web/API project -> Configured for IIS (No Managed Code) / Kestrel reverse-proxy deployment.";
                }
                else if (isClassicAspNet)
                {
                    result.DetectedDeployTarget = DeploymentTargetType.IIS;
                    result.TargetDescription = "Classic ASP.NET Framework Web/API project -> Configured for IIS (.NET CLR v4.0).";
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
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to inspect project at {Path}", csprojPath);
                result.Success = false;
                result.TargetDescription = $"Exception during inspection: {ex.Message}";
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
        var inspection = await InspectProjectAsync(projectPath, cancellationToken);
        if (!inspection.Success)
        {
            return new ProjectBuildResult
            {
                Success = false,
                ErrorMessage = $"Failed to inspect project: {inspection.TargetDescription}"
            };
        }

        var targetOutputDir = outputDirectory;
        if (string.IsNullOrWhiteSpace(targetOutputDir))
        {
            var tempBase = Path.Combine(Path.GetTempPath(), "McpPublish_" + Guid.NewGuid().ToString("N")[..8]);
            targetOutputDir = Path.Combine(tempBase, inspection.ProjectName);
        }

        Directory.CreateDirectory(targetOutputDir);

        var outputLogs = new StringBuilder();
        var errorLogs = new StringBuilder();

        string executableName;
        string commandArguments;

        if (inspection.IsNetFramework)
        {
            // For .NET Framework, locate MSBuild.exe
            var msbuildPath = FindMsBuildExe();
            executableName = msbuildPath;
            commandArguments = $"\"{inspection.ProjectPath}\" /p:Configuration={configuration} /p:Platform=\"AnyCPU\" /p:OutputPath=\"{targetOutputDir}\" /t:Rebuild /verbosity:minimal";
            _logger.LogInformation("Building .NET Framework project using MSBuild at {Path}...", msbuildPath);
        }
        else
        {
            // For .NET Core / .NET 8+, use dotnet publish
            executableName = "dotnet";
            var selfContainedFlag = isSelfContained ? "--self-contained true" : "--self-contained false";
            commandArguments = $"publish \"{inspection.ProjectPath}\" -c {configuration} -r {runtime} {selfContainedFlag} -o \"{targetOutputDir}\"";
            _logger.LogInformation("Building .NET Core project using dotnet publish...");
        }

        var psi = new ProcessStartInfo
        {
            FileName = executableName,
            Arguments = commandArguments,
            WorkingDirectory = Path.GetDirectoryName(inspection.ProjectPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) outputLogs.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorLogs.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            var success = process.ExitCode == 0;
            string? zipPath = null;

            if (success && createZip)
            {
                var zipName = $"{inspection.ProjectName}_publish_{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
                zipPath = Path.Combine(Path.GetDirectoryName(targetOutputDir) ?? Path.GetTempPath(), zipName);

                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                ZipFile.CreateFromDirectory(targetOutputDir, zipPath);
                _logger.LogInformation("Created deployment archive at {ZipPath}", zipPath);
            }

            return new ProjectBuildResult
            {
                Success = success,
                Configuration = configuration,
                Runtime = runtime,
                OutputDirectory = targetOutputDir,
                ZipPackagePath = zipPath,
                OutputLogs = outputLogs.ToString(),
                ErrorMessage = success ? null : (errorLogs.Length > 0 ? errorLogs.ToString() : outputLogs.ToString())
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run build process");
            return new ProjectBuildResult
            {
                Success = false,
                Configuration = configuration,
                Runtime = runtime,
                OutputDirectory = targetOutputDir,
                OutputLogs = outputLogs.ToString(),
                ErrorMessage = $"Build execution exception: {ex.Message}"
            };
        }
    }

    private static string FindMsBuildExe()
    {
        var wellKnownPaths = new[]
        {
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
        };

        foreach (var path in wellKnownPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return "msbuild.exe";
    }

    private static string? FindCsproj(string path)
    {
        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return path;

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
            if (files.Length > 0) return files[0];

            var slnFiles = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                var allCsproj = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories);
                if (allCsproj.Length > 0) return allCsproj[0];
            }
        }

        return null;
    }
}
