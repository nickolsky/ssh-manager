using System.Diagnostics;
using SshManager.Core.Models;

namespace SshManager.Core.Ssh;

/// <summary>
/// Rebooting a server and waiting for it to come back. The kernel's boot id tells a real reboot apart from a server
/// that only dropped the connection for a moment.
/// </summary>
public static class ServerPower
{
    /// <summary>
    /// The kernel's boot id plus the start time of PID 1: the first changes on a real reboot, the second also when a
    /// container VPS (OpenVZ, LXC) restarts on the host's kernel.
    /// </summary>
    private const string BootIdCommand = "echo \"$(cat /proc/sys/kernel/random/boot_id) $(cut -d' ' -f22 /proc/1/stat 2>/dev/null)\"";

    /// <summary>Detached with a short delay, so the command returns before the connection drops.</summary>
    internal const string RebootCommand = "nohup sh -c 'sleep 2; systemctl reboot || shutdown -r now || reboot' >/dev/null 2>&1 &";

    /// <summary>Asks the server to reboot (as root, through sudo when needed). Returns its boot id before the reboot. Blocking.</summary>
    public static string Reboot(SshClientFactory ssh, ServerEntry server, bool interactive)
    {
        using var client = ssh.Connect(server, interactive);
        var bootId = BootId(client);
        var r = RemoteShell.Run(client, server, RebootCommand, elevated: true, TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new InvalidOperationException(r.Combined.Trim() is { Length: > 0 } e ? e : L.Get("Reboot.Failed"));
        return bootId;
    }

    private static string BootId(Renci.SshNet.SshClient client)
    {
        using var cmd = client.CreateCommand(BootIdCommand);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        return cmd.Execute().Trim();
    }

    /// <summary>
    /// Polls until SSH answers again after the reboot: with a different boot id, or at all once it had been unreachable
    /// (container VPSes — OpenVZ, LXC — share the host's kernel, so their boot id stays the same).
    /// The time it took, or null when <paramref name="timeout"/> ran out.
    /// </summary>
    public static async Task<TimeSpan?> WaitBackAsync(SshClientFactory ssh, ServerEntry server, string bootIdBefore, TimeSpan timeout,
        CancellationToken ct = default, TimeSpan? pollEvery = null)
    {
        var sw = Stopwatch.StartNew();
        var poll = pollEvery ?? TimeSpan.FromSeconds(10);
        var wasDown = false;
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, poll.TotalSeconds)), ct);
        while (sw.Elapsed < timeout)
        {
            try
            {
                var id = await Task.Run(() =>
                {
                    using var client = ssh.Connect(server, interactive: false);
                    return BootId(client);
                }, ct);
                if (wasDown || (id.Length > 0 && id != bootIdBefore)) return sw.Elapsed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                wasDown = true; // still down, or sshd not up yet
            }
            await Task.Delay(poll, ct);
        }
        return null;
    }
}
