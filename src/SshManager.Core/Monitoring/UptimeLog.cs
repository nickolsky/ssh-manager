using System.Globalization;
using System.Text;

namespace SshManager.Core.Monitoring;

/// <summary>One availability check.</summary>
/// <param name="Up">The server answered: SSH, or at least one monitored port.</param>
/// <param name="SshUp">sshd itself answered.</param>
/// <param name="IntervalMinutes">Check interval at that time: a sample stands for at most ~2 intervals.</param>
public sealed record UptimeSample(DateTime Utc, bool Up, bool SshUp, int? LatencyMs, int IntervalMinutes);

/// <summary>Uptime share of a period; null when there are no samples in it.</summary>
public sealed record UptimeStats(double? Day, double? Week, double? Month);

/// <summary>A stretch of time in one state, for charts and the outage list.</summary>
public sealed record UptimeSpan(DateTime FromUtc, DateTime ToUtc, UptimeState State)
{
    public TimeSpan Duration => ToUtc - FromUtc;
}

/// <summary>One bar of the uptime chart.</summary>
/// <param name="UpPercent">Share of observed time up, null when nothing was observed.</param>
/// <param name="DegradedShare">Share of observed time up only through a monitored port (0..1).</param>
public sealed record UptimeBucket(DateTime FromUtc, DateTime ToUtc, double? UpPercent, double DegradedShare, double? AvgLatencyMs);

public enum UptimeState
{
    NoData,
    Up,
    /// <summary>Up through a monitored port while SSH did not answer.</summary>
    Degraded,
    Down,
}

/// <summary>
/// History of availability checks, one CSV file per server in data\uptime (not secret: times and states only).
/// Samples are kept for <see cref="Retention"/>; files are cached in memory after the first read.
/// </summary>
public sealed class UptimeLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    private readonly string _dir;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, List<UptimeSample>> _cache = [];

    public UptimeLog(string? dir = null) => _dir = dir ?? Path.Combine(AppPaths.DataDir, "uptime");

    public event EventHandler<Guid>? Changed;

    private string FileOf(Guid id) => Path.Combine(_dir, id.ToString("N") + ".csv");

    public void Append(Guid id, UptimeSample s)
    {
        lock (_sync)
        {
            var list = LoadLocked(id);
            list.Add(s);
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(FileOf(id), Format(s) + "\n");
            }
            catch (IOException)
            {
                // history is best effort; monitoring must go on
            }
        }
        Changed?.Invoke(this, id);
    }

    /// <summary>Samples since <paramref name="fromUtc"/> (plus the one before, which covers the start), oldest first.</summary>
    public List<UptimeSample> Read(Guid id, DateTime fromUtc)
    {
        lock (_sync)
        {
            var list = LoadLocked(id);
            var i = list.FindIndex(x => x.Utc >= fromUtc);
            if (i < 0) return list.Count > 0 ? [list[^1]] : [];
            return list.GetRange(Math.Max(0, i - 1), list.Count - Math.Max(0, i - 1));
        }
    }

    public void Delete(Guid id)
    {
        lock (_sync)
        {
            _cache.Remove(id);
            try
            {
                File.Delete(FileOf(id));
            }
            catch (IOException)
            {
            }
        }
    }

    public UptimeStats Stats(Guid id, DateTime nowUtc)
    {
        var samples = Read(id, nowUtc - TimeSpan.FromDays(30));
        return new UptimeStats(
            Percent(samples, nowUtc - TimeSpan.FromDays(1), nowUtc),
            Percent(samples, nowUtc - TimeSpan.FromDays(7), nowUtc),
            Percent(samples, nowUtc - TimeSpan.FromDays(30), nowUtc));
    }

    // ---------- math (static, testable) ----------

    /// <summary>
    /// Each sample stands for the time until the next one, but for at most two check intervals (plus a minute);
    /// longer gaps (app closed, PC asleep) count as no data rather than up or down.
    /// </summary>
    public static List<UptimeSpan> Spans(IReadOnlyList<UptimeSample> samples, DateTime fromUtc, DateTime toUtc)
    {
        var spans = new List<UptimeSpan>();
        var cursor = fromUtc;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var reach = s.Utc + TimeSpan.FromMinutes(Math.Max(1, s.IntervalMinutes) * 2 + 1);
            var end = i + 1 < samples.Count && samples[i + 1].Utc < reach ? samples[i + 1].Utc : reach;
            var start = s.Utc < fromUtc ? fromUtc : s.Utc;
            if (end > toUtc) end = toUtc;
            if (end <= start) continue;
            if (start > cursor) Add(spans, cursor, start, UptimeState.NoData);
            Add(spans, start, end, !s.Up ? UptimeState.Down : s.SshUp ? UptimeState.Up : UptimeState.Degraded);
            cursor = end;
        }
        if (cursor < toUtc) Add(spans, cursor, toUtc, UptimeState.NoData);
        return spans;
    }

    private static void Add(List<UptimeSpan> spans, DateTime from, DateTime to, UptimeState state)
    {
        if (spans.Count > 0 && spans[^1].State == state && spans[^1].ToUtc == from) spans[^1] = spans[^1] with { ToUtc = to };
        else spans.Add(new UptimeSpan(from, to, state));
    }

    /// <summary>Share of observed time the server was up (degraded counts as up), 0..100.</summary>
    public static double? Percent(IReadOnlyList<UptimeSample> samples, DateTime fromUtc, DateTime toUtc)
    {
        double up = 0, total = 0;
        foreach (var s in Spans(samples, fromUtc, toUtc))
        {
            if (s.State == UptimeState.NoData) continue;
            total += s.Duration.TotalSeconds;
            if (s.State != UptimeState.Down) up += s.Duration.TotalSeconds;
        }
        return total <= 0 ? null : 100.0 * up / total;
    }

    /// <summary>Splits the period into <paramref name="count"/> equal buckets for a chart.</summary>
    public static List<UptimeBucket> Buckets(IReadOnlyList<UptimeSample> samples, DateTime fromUtc, DateTime toUtc, int count)
    {
        var spans = Spans(samples, fromUtc, toUtc);
        var step = (toUtc - fromUtc) / Math.Max(1, count);
        var result = new List<UptimeBucket>(count);
        int si = 0, li = 0;
        for (int b = 0; b < count; b++)
        {
            var from = fromUtc + step * b;
            var to = b == count - 1 ? toUtc : from + step;
            double up = 0, down = 0, degraded = 0;
            while (si < spans.Count && spans[si].ToUtc <= from) si++;
            for (int k = si; k < spans.Count && spans[k].FromUtc < to; k++)
            {
                var sp = spans[k];
                var secs = ((sp.ToUtc < to ? sp.ToUtc : to) - (sp.FromUtc > from ? sp.FromUtc : from)).TotalSeconds;
                if (secs <= 0) continue;
                switch (sp.State)
                {
                    case UptimeState.Up: up += secs; break;
                    case UptimeState.Degraded: up += secs; degraded += secs; break;
                    case UptimeState.Down: down += secs; break;
                }
            }
            long latencySum = 0;
            int latencyCount = 0;
            while (li < samples.Count && samples[li].Utc < from) li++;
            for (int k = li; k < samples.Count && samples[k].Utc < to; k++)
            {
                if (samples[k].LatencyMs is not { } l || !samples[k].Up) continue;
                latencySum += l;
                latencyCount++;
            }
            var observed = up + down;
            result.Add(new UptimeBucket(from, to,
                observed > 0 ? 100.0 * up / observed : null,
                observed > 0 ? degraded / observed : 0,
                latencyCount > 0 ? (double)latencySum / latencyCount : null));
        }
        return result;
    }

    /// <summary>Continuous down periods, newest first.</summary>
    public static List<UptimeSpan> Outages(IReadOnlyList<UptimeSample> samples, DateTime fromUtc, DateTime toUtc) =>
        Spans(samples, fromUtc, toUtc).Where(s => s.State == UptimeState.Down).OrderByDescending(s => s.FromUtc).ToList();

    // ---------- file format: 2026-09-24T10:00:00Z,1,1,45,5 ----------

    internal static string Format(UptimeSample s) => string.Join(',',
        s.Utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        s.Up ? "1" : "0", s.SshUp ? "1" : "0",
        s.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? "",
        s.IntervalMinutes.ToString(CultureInfo.InvariantCulture));

    internal static UptimeSample? ParseLine(string line)
    {
        var p = line.Trim().Split(',');
        if (p.Length < 5 ||
            !DateTime.TryParseExact(p[0], "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ||
            !int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
            return null;
        int? latency = int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
        return new UptimeSample(DateTime.SpecifyKind(t, DateTimeKind.Utc), p[1] == "1", p[2] == "1", latency, interval);
    }

    private List<UptimeSample> LoadLocked(Guid id)
    {
        if (_cache.TryGetValue(id, out var list)) return list;
        list = [];
        var file = FileOf(id);
        try
        {
            if (File.Exists(file))
            {
                foreach (var line in File.ReadLines(file))
                    if (ParseLine(line) is { } s) list.Add(s);
                list.Sort((a, b) => a.Utc.CompareTo(b.Utc));
                var cutoff = DateTime.UtcNow - Retention;
                var old = list.FindIndex(x => x.Utc >= cutoff);
                if (list.Count > 0 && old != 0)
                {
                    list.RemoveRange(0, old < 0 ? list.Count : old);
                    var sb = new StringBuilder();
                    foreach (var s in list) sb.Append(Format(s)).Append('\n');
                    File.WriteAllText(file, sb.ToString());
                }
            }
        }
        catch (IOException)
        {
        }
        _cache[id] = list;
        return list;
    }
}
