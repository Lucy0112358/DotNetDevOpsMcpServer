using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Build;
using DotNetDevOpsMcpServer.Services.Database;
using DotNetDevOpsMcpServer.Services.Remote;
using DotNetDevOpsMcpServer.Tools;
using DotNetDevOpsMcpServer.Tools.Build;
using DotNetDevOpsMcpServer.Tools.Database;
using DotNetDevOpsMcpServer.Tools.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotNetDevOpsMcpServer.Tests;

public class McpEngineTests
{
    private readonly McpServerEngine _engine;

    public McpEngineTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDatabaseService, SqlDacFxService>();
        services.AddSingleton<IEfCoreMigrationService, EfCoreMigrationService>();
        services.AddSingleton<IProjectBuildService, ProjectBuildService>();
        services.AddSingleton<LocalExecutor>();
        services.AddSingleton<WinRmExecutor>();
        services.AddSingleton<SshExecutor>();
        services.AddSingleton<RemoteExecutorFactory>();

        services.AddSingleton<IDevOpsTool, DbCompareSchemasTool>();
        services.AddSingleton<IDevOpsTool, DbGenerateScriptTool>();
        services.AddSingleton<IDevOpsTool, DbApplyMigrationTool>();
        services.AddSingleton<IDevOpsTool, EfGenerateMigrationScriptTool>();
        services.AddSingleton<IDevOpsTool, EfDatabaseUpdateTool>();
        services.AddSingleton<IDevOpsTool, EfListMigrationsTool>();
        services.AddSingleton<IDevOpsTool, DotnetInspectProjectTool>();
        services.AddSingleton<IDevOpsTool, DotnetBuildAndPublishTool>();
        services.AddSingleton<IDevOpsTool, ServerTestConnectionTool>();

        services.AddSingleton<ServerDeployIisTool>();
        services.AddSingleton<IDevOpsTool>(sp => sp.GetRequiredService<ServerDeployIisTool>());

        services.AddSingleton<ServerDeployWindowsServiceTool>();
        services.AddSingleton<IDevOpsTool>(sp => sp.GetRequiredService<ServerDeployWindowsServiceTool>());

        services.AddSingleton<IDevOpsTool, ServerConfigureFirewallTool>();
        services.AddSingleton<IDevOpsTool, ServerAutoDeployTool>();
        services.AddSingleton<IDevOpsTool, ServerUndeployIisTool>();
        services.AddSingleton<IDevOpsTool, ServerUndeployWindowsServiceTool>();

        var sp = services.BuildServiceProvider();
        var tools = sp.GetServices<IDevOpsTool>();
        _engine = new McpServerEngine(tools, NullLogger<McpServerEngine>.Instance);
    }

    [Fact]
    public async Task HandleMessage_Initialize_ReturnsMcpCapabilities()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        var response = await _engine.HandleMessageAsync(request);

        Assert.NotNull(response);
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("dotnet-devops-mcp", json);
        Assert.Contains("2024-11-05", json);
    }

    [Fact]
    public async Task HandleMessage_Ping_ReturnsEmptyObject()
    {
        var request = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";
        var response = await _engine.HandleMessageAsync(request);

        Assert.NotNull(response);
        Assert.Null(response.Error);
    }

    [Fact]
    public async Task HandleMessage_ToolsList_ReturnsAllRegisteredTools()
    {
        var request = """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""";
        var response = await _engine.HandleMessageAsync(request);

        Assert.NotNull(response);
        Assert.Null(response.Error);

        var listResult = response.Result as McpListToolsResult;
        Assert.NotNull(listResult);
        Assert.Equal(15, listResult.Tools.Count);

        var toolNames = listResult.Tools.Select(t => t.Name).ToList();
        Assert.Contains("db_compare_schemas", toolNames);
        Assert.Contains("db_generate_migration_script", toolNames);
        Assert.Contains("db_apply_migration", toolNames);
        Assert.Contains("ef_generate_migration_script", toolNames);
        Assert.Contains("ef_database_update", toolNames);
        Assert.Contains("ef_list_migrations", toolNames);
        Assert.Contains("dotnet_inspect_project", toolNames);
        Assert.Contains("dotnet_build_and_publish", toolNames);
        Assert.Contains("server_test_connection", toolNames);
        Assert.Contains("server_deploy_iis", toolNames);
        Assert.Contains("server_deploy_windows_service", toolNames);
        Assert.Contains("server_configure_firewall", toolNames);
        Assert.Contains("server_auto_deploy", toolNames);
        Assert.Contains("server_undeploy_iis", toolNames);
        Assert.Contains("server_undeploy_windows_service", toolNames);
    }

    [Fact]
    public async Task HandleMessage_ToolsCall_UnknownTool_ReturnsError()
    {
        var request = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"non_existent_tool","arguments":{}}}""";
        var response = await _engine.HandleMessageAsync(request);

        Assert.NotNull(response);
        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error.Code);
    }
}
