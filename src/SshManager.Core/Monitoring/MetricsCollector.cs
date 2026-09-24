using System.Collections.Concurrent;
using System.Globalization;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Monitoring;

public sealed record ServerMetrics(
    double? CpuPercent, double? Load1, int? Cores,
    long? MemTotalKb, long? MemAvailableKb,
    long? DiskTotalKb, long? DiskUsedKb,
    TimeSpan? Uptime, DateTime Collected, string? Error = null)
{
    public double? MemPercent => MemTotalKb is > 0 && MemAvailableKb is { } a ? 100.0 * (MemTotalKb.Value - a) / MemTotalKb.Value : null;
    public double? DiskPercent => DiskTotalKb is > 0 && DiskUsedKb is { } u ? 100.0 * u / DiskTotalKb.Value : null;
}

/// <summary>CPU / memory / disk usage over SSH (Linux /proc), collected after each successful availability check.</summary>
public sealed class MetricsCollector(SshClientFactory ssh)
{
    private const string Script = """
        echo '@@sshm:stat1'; head -n1 /proc/stat
        sleep 1
        echo '@@sshm:stat2'; head -n1 /proc/stat
        echo '@@sshm:load'; cat /proc/loadavg
        echo '@@sshm:cores'; nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo
        echo '@@sshm:mem'; grep -E '^(MemTotal|MemAvailable|MemFree|Buffers|Cached):' /proc/meminfo
        echo '@@sshm:disk'; df -Pk / 2>/dev/null | tail -n1
        echo '@@sshm:uptime'; cat /proc/uptime
        echo '@@sshm:end'
        """;

    private readonly ConcurrentDictionary<Guid, ServerMetrics> _metrics = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(4);

    public event EventHandler<Guid>? MetricsChanged;

    public ServerMetrics? Get(Guid id) => _metrics.TryGetValue(id, out var m) ? m : null;

    public void Clear() => _metrics.Clear();

    public async Task CollectAsync(ServerEntry server)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost) || !_inFlight.TryAdd(server.Id, 0)) return;
        try
        {
            await _gate.WaitAsync();
            try
            {
                var m = await Task.Run(() => Collect(server));
                _metrics[server.Id] = m;
            }
            catch (Exception ex)
            {
                var old = Get(server.Id);
                _metrics[server.Id] = old == null
                    ? new ServerMetrics(null, null, null, null, null, null, null, null, DateTime.Now, ex.Message)
                    : old with { Error = ex.Message };
            }
            finally
            {
                _gate.Release();
            }
            MetricsChanged?.Invoke(this, server.Id);
        }
        finally
        {
            _inFlight.TryRemove(server.Id, out _);
        }
    }

    private ServerMetrics Collect(ServerEntry server)
    {
        using var client = ssh.Connect(server, interactive: false);
        var r = RemoteShell.Run(client, server, Script, elevated: false, TimeSpan.FromSeconds(30));
        var sections = SectionParser.Split(r.Output);
        if (!sections.ContainsKey("end")) throw new InvalidOperationException(r.Combined);
        return Parse(sections, DateTime.Now);
    }

    public static ServerMetrics Parse(Dictionary<string, string> s, DateTime now)
    {
        double? cpu = null;
        if (s.TryGetValue("stat1", out var a) && s.TryGetValue("stat2", out var b) &&
            CpuTimes(a) is { } t1 && CpuTimes(b) is { } t2 && t2.Total > t1.Total)
            cpu = Math.Clamp(100.0 * (1 - (double)(t2.Idle - t1.Idle) / (t2.Total - t1.Total)), 0, 100);

        double? load = null;
        if (s.TryGetValue("load", out var l) && l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } lp &&
            double.TryParse(lp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var l1))
            load = l1;

        int? cores = s.TryGetValue("cores", out var c) && int.TryParse(c.Trim(), out var n) ? n : null;

        long? memTotal = null, memAvail = null;
        if (s.TryGetValue("mem", out var mem))
        {
            var kv = mem.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split(':', 2))
                .Where(x => x.Length == 2)
                .ToDictionary(x => x[0].Trim(), x => long.TryParse(x[1].Replace("kB", "").Trim(), out var v) ? v : 0);
            memTotal = kv.GetValueOrDefault("MemTotal") is > 0 and var total ? total : null;
            memAvail = kv.TryGetValue("MemAvailable", out var av)
                ? av
                : kv.GetValueOrDefault("MemFree") + kv.GetValueOrDefault("Buffers") + kv.GetValueOrDefault("Cached");
            if (memTotal == null) memAvail = null;
        }

        long? diskTotal = null, diskUsed = null;
        if (s.TryGetValue("disk", out var d) && d.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 4 } dp &&
            long.TryParse(dp[1], out var dt) && long.TryParse(dp[2], out var du))
        {
            diskTotal = dt;
            diskUsed = du;
        }

        TimeSpan? uptime = null;
        if (s.TryGetValue("uptime", out var u) && u.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } up &&
            double.TryParse(up[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            uptime = TimeSpan.FromSeconds(secs);

        return new ServerMetrics(cpu, load, cores, memTotal, memAvail, diskTotal, diskUsed, uptime, now);
    }

    private static (long Idle, long Total)? CpuTimes(string line)
    {
        // cpu  user nice system idle iowait irq softirq steal guest guest_nice
        var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 5 || p[0] != "cpu") return null;
        var v = p.Skip(1).Take(8).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
        var idle = v[3] + (v.Length > 4 ? v[4] : 0);
        return (idle, v.Sum());
    }
}
