using System.Text.Json;
using DotNetDevOpsMcpServer.Protocol;
using DotNetDevOpsMcpServer.Services.Build;
using DotNetDevOpsMcpServer.Services.Database;
using DotNetDevOpsMcpServer.Services.Remote;
using DotNetDevOpsMcpServer.Tools;
using DotNetDevOpsMcpServer.Tools.Build;
using DotNetDevOpsMcpServer.Tools.Database;
using DotNetDevOpsMcpServer.Tools.Server;
using DotNetDevOpsMcpServer.Transport;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Determine Transport Mode: default to stdio if not explicitly SSE
var transportArg = args.FirstOrDefault(a => a.StartsWith("--transport=", StringComparison.OrdinalIgnoreCase))?.Split('=')[1]
                   ?? (args.Contains("--sse", StringComparer.OrdinalIgnoreCase) ? "sse" : "stdio");

var portArg = args.FirstOrDefault(a => a.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))?.Split('=')[1]
              ?? "5000";

if (transportArg.Equals("stdio", StringComparison.OrdinalIgnoreCase))
{
    // In stdio mode, standard output MUST only contain MCP JSON-RPC messages.
    // Configure logging to write exclusively to standard error.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}
else
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Information);
}

// Register Services
builder.Services.AddSingleton<IDatabaseService, SqlDacFxService>();
builder.Services.AddSingleton<IProjectBuildService, ProjectBuildService>();

// Register Remote Executors
builder.Services.AddSingleton<LocalExecutor>();
builder.Services.AddSingleton<WinRmExecutor>();
builder.Services.AddSingleton<SshExecutor>();
builder.Services.AddSingleton<RemoteExecutorFactory>();

// Register Tools
builder.Services.AddSingleton<IDevOpsTool, DbCompareSchemasTool>();
builder.Services.AddSingleton<IDevOpsTool, DbGenerateScriptTool>();
builder.Services.AddSingleton<IDevOpsTool, DbApplyMigrationTool>();
builder.Services.AddSingleton<IDevOpsTool, DotnetInspectProjectTool>();
builder.Services.AddSingleton<IDevOpsTool, DotnetBuildAndPublishTool>();
builder.Services.AddSingleton<IDevOpsTool, ServerTestConnectionTool>();

// Self-register concrete tools for composite tool injection
builder.Services.AddSingleton<ServerDeployIisTool>();
builder.Services.AddSingleton<IDevOpsTool>(sp => sp.GetRequiredService<ServerDeployIisTool>());

builder.Services.AddSingleton<ServerDeployWindowsServiceTool>();
builder.Services.AddSingleton<IDevOpsTool>(sp => sp.GetRequiredService<ServerDeployWindowsServiceTool>());

builder.Services.AddSingleton<IDevOpsTool, ServerConfigureFirewallTool>();
builder.Services.AddSingleton<IDevOpsTool, ServerAutoDeployTool>();

// Register MCP Protocol Engine & Stdio Transport
builder.Services.AddSingleton<McpServerEngine>();
builder.Services.AddSingleton<StdioTransport>();

if (transportArg.Equals("stdio", StringComparison.OrdinalIgnoreCase))
{
    var app = builder.Build();
    var transport = app.Services.GetRequiredService<StdioTransport>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await transport.RunAsync(cts.Token);
    return;
}

// SSE Transport Hosting
builder.WebHost.UseUrls($"http://*:{portArg}");
var webApp = builder.Build();

var sseClients = new System.Collections.Concurrent.ConcurrentDictionary<string, HttpResponse>();

webApp.MapGet("/sse", async (HttpContext context, [FromServices] ILogger<Program> logger) =>
{
    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var sessionId = Guid.NewGuid().ToString("N");
    sseClients[sessionId] = context.Response;
    logger.LogInformation("New SSE client connected: {SessionId}", sessionId);

    // Send endpoint event according to MCP SSE specification
    var endpointMessage = $"event: endpoint\ndata: /messages?sessionId={sessionId}\n\n";
    await context.Response.WriteAsync(endpointMessage);
    await context.Response.Body.FlushAsync();

    var tcs = new TaskCompletionSource();
    context.RequestAborted.Register(() =>
    {
        sseClients.TryRemove(sessionId, out _);
        logger.LogInformation("SSE client disconnected: {SessionId}", sessionId);
        tcs.TrySetResult();
    });

    await tcs.Task;
});

webApp.MapPost("/messages", async (
    HttpContext context,
    [FromQuery] string? sessionId,
    [FromServices] McpServerEngine engine,
    [FromServices] ILogger<Program> logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();

    logger.LogDebug("Received SSE message: {Body}", body);
    var response = await engine.HandleMessageAsync(body, context.RequestAborted);

    if (response == null)
    {
        return Results.Accepted();
    }

    return Results.Json(response);
});

webApp.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    server = "dotnet-devops-mcp",
    version = "1.0.0",
    protocolVersion = "2024-11-05"
}));

Console.WriteLine($"Starting .NET DevOps MCP Server on http://localhost:{portArg} (SSE Mode)...");
await webApp.RunAsync();
