namespace SshManager.Core.Models;

public enum TerminalMode
{
    WindowsTerminalTab,
    WindowsTerminalWindow,
    ConsoleWindow,
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
    public string? SshPath { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int ServerAliveInterval { get; set; } = 30;
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 740;
    /// <summary>User-resized DataGrid column widths by grid name (all columns except the last, which fills).</summary>
    public Dictionary<string, double[]> ColumnWidths { get; set; } = [];

    /// <summary>"ru" / "en"; null = Windows UI language.</summary>
    public string? Language { get; set; }
    /// <summary>Default availability check interval in minutes (0 = off); servers can override it.</summary>
    public int MonitorIntervalMinutes { get; set; } = 10;
    public bool NotifyOnServerDown { get; set; } = true;
    /// <summary>Look up server location via an online GeoIP service.</summary>
    public bool GeoIpEnabled { get; set; } = true;
    /// <summary>Collect OS / containers / services / forwards in the background.</summary>
    public bool AutoInventory { get; set; } = true;
    /// <summary>Log in after each successful check to read CPU / memory / disk usage.</summary>
    public bool CollectMetrics { get; set; } = true;
    /// <summary>Group paths collapsed in the server tree.</summary>
    public List<string> CollapsedGroups { get; set; } = [];
    public BackupSettings Backup { get; set; } = new();
}
