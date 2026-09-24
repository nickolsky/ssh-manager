using System.Collections.Concurrent;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Terminal;

/// <summary>
/// Command lines typed in the built-in terminal, per server, newest first. Shared by the server's tabs while the
/// app runs; never written to disk (the server's own shell history is read at the start of each session).
/// </summary>
public sealed class CommandHistory
{
    private const int Max = 5000;
    private static readonly ConcurrentDictionary<Guid, CommandHistory> ByServer = new();
    private readonly List<string> _lines = [];
    private bool _remoteLoaded;

    public static CommandHistory For(Guid serverId) => ByServer.GetOrAdd(serverId, _ => new CommandHistory());

    public static void Forget(Guid serverId) => ByServer.TryRemove(serverId, out _);

    public void Add(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.Length > 2000) return;
        lock (_lines)
        {
            _lines.Remove(line);
            _lines.Insert(0, line);
            if (_lines.Count > Max) _lines.RemoveRange(Max, _lines.Count - Max);
        }
    }

    /// <summary>The server's history file (oldest first); merged once, behind what was typed here.</summary>
    public void MergeRemote(IEnumerable<string> oldestFirst)
    {
        lock (_lines)
        {
            if (_remoteLoaded) return;
            _remoteLoaded = true;
            var seen = new HashSet<string>(_lines, StringComparer.Ordinal);
            foreach (var l in oldestFirst.Reverse())
            {
                var line = l.Trim();
                if (line.Length == 0 || line.Length > 2000 || !seen.Add(line)) continue;
                _lines.Add(line);
                if (_lines.Count >= Max) break;
            }
        }
    }

    public List<string> Snapshot()
    {
        lock (_lines) return [.. _lines];
    }
}

/// <summary>
/// Suggestions for one terminal tab: loads what the server has (commands, history, containers, services) over
/// the tab's SSH connection and answers <see cref="CompleteAsync"/> for the page.
/// </summary>
public sealed class TerminalAssist(ServerEntry server, Func<string, TimeSpan, ShellResult> run)
{
    private static readonly TimeSpan InfoTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DirTtl = TimeSpan.FromSeconds(8);

    private readonly CommandHistory _history = CommandHistory.For(server.Id);
    private readonly ConcurrentDictionary<string, (DateTime At, Task<IReadOnlyList<string>?> List)> _dirs = new();
    private readonly ConcurrentDictionary<string, (DateTime At, Task<IReadOnlyList<Candidate>> List)> _queries = new();
    private RemoteInfo _info = new();
    private Task? _loading;
    private DateTime _loaded;
    private List<string>? _historyCache;
    private Dictionary<string, int>? _useCache;

    /// <summary>The shell's working directory (reported by the integration on every prompt).</summary>
    public string? Cwd { get; set; }

    public string? Home => _info.Home;

    /// <summary>Starts loading the server's commands and history in the background.</summary>
    public void Start() => EnsureInfo();

    public void AddCommand(string line)
    {
        _history.Add(line);
        _historyCache = null;
        // a container or a service may have been created / removed
        if (line.Contains("docker") || line.Contains("systemctl") || line.Contains("compose")) _loaded = DateTime.MinValue;
        _dirs.Clear();
    }

    public async Task<CompletionResult> CompleteAsync(string line, int cursor, bool explicitRequest, CancellationToken ct)
    {
        EnsureInfo();
        var info = _info;
        if (_historyCache is not { } history)
        {
            _historyCache = history = _history.Snapshot();
            _useCache = CountUse(history);
        }
        var use = _useCache ?? [];
        var containers = info.Containers.Count > 0
            ? info.Containers
            : server.Facts?.Containers.Select(c => new Candidate(c.Name, $"{c.State} · {c.Image}")).ToList() ?? [];
        var services = info.Services.Count > 0
            ? info.Services
            : server.Facts?.Services.Select(s => new Candidate(s.Unit, s.Title)).ToList() ?? [];
        var src = new CompletionSource
        {
            History = history,
            CommandUse = use,
            Commands = info.Commands,
            Containers = containers,
            Images = info.Images,
            Services = services,
            ComposeServices = server.Facts?.Containers.Where(c => c.ComposeService != null)
                .Select(c => (c.ComposeService!, c.ComposeDir)).ToList() ?? [],
            Cwd = Cwd,
            Home = info.Home,
            ListDirectory = ListDirectoryAsync,
            Query = QueryAsync,
        };
        return await CompletionEngine.CompleteAsync(line, cursor, src, explicitRequest, ct).ConfigureAwait(false);
    }

    private static Dictionary<string, int> CountUse(List<string> history)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in history)
        {
            var first = h.Split(' ', 2)[0];
            if (first is "sudo" && h.Split(' ', 3) is { Length: > 1 } parts) first = parts[1];
            d[first] = d.GetValueOrDefault(first) + 1;
        }
        return d;
    }

    // ---------- remote info ----------

    private sealed class RemoteInfo
    {
        public string? Home;
        public List<string> Commands = [];
        public List<Candidate> Containers = [];
        public List<Candidate> Images = [];
        public List<Candidate> Services = [];
    }

    private const string InfoScript = """
        h="$HOME/.bash_history"; case "${SHELL##*/}" in zsh) h="${HISTFILE:-$HOME/.zsh_history}" ;; esac
        echo '@@home'; echo "$HOME"
        echo '@@commands'
        if command -v bash >/dev/null 2>&1; then bash -c 'compgen -c' 2>/dev/null; else (IFS=:; for d in $PATH; do ls "$d" 2>/dev/null; done); fi | sort -u | head -n 12000
        echo '@@history'; tail -n 5000 "$h" 2>/dev/null
        echo '@@containers'; docker ps -a --format '{{.Names}}\t{{.State}} · {{.Image}}' 2>/dev/null | head -n 500
        echo '@@images'; docker images --format '{{.Repository}}:{{.Tag}}' 2>/dev/null | grep -v '<none>' | head -n 500
        echo '@@units'; systemctl list-unit-files --type=service --no-legend --no-pager 2>/dev/null | awk '{print $1"\t"$2}' | head -n 3000
        """;

    private void EnsureInfo()
    {
        if (_loading is { IsCompleted: false } || DateTime.UtcNow - _loaded < InfoTtl) return;
        _loaded = DateTime.UtcNow;
        _loading = Task.Run(() =>
        {
            try
            {
                var r = run(InfoScript, TimeSpan.FromSeconds(30));
                var (info, history) = ParseInfo(r.Output);
                _info = info;
                _history.MergeRemote(history);
                _historyCache = null;
            }
            catch (Exception)
            {
                _loaded = DateTime.UtcNow - InfoTtl + TimeSpan.FromSeconds(20); // retry soon
            }
        });
    }

    private static (RemoteInfo, List<string>) ParseInfo(string output)
    {
        var info = new RemoteInfo();
        var history = new List<string>();
        string? section = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@") && line.Length < 20 && !line.Contains(' '))
            {
                section = line[2..];
                continue;
            }
            if (line.Length == 0) continue;
            switch (section)
            {
                case "home":
                    info.Home = line.Trim();
                    break;
                case "commands":
                    if (line.Length <= 64 && line.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '+' or ':' or '@'))
                        info.Commands.Add(line);
                    break;
                case "history":
                    // zsh extended history: ": 1700000000:0;command"
                    if (line.StartsWith(": ") && line.IndexOf(';') is var semi and > 0) line = line[(semi + 1)..];
                    if (!line.StartsWith('#')) history.Add(line);
                    break;
                case "containers":
                    if (line.Split('\t') is var c && c[0].Length > 0) info.Containers.Add(new Candidate(c[0], c.Length > 1 ? c[1] : null));
                    break;
                case "images":
                    info.Images.Add(new Candidate(line.Trim().Replace(":latest", "")));
                    break;
                case "units":
                    var u = line.Split('\t');
                    var unit = u[0].EndsWith(".service") ? u[0][..^8] : u[0];
                    if (unit.Length > 0 && !unit.EndsWith('@')) info.Services.Add(new Candidate(unit, u.Length > 1 ? u[1] : null));
                    break;
            }
        }
        return (info, history);
    }

    // ---------- live lookups ----------

    private Task<IReadOnlyList<string>?> ListDirectoryAsync(string dir, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_dirs.TryGetValue(dir, out var cached) && now - cached.At < DirTtl) return cached.List;
        var task = Task.Run<IReadOnlyList<string>?>(() =>
        {
            try
            {
                var r = run("ls -1ApL -- " + RemoteShell.Quote(dir) + " 2>/dev/null | head -n 5000", TimeSpan.FromSeconds(8));
                return r.Output.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Length > 0).ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }, CancellationToken.None);
        _dirs[dir] = (now, task);
        if (_dirs.Count > 200) _dirs.Clear();
        return task;
    }

    private Task<IReadOnlyList<Candidate>> QueryAsync(string kind, string prefix, CancellationToken ct)
    {
        var (key, script) = kind switch
        {
            // the first characters give the list, the engine filters it further
            "package" => ("p:" + prefix[..Math.Min(4, prefix.Length)],
                "apt-cache pkgnames " + RemoteShell.Quote(prefix[..Math.Min(4, prefix.Length)]) + " 2>/dev/null | head -n 5000"),
            "installed" => ("i", "dpkg-query -W -f='${Package}\\n' 2>/dev/null || rpm -qa --qf '%{NAME}\\n' 2>/dev/null"),
            "branch" => ("b:" + Cwd, "git -C " + RemoteShell.Quote(Cwd ?? ".") +
                                     " for-each-ref --format='%(refname:short)' refs/heads refs/remotes refs/tags 2>/dev/null | head -n 500"),
            _ => ("", ""),
        };
        if (script.Length == 0) return Task.FromResult<IReadOnlyList<Candidate>>([]);
        var now = DateTime.UtcNow;
        if (_queries.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromMinutes(1)) return cached.List;
        var task = Task.Run<IReadOnlyList<Candidate>>(() =>
        {
            try
            {
                var r = run(script, TimeSpan.FromSeconds(10));
                return r.Output.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Select(x => new Candidate(x)).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }, CancellationToken.None);
        _queries[key] = (now, task);
        return task;
    }
}
