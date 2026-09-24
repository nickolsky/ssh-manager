namespace SshManager.Core;

/// <summary>
/// Locates the data folder. It lives in the SSHManagement folder (next to SSHManagement.sln),
/// so both a dev build under src\...\bin and a published copy under SSHManagement\app share it.
/// </summary>
public static class AppPaths
{
    public const string SolutionMarker = "SSHManagement.sln";

    public static string DataDir { get; } = ResolveDataDir();
    public static string VaultFile => Path.Combine(DataDir, "vault.dat");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string KnownHostsFile => Path.Combine(DataDir, "known_hosts");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string PublicKeyDir => Path.Combine(DataDir, "pub");

    public static string ExeDir => AppContext.BaseDirectory;
    public static string HelperExe => Path.Combine(ExeDir, "sshm.exe");
    public static string MainExe => Path.Combine(ExeDir, "SshManager.exe");

    public static string AgentPipeDefault => "openssh-ssh-agent";
    public static string AgentPipeFallback => "sshmanager-agent-" + SafeUser;
    public static string ControlPipe => "sshmanager-ctl-" + SafeUser;

    private static string SafeUser =>
        new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).ToLowerInvariant();

    private static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("SSHMANAGER_DATA");
        if (!string.IsNullOrWhiteSpace(env))
            return Ensure(Path.GetFullPath(env));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionMarker)))
                return Ensure(Path.Combine(dir.FullName, "data"));
            dir = dir.Parent;
        }
        return Ensure(Path.Combine(AppContext.BaseDirectory, "data"));
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Path form that OpenSSH option parsing does not mangle (no backslash escapes).</summary>
    public static string ForSsh(string path) => path.Replace('\\', '/');
}
