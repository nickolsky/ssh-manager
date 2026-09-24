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

public sealed record ServerHealth(HealthState State, int? LatencyMs = null, DateTime? Checked = null, string? Error = null)
{
    public static readonly ServerHealth Unknown = new(HealthState.Unknown);
}

public sealed record HealthTransition(Guid ServerId, ServerHealth Before, ServerHealth After);

/// <summary>
/// Periodically opens a TCP connection to each server's SSH port and reads the "SSH-" banner.
/// ICMP is not used: VPN hosts often drop it.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly VaultService _vault;
    private readonly SettingsService _settings;
    private readonly ConcurrentDictionary<Guid, ServerHealth> _health = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _due = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _gate = new(8);
    private Timer? _timer;

    public HealthMonitor(VaultService vault, SettingsService settings)
    {
        _vault = vault;
        _settings = settings;
    }

    public event EventHandler<Guid>? HealthChanged;
    /// <summary>A server went from online to offline.</summary>
    public event EventHandler<HealthTransition>? WentDown;
    /// <summary>A check succeeded (used to collect CPU / memory right after).</summary>
    public event EventHandler<ServerEntry>? ServerOnline;

    public ServerHealth Get(Guid id) => _health.TryGetValue(id, out var h) ? h : ServerHealth.Unknown;

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _health.Clear();
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
            try
            {
                result = await ProbeAsync(s.Host, s.Port, ProbeTimeout);
                // one retry before declaring a server down, to ride out a dropped packet
                if (result.State == HealthState.Offline)
                {
                    await Task.Delay(3000);
                    result = await ProbeAsync(s.Host, s.Port, ProbeTimeout);
                }
            }
            finally
            {
                _gate.Release();
            }
            _due[s.Id] = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, interval <= 0 ? 60 : interval));
            Set(s.Id, result);
            if (previous.State == HealthState.Online && result.State == HealthState.Offline)
                WentDown?.Invoke(this, new HealthTransition(s.Id, previous, result));
            if (result.State == HealthState.Online) ServerOnline?.Invoke(this, s);
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

    public static async Task<ServerHealth> ProbeAsync(string host, int port, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            var latency = (int)sw.ElapsedMilliseconds;
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
