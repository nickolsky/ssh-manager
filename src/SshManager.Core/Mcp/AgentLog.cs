using System.Globalization;
using System.Text;

namespace SshManager.Core.Mcp;

/// <summary>One action of an AI agent (a tool call), allowed or refused.</summary>
/// <param name="ServerId">Null for calls that are not about one server (list_servers).</param>
/// <param name="Args">The call's arguments as short JSON, without file contents or secrets.</param>
/// <param name="Result">What came of it: "exit 0: Filesystem…", the error, or why it was refused.</param>
public sealed record AgentLogEntry(
    DateTime Utc, Guid? ServerId, string ServerName, string Client, string Tool, string Args,
    AgentOutcome Outcome, string Result, TimeSpan Duration)
{
    /// <summary>One line: local time, client, server, tool, outcome, duration, arguments, result.</summary>
    public string Format()
    {
        var local = Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var outcome = Outcome switch { AgentOutcome.Ok => "ok", AgentOutcome.Failed => "FAILED", _ => "DENIED" };
        return $"{local}  {Client}  {(ServerName.Length > 0 ? ServerName : "-")}  {Tool}  {outcome}  {Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s" +
               $"  {OneLine(Args, 400)}  → {OneLine(Result, AgentLog.MaxResultChars)}";
    }

    private static string OneLine(string s, int max)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim().Replace("\n", " ⏎ ").Replace('\t', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public enum AgentOutcome
{
    Ok,
    Failed,
    /// <summary>Refused: the server's access level, a local folder outside the list, a declined confirmation.</summary>
    Denied,
}

/// <summary>
/// Per-server log of what AI agents did: <c>data\agent-logs\&lt;server id&gt;.log</c> (calls without a server go to
/// <c>general.log</c>). The current file starts over every day and when it passes a quarter of the size limit;
/// old files go after the retention period, and the oldest ones earlier when a server's files together exceed the limit.
/// </summary>
public sealed class AgentLog
{
    public const string FolderName = "agent-logs";
    public const int MaxResultChars = 1000;
    private const string General = "general";

    private readonly string _dir;
    private readonly Func<(long MaxBytes, TimeSpan Retention)> _limits;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private DateTime _lastPrune = DateTime.MinValue;

    /// <param name="limits">Read on every write, so changed settings apply at once.</param>
    /// <param name="now">Clock (UTC), replaceable in tests.</param>
    public AgentLog(string? dir, Func<(long MaxBytes, TimeSpan Retention)> limits, Func<DateTime>? now = null)
    {
        _dir = dir ?? Path.Combine(AppPaths.DataDir, FolderName);
        _limits = limits;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public string Folder => _dir;

    /// <summary>Raised after a line was written (on the writer's thread).</summary>
    public event Action<AgentLogEntry>? Appended;

    public string FileFor(Guid? serverId) => Path.Combine(_dir, Stem(serverId) + ".log");

    private static string Stem(Guid? serverId) => serverId?.ToString("N") ?? General;

    public void Write(AgentLogEntry entry)
    {
        var line = entry.Format() + "\n";
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var file = FileFor(entry.ServerId);
                var (maxBytes, retention) = _limits();
                RotateIfNeeded(entry.ServerId, file, maxBytes, Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(file, line, new UTF8Encoding(false));
                if (_now() - _lastPrune > TimeSpan.FromHours(1)) PruneAll(maxBytes, retention);
                else Prune(Stem(entry.ServerId), maxBytes, retention);
            }
            catch (IOException)
            {
                // a full or locked disk must not break the agent's call
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Appended?.Invoke(entry);
    }

    /// <summary>The last <paramref name="lines"/> lines for a server (null = the general log), older files included when needed.</summary>
    public List<string> Tail(Guid? serverId, int lines)
    {
        lock (_sync)
        {
            var result = new List<string>();
            foreach (var f in Files(Stem(serverId)).OrderByDescending(f => f.Current).ThenByDescending(f => f.Info.Name, StringComparer.Ordinal))
            {
                string[] content;
                try
                {
                    content = File.ReadAllLines(f.Info.FullName, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue;
                }
                result.InsertRange(0, content.Where(l => l.Length > 0));
                if (result.Count >= lines) break;
            }
            return result.Count > lines ? result[^lines..] : result;
        }
    }

    /// <summary>The last <paramref name="lines"/> lines of all servers together, in time order (lines start with the time).</summary>
    public List<string> TailAll(int lines)
    {
        List<string> stems;
        lock (_sync)
        {
            if (!Directory.Exists(_dir)) return [];
            stems = Directory.EnumerateFiles(_dir, "*.log").Select(f => Path.GetFileNameWithoutExtension(f).Split('-')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var all = stems.SelectMany(stem => Tail(stem == General ? null : Guid.TryParseExact(stem, "N", out var g) ? g : null, lines))
            .OrderBy(l => l.Length >= 19 ? l[..19] : l, StringComparer.Ordinal).ToList();
        return all.Count > lines ? all[^lines..] : all;
    }

    /// <summary>Applies the limits to every server's files (also called once an hour by writes).</summary>
    public void PruneAll()
    {
        lock (_sync)
        {
            var (maxBytes, retention) = _limits();
            PruneAll(maxBytes, retention);
        }
    }

    private void PruneAll(long maxBytes, TimeSpan retention)
    {
        _lastPrune = _now();
        if (!Directory.Exists(_dir)) return;
        var stems = Directory.EnumerateFiles(_dir, "*.log").Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(n => n.Split('-')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var stem in stems) Prune(stem, maxBytes, retention);
    }

    private void RotateIfNeeded(Guid? serverId, string file, long maxBytes, int adding)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0) return;
        var tooBig = info.Length + adding > Math.Max(64 * 1024, maxBytes / 4);
        if (!tooBig && FirstDay(file) == _now().ToLocalTime().Date) return;
        var stem = $"{Stem(serverId)}-{_now().ToLocalTime():yyyyMMdd-HHmmss-fff}";
        var archive = Path.Combine(_dir, stem + ".log");
        for (var n = 2; File.Exists(archive); n++) archive = Path.Combine(_dir, $"{stem}-{n}.log"); // two rotations in one millisecond
        File.Move(file, archive);
    }

    /// <summary>The day of the file's first line (lines start with the local time), not the file's creation time:
    /// Windows gives a file recreated under the same name its old creation time for a while.</summary>
    private static DateTime? FirstDay(string file)
    {
        using var reader = new StreamReader(file, Encoding.UTF8);
        var first = reader.ReadLine();
        return first != null && first.Length >= 10 &&
               DateTime.TryParseExact(first[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    private void Prune(string stem, long maxBytes, TimeSpan retention)
    {
        var archives = Files(stem).Where(f => !f.Current).OrderBy(f => f.Info.Name, StringComparer.Ordinal).ToList();
        var cutoff = _now() - retention;
        foreach (var f in archives.Where(f => ArchivedUtc(f.Info) < cutoff).ToList())
        {
            TryDelete(f.Info);
            archives.Remove(f);
        }
        var total = Files(stem).Sum(f => f.Info.Length);
        foreach (var f in archives)
        {
            if (total <= maxBytes) break;
            total -= f.Info.Length;
            TryDelete(f.Info);
        }
    }

    /// <summary>When a file was archived: from its name ("…-20260925-140312-123.log"), else its last write.</summary>
    private static DateTime ArchivedUtc(FileInfo f)
    {
        var name = Path.GetFileNameWithoutExtension(f.Name);
        var dash = name.IndexOf('-');
        return dash > 0 && name.Length >= dash + 20 && DateTime.TryParseExact(name.Substring(dash + 1, 19), "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var local)
            ? local.ToUniversalTime()
            : f.LastWriteTimeUtc;
    }

    private IEnumerable<(FileInfo Info, bool Current)> Files(string stem)
    {
        if (!Directory.Exists(_dir)) yield break;
        foreach (var path in Directory.EnumerateFiles(_dir, stem + "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == stem) yield return (new FileInfo(path), true);
            else if (name.StartsWith(stem + "-", StringComparison.Ordinal)) yield return (new FileInfo(path), false);
        }
    }

    private static void TryDelete(FileInfo f)
    {
        try
        {
            f.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
