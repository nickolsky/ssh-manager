using System.Collections.Concurrent;
using SshManager.Core.Forwarding;
using SshManager.Core.Models;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Core.Inventory;

/// <summary>
/// Logs in over SSH.NET and collects the OS, Docker containers, well-known services and NAT port forwards
/// into <see cref="ServerEntry.Facts"/>. Read-only on the server.
/// </summary>
public sealed class ServerInventoryService(VaultService vault, SshClientFactory ssh)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(2);

    private const string Script = """
        echo '@@sshm:os'; cat /etc/os-release 2>/dev/null || cat /usr/lib/os-release 2>/dev/null
        echo '@@sshm:kernel'; uname -srm 2>/dev/null
        if command -v docker >/dev/null 2>&1; then echo '@@sshm:docker'; docker ps -a --format '{{json .}}' 2>&1; fi
        if command -v systemctl >/dev/null 2>&1; then echo '@@sshm:services'; systemctl list-units --type=service --all --no-legend --plain --no-pager 2>/dev/null; fi
        if command -v iptables >/dev/null 2>&1; then echo '@@sshm:nat'; iptables -t nat -S 2>&1; fi
        echo '@@sshm:ports'; ss -Htlnp 2>/dev/null || ss -tlnp 2>/dev/null || netstat -tlnp 2>/dev/null
        echo '@@sshm:end'
        """;

    private readonly SemaphoreSlim _gate = new(4);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastAttempt = new();
    private readonly object _sync = new();

    /// <summary>A refresh started or finished for this server.</summary>
    public event EventHandler<Guid>? RunningChanged;

    public bool IsRunning(Guid serverId) => _running.ContainsKey(serverId);

    public static bool Supported(ServerEntry s) => string.IsNullOrWhiteSpace(s.JumpHost);

    /// <summary>True when there is no fresh data and no recent failed attempt.</summary>
    public bool NeedsRefresh(ServerEntry s)
    {
        if (!Supported(s) || IsRunning(s.Id)) return false;
        if (_lastAttempt.TryGetValue(s.Id, out var at) && DateTime.UtcNow - at < RetryAfterError) return false;
        return s.Facts?.InventoryUpdated is not { } updated || DateTime.Now - updated > StaleAfter;
    }

    /// <param name="interactive">Allow asking the user to trust an unknown host key.</param>
    public Task RefreshAsync(Guid serverId, bool interactive = false)
    {
        lock (_sync)
        {
            if (_running.TryGetValue(serverId, out var existing)) return existing;
            var task = Task.Run(() => RunAsync(serverId, interactive));
            _running[serverId] = task;
            RunningChanged?.Invoke(this, serverId);
            return task;
        }
    }

    private async Task RunAsync(Guid id, bool interactive)
    {
        try
        {
            if (!vault.TryRead(d => d.Servers.FirstOrDefault(x => x.Id == id)?.Clone(), out var server) || server == null) return;
            if (!Supported(server))
            {
                vault.UpdateFacts(id, f => f.InventoryError = L.Get("Inventory.JumpHost"));
                return;
            }
            _lastAttempt[id] = DateTime.UtcNow;
            await _gate.WaitAsync();
            try
            {
                var sections = Collect(server, interactive);
                vault.UpdateFacts(id, f => Apply(f, sections));
            }
            catch (Exception ex) when (ex is not VaultLockedException)
            {
                vault.UpdateFacts(id, f => f.InventoryError = ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (VaultLockedException)
        {
        }
        finally
        {
            lock (_sync) _running.TryRemove(id, out _);
            RunningChanged?.Invoke(this, id);
        }
    }

    private Dictionary<string, string> Collect(ServerEntry server, bool interactive)
    {
        using var client = ssh.Connect(server, interactive);
        var r = RemoteShell.Run(client, server, Script, elevated: true);
        if (RemoteShell.SudoFailed(r)) r = RemoteShell.Run(client, server, Script, elevated: false);
        var sections = SectionParser.Split(r.Output);
        if (!sections.ContainsKey("end")) throw new InvalidOperationException(L.Get("Inventory.Failed") + " " + r.Combined);
        return sections;
    }

    public static void Apply(ServerFacts f, Dictionary<string, string> sections)
    {
        if (sections.TryGetValue("os", out var os)) OsReleaseParser.Apply(os, f);
        if (sections.TryGetValue("kernel", out var kernel)) f.Kernel = kernel.Trim();

        f.DockerAvailable = sections.TryGetValue("docker", out var docker);
        if (docker == null) f.Containers = [];
        else if (!docker.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            f.Containers = DockerPsParser.Parse(docker);

        if (sections.TryGetValue("services", out var services)) f.Services = ServiceListParser.Parse(services);

        // "-P PREROUTING ACCEPT" is always printed when we could read the table (i.e. we were root)
        if (sections.TryGetValue("nat", out var nat) && nat.Contains("-P PREROUTING", StringComparison.Ordinal))
            f.Forwards = IptablesParser.ParseNat(nat);

        if (sections.TryGetValue("ports", out var ports)) f.ListeningPorts = ListeningPortParser.Parse(ports);

        f.InventoryError = null;
        f.InventoryUpdated = DateTime.Now;
    }
}
