using System.Text;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Scripts;

/// <summary>
/// Install scripts and quick actions run in a terminal tab, so interactive installers work.
/// The script is uploaded over SFTP to /tmp (never on a command line) and removed when it ends.
/// </summary>
public sealed class ScriptRunner(SshClientFactory ssh, SessionLauncher launcher)
{
    /// <summary>Uploads the script (slow, call off the UI thread) and returns the remote command to run it.</summary>
    public string Prepare(ServerEntry server, ScriptEntry script)
    {
        var remote = $"/tmp/sshm-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.sh";
        var body = script.Body.Replace("\r\n", "\n");
        if (!body.EndsWith('\n')) body += "\n";
        using (var sftp = ssh.ConnectSftp(server))
        {
            using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(body));
            sftp.UploadFile(ms, remote);
            sftp.ChangePermissions(remote, 700); // SSH.NET reads the digits as octal
        }
        return RunCommand(server, remote, script.UseSudo);
    }

    public static string RunCommand(ServerEntry server, string remoteFile, bool sudo)
    {
        var f = RemoteShell.Quote(remoteFile);
        var env = $"env SSHM_SERVER_NAME={RemoteShell.Quote(server.Name)} SSHM_HOST={RemoteShell.Quote(server.Host)}";
        var elevate = sudo && !RemoteShell.IsRoot(server) ? "sudo " : "";
        var shell = $"$(command -v bash || echo sh)";
        return $"trap 'rm -f {remoteFile}' EXIT; {elevate}{env} {shell} {f}; rc=$?; echo; echo \"[exit $rc]\"; exit $rc";
    }

    public void Launch(ServerEntry server, string remoteCommand, string title) =>
        launcher.Launch(server, remoteCommand, title);

    /// <summary>Command (with sudo for non-root users) for container / service quick actions.</summary>
    public static string Elevated(ServerEntry server, string command) =>
        RemoteShell.IsRoot(server) ? command : "sudo " + command;

    public static string DockerLogs(ServerEntry s, string container) =>
        Elevated(s, $"docker logs -f --tail 200 {RemoteShell.Quote(container)}");

    public static string DockerRestart(ServerEntry s, string container) =>
        Elevated(s, $"docker restart {RemoteShell.Quote(container)}");

    public static string ServiceStatus(ServerEntry s, string unit) =>
        Elevated(s, $"systemctl status --no-pager -l {RemoteShell.Quote(unit)}");

    public static string ServiceRestart(ServerEntry s, string unit) =>
        Elevated(s, $"systemctl restart {RemoteShell.Quote(unit)}") + $" && systemctl status --no-pager {RemoteShell.Quote(unit)}";

    public static string ServiceLogs(ServerEntry s, string unit) =>
        Elevated(s, $"journalctl -u {RemoteShell.Quote(unit)} -n 200 -f --no-pager");
}
