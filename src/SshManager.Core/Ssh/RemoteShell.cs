using System.Text;
using Renci.SshNet;
using SshManager.Core.Models;

namespace SshManager.Core.Ssh;

public sealed record ShellResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => (Output + "\n" + Error).Trim();
}

/// <summary>Runs shell scripts over an SSH.NET connection, optionally as root via sudo.</summary>
public static class RemoteShell
{
    /// <summary>POSIX single-quote quoting.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static bool IsRoot(ServerEntry server) => server.Username == "root";

    /// <summary>
    /// Runs <paramref name="script"/> with sh. When <paramref name="elevated"/> and the user is not root, uses
    /// sudo: with the stored password fed on stdin (never on the command line), otherwise sudo -n.
    /// </summary>
    public static ShellResult Run(SshClient client, ServerEntry server, string script, bool elevated,
        TimeSpan? timeout = null)
    {
        // stdin may still hold the sudo password when sudo did not ask for it; nothing in the script may read it.
        var body = "exec </dev/null\n" + script.Replace("\r\n", "\n");
        string command;
        string? stdin = null;
        if (!elevated || IsRoot(server)) command = "sh -c " + Quote(body);
        else if (!string.IsNullOrEmpty(server.Password))
        {
            command = "sudo -S -p '' sh -c " + Quote(body);
            stdin = server.Password + "\n";
        }
        else command = "sudo -n sh -c " + Quote(body);

        using var cmd = client.CreateCommand(command, Encoding.UTF8);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var task = cmd.ExecuteAsync();
        if (stdin != null)
        {
            using var input = cmd.CreateInputStream();
            input.Write(Encoding.UTF8.GetBytes(stdin));
        }
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Renci.SshNet.Common.SshOperationTimeoutException)
        {
            throw new TimeoutException(L.Get("Ssh.CommandTimeout"));
        }
        return new ShellResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
    }

    /// <summary>True when sudo refused (wrong/missing password, user not in sudoers).</summary>
    public static bool SudoFailed(ShellResult r) =>
        !r.Ok && r.Output.Length == 0 && r.Error.Contains("sudo", StringComparison.OrdinalIgnoreCase);
}
