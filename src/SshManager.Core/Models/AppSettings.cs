namespace SshManager.Core.Models;

public enum TerminalMode
{
    WindowsTerminalTab,
    WindowsTerminalWindow,
    ConsoleWindow,
    /// <summary>A tab inside the SSH Manager window (xterm.js + SSH.NET).</summary>
    BuiltIn,
}

public enum BackupTarget
{
    Folder,
    Ssh,
}

public sealed class BackupSettings
{
    public BackupTarget Target { get; set; } = BackupTarget.Folder;
    public string? Folder { get; set; }
    public Guid? ServerId { get; set; }
    /// <summary>Remote directory; relative paths are relative to the SFTP home directory.</summary>
    public string RemotePath { get; set; } = "sshmanager-backups";
    /// <summary>How many archives to keep at the destination (0 = all).</summary>
    public int Keep { get; set; } = 10;
    /// <summary>Automatic backup every N hours while unlocked (0 = off).</summary>
    public int AutoHours { get; set; }
    public DateTime? LastBackup { get; set; }
    public string? LastResult { get; set; }
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
    /// <summary>Built-in terminal: load the shell integration (prompt marks, cwd, "edit") for bash / zsh.</summary>
    public bool TerminalIntegration { get; set; } = true;
    /// <summary>Built-in terminal: show suggestions while typing (otherwise Ctrl+Space).</summary>
    public bool TerminalAutoSuggest { get; set; } = true;
    /// <summary>File manager: last local folder, last remote folder per server id.</summary>
    public string? FilesLocalDir { get; set; }
    public Dictionary<string, string> FilesRemoteDirs { get; set; } = [];
    public string? SshPath { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>System-wide shortcut that shows / hides the main window ("Win+Alt+X"); empty = off.</summary>
    public string? GlobalHotkey { get; set; } = "Win+Alt+X";
    public int ServerAliveInterval { get; set; } = 30;
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 740;
    /// <summary>User-resized DataGrid column widths by grid name (all columns except the last, which fills).</summary>
    public Dictionary<string, double[]> ColumnWidths { get; set; } = [];

    /// <summary>"ru" / "en"; null = Windows UI language.</summary>
    public string? Language { get; set; }
    /// <summary>Default availability check interval in minutes (0 = off); servers can override it.</summary>
    public int MonitorIntervalMinutes { get; set; } = 5;
    /// <summary>Format of this file; older files are migrated on load.</summary>
    public int SettingsVersion { get; set; }
    public bool NotifyOnServerDown { get; set; } = true;
    /// <summary>Look for a new version on GitHub Releases once a day.</summary>
    public bool CheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The version the tray already told about.</summary>
    public string? NotifiedUpdate { get; set; }
    /// <summary>Look up server location via an online GeoIP service.</summary>
    public bool GeoIpEnabled { get; set; } = true;
    /// <summary>Collect OS / containers / services / forwards in the background.</summary>
    public bool AutoInventory { get; set; } = true;
    /// <summary>Log in after each successful check to read CPU / memory / disk usage.</summary>
    public bool CollectMetrics { get; set; } = true;
    /// <summary>Group paths collapsed in the server tree.</summary>
    public List<string> CollapsedGroups { get; set; } = [];
    public BackupSettings Backup { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
}

/// <summary>AI agents (Claude Code, Codex…) managing servers over MCP. What each server allows is set on the server.</summary>
public sealed class McpSettings
{
    public const int DefaultPort = 47821;

    /// <summary>Master switch: off = every tool call is refused.</summary>
    public bool Enabled { get; set; }
    /// <summary>Streamable HTTP on 127.0.0.1 (besides stdio through sshm.exe mcp).</summary>
    public bool HttpEnabled { get; set; }
    public int HttpPort { get; set; } = DefaultPort;
    /// <summary>Bearer token for HTTP; made on first use.</summary>
    public string? HttpToken { get; set; }
    /// <summary>Ask in the app before a reboot or a deletion.</summary>
    public bool ConfirmDangerous { get; set; } = true;
    /// <summary>Agent log per server: size limit (MB, all files together) and how long files are kept (days).</summary>
    public int LogMaxMb { get; set; } = 10;
    public int LogRetentionDays { get; set; } = 30;
}
