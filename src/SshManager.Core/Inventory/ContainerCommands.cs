using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Inventory;

/// <summary>What to remove together with a container.</summary>
/// <param name="WholeProject">docker compose down for the container's compose project instead of one container.</param>
/// <param name="Volumes">Also named volumes (compose: -v; single container: its anonymous volumes).</param>
/// <param name="Image">Also the image, when no other container uses it.</param>
/// <param name="ProjectFolder">Also the compose folder with its files and bind-mounted data.</param>
public sealed record ContainerRemoval(bool WholeProject, bool Volumes, bool Image, bool ProjectFolder);

/// <summary>systemd service control; run with sudo when needed (<see cref="RemoteShell.Run"/>).</summary>
public static class ServiceCommands
{
    private static string U(ServiceInfo s) => RemoteShell.Quote(s.Unit + ".service");

    public static string Start(ServiceInfo s) => $"systemctl start {U(s)}";

    public static string Stop(ServiceInfo s) => $"systemctl stop {U(s)}";

    public static string Restart(ServiceInfo s) => $"systemctl restart {U(s)}";

    public static string Enable(ServiceInfo s) => $"systemctl enable {U(s)}";

    public static string Disable(ServiceInfo s) => $"systemctl disable {U(s)}";

    /// <summary>Stopping it would cut the connection the app itself uses.</summary>
    public static bool IsSsh(ServiceInfo s) => s.Unit is "ssh" or "sshd" or "openssh-server" or "dropbear";
}

/// <summary>Shell commands for container control; run with sudo when needed (<see cref="RemoteShell.Run"/>).</summary>
public static class ContainerCommands
{
    public static readonly string[] RestartPolicies = ["no", "unless-stopped", "always", "on-failure"];

    private static string Q(string s) => RemoteShell.Quote(s);

    public static string Start(ContainerInfo c) => $"docker start {Q(c.Name)}";

    public static string Stop(ContainerInfo c) => $"docker stop {Q(c.Name)}";

    public static string Restart(ContainerInfo c) => $"docker restart {Q(c.Name)}";

    public static string SetRestart(ContainerInfo c, string policy) =>
        RestartPolicies.Contains(policy)
            ? $"docker update --restart={policy} {Q(c.Name)}"
            : throw new ArgumentException("Unknown restart policy", nameof(policy));

    public static string Remove(ContainerInfo c, ContainerRemoval r)
    {
        var lines = new List<string> { "set -e" };
        if (r.WholeProject && c.ComposeProject != null)
        {
            var down = $"docker compose -p {Q(c.ComposeProject)} down --remove-orphans" + (r.Volumes ? " -v" : "") + (r.Image ? " --rmi all" : "");
            // the folder holds the compose file; without it compose still finds the project by its labels
            lines.Add(c.ComposeDir != null
                ? $"if [ -d {Q(c.ComposeDir)} ]; then cd {Q(c.ComposeDir)}; fi; {down}"
                : down);
            if (r.ProjectFolder && SafeFolder(c.ComposeDir)) lines.Add($"rm -rf -- {Q(c.ComposeDir!)}");
        }
        else
        {
            lines.Add($"docker rm -f{(r.Volumes ? "v" : "")} {Q(c.Name)}");
            if (r.Image) lines.Add($"docker rmi {Q(c.Image)} 2>/dev/null || echo 'image {c.Image.Replace("'", "")} is still used, kept'");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Folders that may be deleted with a project: at least two levels deep (/opt/app, /root/app, /home/u/app),
    /// never inside system directories.
    /// </summary>
    public static bool SafeFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !dir.StartsWith('/') || dir.Contains("..")) return false;
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] system = ["bin", "boot", "dev", "etc", "lib", "lib32", "lib64", "proc", "run", "sbin", "sys", "usr", "var", "tmp"];
        if (parts.Length < 2 || system.Contains(parts[0])) return false;
        if (parts[0] == "home" && parts.Length < 3) return false; // a user's home itself
        return true;
    }
}
