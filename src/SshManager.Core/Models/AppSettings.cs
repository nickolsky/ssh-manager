namespace SshManager.Core.Models;

public enum TerminalMode
{
    WindowsTerminalTab,
    WindowsTerminalWindow,
    ConsoleWindow,
}

/// <summary>Non-secret settings stored in data\settings.json.</summary>
public sealed class AppSettings
{
    public bool Autostart { get; set; } = true;
    /// <summary>Lock the vault after this many minutes without user input on the PC. 0 = never.</summary>
    public int AutoLockMinutes { get; set; } = 120;
    public bool LockOnWindowsLock { get; set; }
    /// <summary>Show the unlock window when an external client (ssh, git, VS Code) asks the agent while locked.</summary>
    public bool UnlockOnAgentRequest { get; set; } = true;
    public TerminalMode Terminal { get; set; } = TerminalMode.WindowsTerminalTab;
    public string? WindowsTerminalProfile { get; set; }
    public string? SshPath { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int ServerAliveInterval { get; set; } = 30;
    public double WindowWidth { get; set; } = 1000;
    public double WindowHeight { get; set; } = 640;
    /// <summary>User-resized DataGrid column widths by grid name (all columns except the last, which fills).</summary>
    public Dictionary<string, double[]> ColumnWidths { get; set; } = [];
}
