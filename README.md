# .NET DevOps & Database Synchronization MCP Server

A Model Context Protocol (MCP) server written in **C# (.NET 8 LTS)** that automates .NET project builds, **DB-First SQL Server Schema Compare & Migration Scripting** via Microsoft DacFx (`Microsoft.SqlServer.DacFx`), and remote **Windows Server Deployments** (IIS / Windows Services / Firewall) over WinRM, SSH, or Local execution.

---

## Key Features

1. **DB-First Schema Compare & Migration Scripting**:
   - Uses native `Microsoft.SqlServer.DacFx` (the exact engine powering Visual Studio Schema Compare and `SqlPackage`).
   - Compares Dev vs Staging/Prod database schemas using SQL Authentication.
   - Generates safe, transactional T-SQL delta migration scripts for review before applying.
   - Applies migrations directly with execution batching.

2. **Smart .NET Project Inspection & Build**:
   - Automatically detects whether a `.csproj` is an **ASP.NET Core Web Application** (targeted for IIS) or a **Background Worker** (targeted for Windows Service).
   - Executes `dotnet publish` for specified runtimes (e.g. `win-x64`) and packages artifacts into compressed `.zip` archives.

3. **Remote Windows Server Automation**:
   - Deploys ASP.NET Core apps to **IIS** (stops AppPool/Site, backups existing binaries, configures `No Managed Code`, starts site, and tests health).
   - Deploys Worker Services as managed **Windows Services** (`New-Service` / `sc.exe`).
   - Idempotently creates or updates **Windows Firewall** rules to open required application ports.
   - Supports **WinRM (PowerShell WS-Man)**, **SSH (OpenSSH)**, and **Local execution**.

4. **Dual Transport Support**:
   - **`stdio`**: For direct integration with IDEs (Antigravity, Claude Desktop, VS Code, Cursor).
   - **`SSE` (Server-Sent Events)**: For remote agents and HTTP-based MCP clients.

---

## Exposed MCP Tools

| Tool | Description |
| :--- | :--- |
| `db_compare_schemas` | Compares two SQL Server schemas and outputs a structural diff report (tables, views, SPs, indexes). |
| `db_generate_migration_script` | Generates a transactional T-SQL schema migration script to evolve target DB to match source DB. |
| `db_apply_migration` | Applies a migration script (or file) against the target database. |
| `dotnet_inspect_project` | Analyzes `.csproj` / `.sln` to detect framework, dependencies, and whether it's an IIS Web App or Windows Service. |
| `dotnet_build_and_publish` | Publishes the project via `dotnet publish` and packages a ready-to-deploy `.zip`. |
| `server_test_connection` | Tests remote management connectivity (WinRM / SSH / Local) and retrieves OS details. |
| `server_deploy_iis` | Deploys Web App to IIS, managing AppPool, Website, backups, and firewall rules. |
| `server_deploy_windows_service` | Deploys Worker Service to Windows Services, managing service registration and state. |
| `server_configure_firewall` | Opens or updates inbound/outbound Windows Firewall rules. |
| `server_auto_deploy` | Complete pipeline: Auto-detects project $\to$ builds $\to$ generates DB diff $\to$ deploys to IIS/Service $\to$ opens firewall. |

---

## Installation & Setup

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher.
- Access to SQL Server instance(s) with SQL Authentication.
- (Optional) Remote Windows Server with WinRM (Port 5985/5986) or OpenSSH enabled.

### Build the Project
```bash
cd DotNetDevOpsMcpServer
dotnet build DotNetDevOpsMcpServer.sln -c Release
```

The compiled binary will be located at:
`DotNetDevOpsMcpServer/src/DotNetDevOpsMcpServer/bin/Release/net8.0/DotNetDevOpsMcpServer.dll` (or `.exe` on Windows).

---

## Configuring with MCP Clients

### 1. Antigravity / Claude Desktop / Cursor (`stdio` Mode)

Add the following to your MCP configuration file (e.g. `mcpSettings.json` or `claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "dotnet-devops": {
      "command": "dotnet",
      "args": [
        "C:\\Users\\baghd\\.gemini\antigravity\\scratch\\DotNetDevOpsMcpServer\\src\\DotNetDevOpsMcpServer\\bin\\Release\\net8.0\\DotNetDevOpsMcpServer.dll",
        "--transport=stdio"
      ]
    }
  }
}
```

Or run the precompiled executable directly:
```json
{
  "mcpServers": {
    "dotnet-devops": {
      "command": "C:\\Users\\baghd\\.gemini\\antigravity\\scratch\\DotNetDevOpsMcpServer\\src\\DotNetDevOpsMcpServer\\bin\\Release\\net8.0\\DotNetDevOpsMcpServer.exe",
      "args": ["--transport=stdio"]
    }
  }
}
```

---

### 2. HTTP / SSE Mode (Server-Sent Events)

Start the server on port `5000` (or any custom port):
```bash
dotnet run --project src/DotNetDevOpsMcpServer -- --transport=sse --port=5000
```

Connect your SSE MCP client to:
- **SSE Endpoint**: `http://localhost:5000/sse`
- **Messages Endpoint**: `http://localhost:5000/messages`
- **Health Check**: `http://localhost:5000/health`

---

## Example Tool Invocations

### 1. DB-First Schema Comparison
```json
{
  "name": "db_compare_schemas",
  "arguments": {
    "sourceConnectionString": "Server=dev-sql;Database=AppDb_Dev;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "targetConnectionString": "Server=prod-sql;Database=AppDb_Prod;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "ignorePermissions": true,
    "dropObjectsNotInSource": false
  }
}
```

### 2. Generate Migration Script
```json
{
  "name": "db_generate_migration_script",
  "arguments": {
    "sourceConnectionString": "Server=dev-sql;Database=AppDb_Dev;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "targetConnectionString": "Server=prod-sql;Database=AppDb_Prod;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "outputPath": "C:\\migrations\\prod_update_v1.sql"
  }
}
```

### 3. All-In-One Auto Deployment
```json
{
  "name": "server_auto_deploy",
  "arguments": {
    "projectPath": "C:\\Projects\\MyAwesomeApi",
    "serverHost": "192.168.1.100",
    "username": "Administrator",
    "password": "ServerPassword!",
    "sourceDbConnectionString": "Server=dev-sql;Database=AppDb_Dev;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "targetDbConnectionString": "Server=prod-sql;Database=AppDb_Prod;User Id=sa;Password=Secret123!;TrustServerCertificate=True;",
    "autoApplyDbMigration": false,
    "port": 8080
  }
}
```

---

## Running Tests

Execute the automated test suite:
```bash
dotnet test DotNetDevOpsMcpServer.sln
```
