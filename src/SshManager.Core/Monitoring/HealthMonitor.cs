using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Monitoring;

public enum HealthState
{
    Unknown,
    Checking,
    Online,
    Offline,
    /// <summary>Monitoring switched off for this server.</summary>
    Disabled,
    /// <summary>Reachable only through a jump host; not checked.</summary>
    NotChecked,
}

/// <param name="SshDown">Online only because a monitored port answered; sshd did not.</param>
public sealed record ServerHealth(HealthState State, int? LatencyMs = null, DateTime? Checked = null, string? Error = null,
    bool SshDown = false)
{
    public static readonly ServerHealth Unknown = new(HealthState.Unknown);
}

public sealed record HealthTransition(Guid ServerId, ServerHealth Before, ServerHealth After);

public sealed record PortTransition(Guid ServerId, MonitoredPort Port, ServerHealth After);

/// <summary>
/// Periodically opens a TCP connection to each server's SSH port and reads the "SSH-" banner, and connects to its
/// monitored ports. The server counts as up when SSH or any monitored port answers (a VPN box may firewall SSH).
/// ICMP is not used: VPN hosts often drop it. Every check is written to the <see cref="UptimeLog"/>.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly VaultService _vault;
    private readonly SettingsService _settings;
    private readonly UptimeLog? _log;
    private readonly ConcurrentDictionary<Guid, ServerHealth> _health = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<int, ServerHealth>> _ports = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _due = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(8);
    private Timer? _timer;

    public HealthMonitor(VaultService vault, SettingsService settings, UptimeLog? log = null)
    {
        _vault = vault;
        _settings = settings;
        _log = log;
    }

    public event EventHandler<Guid>? HealthChanged;
    /// <summary>A server went from online to offline.</summary>
    public event EventHandler<HealthTransition>? WentDown;
    /// <summary>A monitored port stopped answering while the server itself is up.</summary>
    public event EventHandler<PortTransition>? PortWentDown;
    /// <summary>A check succeeded (used to collect CPU / memory right after).</summary>
    public event EventHandler<ServerEntry>? ServerOnline;

    public ServerHealth Get(Guid id) => _health.TryGetValue(id, out var h) ? h : ServerHealth.Unknown;

    /// <summary>State of each monitored port by port number (empty until checked).</summary>
    public IReadOnlyDictionary<int, ServerHealth> GetPorts(Guid id) =>
        _ports.TryGetValue(id, out var p) ? p : new Dictionary<int, ServerHealth>();

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _health.Clear();
        _ports.Clear();
        _due.Clear();
    }

    /// <summary>Re-evaluates schedules (e.g. after the interval setting changed).</summary>
    public void Reschedule()
    {
        _due.Clear();
        _timer?.Change(TimeSpan.FromMilliseconds(200), TickInterval);
    }

    public int IntervalFor(ServerEntry s) => s.MonitorIntervalMinutes ?? _settings.Settings.MonitorIntervalMinutes;

    public void CheckNow(Guid id)
    {
        if (_vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Clone(), out var s) && s != null)
            _ = CheckAsync(s, force: true);
    }

    public void CheckAll()
    {
        if (!_vault.TryRead(d => d.Servers.Select(s => s.Clone()).ToList(), out var servers)) return;
        foreach (var s in servers) _ = CheckAsync(s, force: true);
    }

    private void Tick()
    {
        if (!_vault.TryRead(d => d.Servers.Select(s => s.Clone()).ToList(), out var servers)) return;
        var ids = servers.Select(s => s.Id).ToHashSet();
        foreach (var gone in _health.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _health.TryRemove(gone, out _);
            _ports.TryRemove(gone, out _);
            _due.TryRemove(gone, out _);
        }
        var now = DateTime.UtcNow;
        foreach (var s in servers)
        {
            if (!_due.TryGetValue(s.Id, out var due) || due <= now) _ = CheckAsync(s, force: false);
        }
    }

    private async Task CheckAsync(ServerEntry s, bool force)
    {
        var interval = IntervalFor(s);
        if (!string.IsNullOrWhiteSpace(s.JumpHost))
        {
            Set(s.Id, new ServerHealth(HealthState.NotChecked));
            _due[s.Id] = DateTime.MaxValue;
            return;
        }
        if (interval <= 0 && !force)
        {
            Set(s.Id, new ServerHealth(HealthState.Disabled));
            _due[s.Id] = DateTime.UtcNow + TickInterval * 4;
            return;
        }
        if (!_inFlight.TryAdd(s.Id, 0)) return;
        try
        {
            var previous = Get(s.Id);
            if (previous.State is HealthState.Unknown or HealthState.Disabled)
                Set(s.Id, previous with { State = HealthState.Checking });
            await _gate.WaitAsync();
            ServerHealth result;
            var ports = new Dictionary<int, ServerHealth>();
            try
            {
                var sshCheck = ProbeWithRetryAsync(s.Host, s.Port, expectSsh: true);
                var checks = s.MonitoredPorts.Where(p => p.Port != s.Port).DistinctBy(p => p.Port)
                    .Select(async p => (p.Port, await ProbeWithRetryAsync(s.Host, p.Port, expectSsh: false))).ToList();
                result = await sshCheck;
                foreach (var (port, h) in await Task.WhenAll(checks)) ports[port] = h;
                if (s.MonitoredPorts.Any(p => p.Port == s.Port)) ports[s.Port] = result with { Error = null };
                if (result.State == HealthState.Offline && ports.Values.FirstOrDefault(p => p.State == HealthState.Online) is { } open)
                    result = new ServerHealth(HealthState.Online, open.LatencyMs, open.Checked,
                        L.F("Health.SshDownPortUp", result.Error), SshDown: true);
            }
            finally
            {
                _gate.Release();
            }
            _log?.Append(s.Id, new UptimeSample(DateTime.UtcNow, result.State == HealthState.Online, !result.SshDown,
                result.LatencyMs, Math.Max(1, interval <= 0 ? 60 : interval)));
            _due[s.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, interval <= 0 ? 60 : interval));
            var previousPorts = GetPorts(s.Id);
            _ports[s.Id] = ports;
            Set(s.Id, result);
            foreach (var p in s.MonitoredPorts)
            {
                if (previousPorts.TryGetValue(p.Port, out var before) && before.State == HealthState.Online &&
                    ports.TryGetValue(p.Port, out var after) && after.State == HealthState.Offline)
                    PortWentDown?.Invoke(this, new PortTransition(s.Id, p, after));
            }
            if (previous.State == HealthState.Online && result.State == HealthState.Offline)
                WentDown?.Invoke(this, new HealthTransition(s.Id, previous, result));
            if (result.State == HealthState.Online && !result.SshDown) ServerOnline?.Invoke(this, s);
        }
        finally
        {
            _inFlight.TryRemove(s.Id, out _);
        }
    }

    private void Set(Guid id, ServerHealth h)
    {
        _health[id] = h;
        HealthChanged?.Invoke(this, id);
    }

    /// <summary>One retry before declaring something down, to ride out a dropped packet.</summary>
    private static async Task<ServerHealth> ProbeWithRetryAsync(string host, int port, bool expectSsh)
    {
        var r = await ProbeAsync(host, port, ProbeTimeout, expectSsh);
        if (r.State != HealthState.Offline) return r;
        await Task.Delay(3000);
        return await ProbeAsync(host, port, ProbeTimeout, expectSsh);
    }

    /// <param name="expectSsh">Read the greeting and flag a non-SSH answer; otherwise an accepted connection is enough.</param>
    public static async Task<ServerHealth> ProbeAsync(string host, int port, TimeSpan timeout, bool expectSsh = true)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            var latency = (int)sw.ElapsedMilliseconds;
            if (!expectSsh) return new ServerHealth(HealthState.Online, latency, DateTime.Now);
            var buffer = new byte[256];
            string banner = "";
            try
            {
                var n = await tcp.GetStream().ReadAsync(buffer, cts.Token);
                banner = Encoding.ASCII.GetString(buffer, 0, n);
            }
            catch (OperationCanceledException)
            {
            }
            var error = banner.StartsWith("SSH-", StringComparison.Ordinal) ? null : L.Get("Health.NoBanner");
            return new ServerHealth(HealthState.Online, latency, DateTime.Now, error);
        }
        catch (OperationCanceledException)
        {
            return new ServerHealth(HealthState.Offline, null, DateTime.Now, L.Get("Health.Timeout"));
        }
        catch (SocketException ex)
        {
            return new ServerHealth(HealthState.Offline, null, DateTime.Now, ex.Message);
        }
    }

    public void Dispose() => Stop();
}
