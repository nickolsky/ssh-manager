using System.Text.RegularExpressions;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Inventory;

/// <summary>
/// Scheduled jobs of a server: every user's crontab, /etc/crontab and /etc/cron.d, the cron.hourly…monthly scripts
/// and systemd timers. Part of the inventory; <see cref="Collect"/> also reads them on their own (sshm cron, MCP).
/// </summary>
public static partial class CronCollector
{
    /// <summary>
    /// Shell for the "@@sshm:cron-*" sections. Other users' crontabs need root (the inventory runs through sudo);
    /// without it only the login user's own crontab is read.
    /// </summary>
    public const string Script = """
        if command -v crontab >/dev/null 2>&1; then echo '@@sshm:cron-users'; me=$(id -un)
          for u in $(cut -d: -f1 /etc/passwd); do
            if [ "$u" = "$me" ]; then c=$(crontab -l 2>/dev/null); else c=$(crontab -l -u "$u" 2>/dev/null); fi
            [ -n "$c" ] && printf '%s\n' "$c" | awk -v u="$u" '{print u "|" $0}'
          done; fi
        echo '@@sshm:cron-files'; for f in /etc/crontab /etc/cron.d/*; do [ -f "$f" ] && awk -v f="$f" '{print f "|" $0}' "$f" 2>/dev/null; done
        echo '@@sshm:cron-periodic'; for d in hourly daily weekly monthly; do for f in /etc/cron.$d/*; do [ -f "$f" ] && [ "${f##*/}" != .placeholder ] && echo "$d|$f"; done; done
        if command -v systemctl >/dev/null 2>&1; then echo '@@sshm:timers'
          t=$(systemctl list-units --type=timer --all --plain --no-legend --no-pager 2>/dev/null | awk '{print $1}' | grep '\.timer$')
          [ -n "$t" ] && systemctl show $t -p Id -p Unit -p Description -p ActiveState -p TimersCalendar -p TimersMonotonic -p NextElapseUSecRealtime -p LastTriggerUSec --no-pager 2>/dev/null; fi
        """;

    /// <summary>Reads only the scheduled jobs (quicker than a full inventory). Blocking; not for servers behind a jump host.</summary>
    public static List<CronJob> Collect(SshClientFactory ssh, ServerEntry server, bool interactive = false)
    {
        using var client = ssh.Connect(server, interactive);
        var script = Script + "\necho '@@sshm:end'";
        var r = RemoteShell.Run(client, server, script, elevated: true, TimeSpan.FromMinutes(1));
        if (RemoteShell.SudoFailed(r)) r = RemoteShell.Run(client, server, script, elevated: false, TimeSpan.FromMinutes(1));
        var sections = SectionParser.Split(r.Output);
        if (!sections.ContainsKey("end")) throw new InvalidOperationException(L.Get("Inventory.Failed") + " " + r.Combined);
        return Parse(sections);
    }

    /// <summary>All jobs from the sections (see <see cref="Collected"/> for whether they were there at all).</summary>
    public static List<CronJob> Parse(IReadOnlyDictionary<string, string> sections)
    {
        var jobs = new List<CronJob>();
        if (sections.TryGetValue("cron-users", out var users))
            foreach (var (user, line) in Prefixed(users))
                if (ParseLine(line, withUser: false) is { } j) jobs.Add(j.As(CronKind.Crontab, "crontab", user));
        if (sections.TryGetValue("cron-files", out var files))
            foreach (var (file, line) in Prefixed(files))
                if (ParseLine(line, withUser: true) is { } j) jobs.Add(j.As(CronKind.File, file, j.User));
        if (sections.TryGetValue("cron-periodic", out var periodic))
            foreach (var (period, path) in Prefixed(periodic))
                jobs.Add(new CronJob
                {
                    Kind = CronKind.Periodic, Source = "/etc/cron." + period, User = "root", Schedule = "@" + period, Command = path.Trim(),
                });
        if (sections.TryGetValue("timers", out var timers)) jobs.AddRange(ParseTimers(timers));
        return jobs;
    }

    /// <summary>A plain-text table for the console (sshm cron): schedule, user, command, where it comes from.</summary>
    public static List<string> Format(IReadOnlyList<CronJob> jobs)
    {
        if (jobs.Count == 0) return [L.Get("Cron.None")];
        var rows = jobs.Select(j => j.Kind == CronKind.Timer
            ? (Schedule: j.Schedule, User: "", Command: j.Command + (j.Next != null ? "  (" + L.F("Cron.Next", j.Next) + ")" : ""), Source: j.Source + (j.Active ? "" : " [inactive]"))
            : (j.Schedule, j.User, j.Command, j.Source)).ToList();
        var w1 = Math.Min(28, rows.Max(r => r.Schedule.Length));
        var w2 = Math.Min(12, rows.Max(r => r.User.Length));
        return rows.Select(r => $"{r.Schedule.PadRight(w1)}  {r.User.PadRight(w2)}  {r.Command}   # {r.Source}").ToList();
    }

    public static bool Collected(IReadOnlyDictionary<string, string> sections) =>
        sections.ContainsKey("cron-files") || sections.ContainsKey("cron-periodic");

    private static CronJob As(this CronJob j, CronKind kind, string source, string user)
    {
        j.Kind = kind;
        j.Source = source;
        j.User = user;
        return j;
    }

    private static IEnumerable<(string Prefix, string Line)> Prefixed(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var bar = raw.IndexOf('|');
            if (bar > 0) yield return (raw[..bar], raw[(bar + 1)..]);
        }
    }

    /// <summary>
    /// One crontab line: "m h dom mon dow [user] command" or "@reboot [user] command";
    /// blank lines, comments and VAR=value lines give null.
    /// </summary>
    public static CronJob? ParseLine(string line, bool withUser)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#') || EnvLine().IsMatch(t)) return null;
        var parts = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int fields;
        if (parts[0].StartsWith('@'))
        {
            if (!Specials.Contains(parts[0])) return null;
            fields = 1;
        }
        else
        {
            if (parts.Length < 6 || !parts.Take(5).All(p => Field().IsMatch(p))) return null;
            fields = 5;
        }
        var need = fields + (withUser ? 2 : 1);
        if (parts.Length < need) return null;
        // the command keeps its own spacing: cut the line after the schedule (and user) tokens
        var rest = t;
        for (var i = 0; i < fields + (withUser ? 1 : 0); i++)
        {
            rest = rest.TrimStart();
            rest = rest[parts[i].Length..];
        }
        return new CronJob
        {
            Schedule = string.Join(' ', parts.Take(fields)),
            User = withUser ? parts[fields] : "",
            Command = rest.Trim(),
        };
    }

    private static readonly HashSet<string> Specials =
        ["@reboot", "@yearly", "@annually", "@monthly", "@weekly", "@daily", "@midnight", "@hourly"];

    /// <summary><c>systemctl show *.timer -p …</c>: KEY=value blocks separated by blank lines.</summary>
    public static List<CronJob> ParseTimers(string text)
    {
        var jobs = new List<CronJob>();
        foreach (var block in text.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = new Dictionary<string, string>();
            foreach (var line in block.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) p[line[..eq]] = line[(eq + 1)..].Trim();
            }
            if (!p.TryGetValue("Id", out var id) || !id.EndsWith(".timer", StringComparison.Ordinal)) continue;
            jobs.Add(new CronJob
            {
                Kind = CronKind.Timer,
                Source = id,
                Schedule = TimerSchedule(p.GetValueOrDefault("TimersCalendar"), p.GetValueOrDefault("TimersMonotonic")),
                Command = p.GetValueOrDefault("Unit") ?? "",
                Description = p.GetValueOrDefault("Description") is { Length: > 0 } d ? d : null,
                Next = Time(p.GetValueOrDefault("NextElapseUSecRealtime")),
                Last = Time(p.GetValueOrDefault("LastTriggerUSec")),
                Active = p.GetValueOrDefault("ActiveState") is null or "active",
            });
        }
        return jobs.OrderBy(j => j.Source, StringComparer.Ordinal).ToList();
    }

    /// <summary>"{ OnCalendar=*-*-* 06,18:00:00 ; next_elapse=… }" → "OnCalendar=*-*-* 06,18:00:00".</summary>
    private static string TimerSchedule(string? calendar, string? monotonic)
    {
        var parts = new List<string>();
        foreach (var value in new[] { calendar, monotonic })
            foreach (Match m in TimerSpec().Matches(value ?? ""))
                parts.Add($"{m.Groups[1].Value}={m.Groups[2].Value.Trim()}");
        return string.Join("; ", parts);
    }

    private static string? Time(string? value) => value is null or "" or "n/a" or "0" ? null : value;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\s*=")]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^[0-9A-Za-z*/,\-]+$")]
    private static partial Regex Field();

    [GeneratedRegex(@"\{\s*(On[A-Za-z]+)=([^;}]*)")]
    private static partial Regex TimerSpec();
}
