using DotNetDevOpsMcpServer.Services.Build;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotNetDevOpsMcpServer.Tests;

public class ProjectInspectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ProjectBuildService _service;

    public ProjectInspectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mcp_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new ProjectBuildService(NullLogger<ProjectBuildService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task InspectProject_WebProject_DetectsIIS()
    {
        var webCsproj = """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""";
        var projPath = Path.Combine(_tempDir, "MyWebSite.csproj");
        await File.WriteAllTextAsync(projPath, webCsproj);

        var result = await _service.InspectProjectAsync(projPath);

        Assert.True(result.Success);
        Assert.Equal("MyWebSite", result.ProjectName);
        Assert.Equal("net8.0", result.TargetFramework);
        Assert.True(result.IsAspNetCore);
        Assert.Equal(DeploymentTargetType.IIS, result.DetectedDeployTarget);
    }

    [Fact]
    public async Task InspectProject_WorkerProject_DetectsWindowsService()
    {
        var workerCsproj = """
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.0.0" />
  </ItemGroup>
</Project>
""";
        var projPath = Path.Combine(_tempDir, "MyWorkerService.csproj");
        await File.WriteAllTextAsync(projPath, workerCsproj);

        var result = await _service.InspectProjectAsync(projPath);

        Assert.True(result.Success);
        Assert.Equal("MyWorkerService", result.ProjectName);
        Assert.True(result.HasWindowsServiceSupport);
        Assert.Equal(DeploymentTargetType.WindowsService, result.DetectedDeployTarget);
    }

    [Fact]
    public async Task InspectProject_ConsoleProject_DetectsConsoleApp()
    {
        var consoleCsproj = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""";
        var projPath = Path.Combine(_tempDir, "MyCliApp.csproj");
        await File.WriteAllTextAsync(projPath, consoleCsproj);

        var result = await _service.InspectProjectAsync(projPath);

        Assert.True(result.Success);
        Assert.Equal(DeploymentTargetType.ConsoleApplication, result.DetectedDeployTarget);
    }
}
