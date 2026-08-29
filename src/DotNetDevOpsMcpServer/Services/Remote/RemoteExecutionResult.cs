namespace DotNetDevOpsMcpServer.Services.Remote;

public class RemoteConnectionConfig
{
    public string Host { get; set; } = "localhost";
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string Protocol { get; set; } = "Auto"; // "Auto", "WinRM", "SSH", "Local"
    public bool UseSsl { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 120;

    public bool IsLocal =>
        string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Host, ".", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}

public class RemoteExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public string Summary => Success ? "Execution succeeded." : $"Execution failed with code {ExitCode}: {StandardError}";
}
