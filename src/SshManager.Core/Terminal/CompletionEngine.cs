using System.Text;

namespace SshManager.Core.Terminal;

/// <summary>One suggestion. Accepting it sends <see cref="Delete"/> backspaces, then <see cref="Insert"/>.</summary>
/// <param name="Kind">history, command, sub, flag, dir, file, container, image, service, package, branch, value.</param>
public sealed record CompletionItem(string Label, string Kind, string? Detail, int Delete, string Insert);

/// <param name="Ghost">Grey inline continuation of the line (from the history), accepted with → / End.</param>
public sealed record CompletionResult(IReadOnlyList<CompletionItem> Items, string? Ghost)
{
    public static readonly CompletionResult Empty = new([], null);
}

public sealed record Candidate(string Name, string? Detail = null);

/// <summary>What the engine can suggest from; filled by <see cref="TerminalAssist"/>.</summary>
public sealed record CompletionSource
{
    /// <summary>Command lines, newest first.</summary>
    public IReadOnlyList<string> History { get; init; } = [];
    /// <summary>How often each command name starts a line in the history.</summary>
    public IReadOnlyDictionary<string, int> CommandUse { get; init; } = new Dictionary<string, int>();
    public IReadOnlyCollection<string> Commands { get; init; } = [];
    public IReadOnlyList<Candidate> Containers { get; init; } = [];
    public IReadOnlyList<Candidate> Images { get; init; } = [];
    public IReadOnlyList<Candidate> Services { get; init; } = [];
    /// <summary>docker compose services and their project folders.</summary>
    public IReadOnlyList<(string Service, string? Dir)> ComposeServices { get; init; } = [];
    public string? Cwd { get; init; }
    public string? Home { get; init; }
    /// <summary>Names in an absolute directory, directories end with '/'; null = cannot list.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<string>?>>? ListDirectory { get; init; }
    /// <summary>Live lookups: (kind: package, installed, branch; prefix) → candidates.</summary>
    public Func<string, string, CancellationToken, Task<IReadOnlyList<Candidate>>>? Query { get; init; }
}

/// <summary>
/// Shell-aware suggestions for the built-in terminal: history, commands, subcommands and flags from
/// <see cref="CommandSpecs"/>, files on the server, containers / services / packages / branches.
/// </summary>
public static class CompletionEngine
{
    private const int MaxItems = 60;

    /// <summary>Words that run the next word as a command (their own flags are skipped).</summary>
    private static readonly Dictionary<string, string[]> Prefixes = new(StringComparer.Ordinal)
    {
        ["sudo"] = ["-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-U"],
        ["time"] = [], ["nohup"] = [], ["exec"] = [], ["command"] = [], ["builtin"] = [], ["env"] = ["-u", "-C"],
        ["nice"] = ["-n"], ["ionice"] = ["-c", "-n"], ["timeout"] = ["-s", "-k"], ["watch"] = ["-n"], ["xargs"] = ["-n", "-I", "-d", "-P"],
        ["stdbuf"] = ["-i", "-o", "-e"], ["strace"] = ["-e", "-o", "-p"], ["torsocks"] = [], ["proxychains"] = [],
    };

    /// <param name="line">The input line (text after the cursor may follow).</param>
    /// <param name="cursor">Cursor position in <paramref name="line"/>.</param>
    /// <param name="explicitRequest">Ctrl+Space: also list files for an empty word, substring matches in the history.</param>
    public static async Task<CompletionResult> CompleteAsync(string line, int cursor, CompletionSource src, bool explicitRequest,
        CancellationToken ct = default)
    {
        cursor = Math.Clamp(cursor, 0, line.Length);
        var before = line[..cursor];
        var atEnd = cursor == line.Length;
        var items = new List<CompletionItem>();
        string? ghost = null;

        // the whole line from the history
        if (atEnd && before.Trim().Length > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in src.History)
            {
                if (h.Length <= before.Length || !h.StartsWith(before, StringComparison.Ordinal) || !seen.Add(h)) continue;
                ghost ??= h[before.Length..];
                items.Add(new CompletionItem(h, "history", null, 0, h[before.Length..]));
                if (items.Count >= 3) break;
            }
            if (explicitRequest && before.Trim().Length >= 2)
                foreach (var h in src.History)
                {
                    if (items.Count >= 8) break;
                    if (!h.StartsWith(before, StringComparison.Ordinal) && h.Contains(before.Trim(), StringComparison.OrdinalIgnoreCase) && seen.Add(h))
                        items.Add(new CompletionItem(h, "history", null, before.Length, h));
                }
        }
        else if (atEnd && explicitRequest)
        {
            // Ctrl+Space on an empty line: recent commands
            foreach (var h in src.History.Distinct().Take(15))
                items.Add(new CompletionItem(h, "history", null, before.Length, h));
            return new CompletionResult(items, null);
        }

        var words = ShellWords.Parse(before, out var current);
        if (current == null) return new CompletionResult(items, ghost); // inside $(...) or a comment: nothing reliable
        var tokenItems = await CompleteWordAsync(words, current, src, explicitRequest, ct).ConfigureAwait(false);
        items.AddRange(tokenItems);

        // nothing but what is already typed
        if (items.All(i => i.Insert.Trim().Length == 0 && i.Delete == 0)) items.Clear();
        return new CompletionResult(items.Take(MaxItems).ToList(), ghost);
    }

    private static async Task<List<CompletionItem>> CompleteWordAsync(List<ShellWord> words, ShellWord current, CompletionSource src,
        bool explicitRequest, CancellationToken ct)
    {
        var typed = current.Value;
        if (current.Redirect) return await PathsAsync(current, src, dirsOnly: false, explicitRequest, ct).ConfigureAwait(false);

        // skip VAR=value assignments and prefix commands (sudo -u user, watch -n 2 …)
        var i = 0;
        while (i < words.Count)
        {
            var w = words[i].Value;
            if (IsAssignment(w)) { i++; continue; }
            if (Prefixes.TryGetValue(w, out var withValue))
            {
                i++;
                while (i < words.Count && words[i].Value.StartsWith('-'))
                {
                    i += withValue.Contains(words[i].Value) ? 2 : 1;
                }
                if (w == "timeout" && i < words.Count) i++; // the duration
                continue;
            }
            break;
        }
        if (i >= words.Count)
        {
            if (typed.Contains('/') || typed.StartsWith('~') || typed.StartsWith('.'))
                return await PathsAsync(current, src, dirsOnly: false, explicitRequest, ct).ConfigureAwait(false);
            if (typed.Length == 0 && !explicitRequest) return [];
            return Commands(current, src);
        }

        var command = words[i].Value;
        var spec = CommandSpecs.Get(command);
        var parents = new List<CommandSpec>();
        var positional = 0;
        FlagSpec? pendingFlag = null;
        for (var k = i + 1; k < words.Count; k++)
        {
            var w = words[k].Value;
            pendingFlag = null;
            if (spec == null) { positional++; continue; }
            if (w.StartsWith('-') && w.Length > 1)
            {
                if (!w.Contains('=') && FindFlag(spec, parents, w) is { TakesValue: true } f)
                {
                    if (k + 1 < words.Count) k++;
                    else pendingFlag = f;
                }
                continue;
            }
            if (positional == 0 && spec.Subs != null && spec.Subs.TryGetValue(w, out var sub))
            {
                parents.Add(spec);
                spec = sub;
                continue;
            }
            positional++;
        }

        // value of a flag: "-u ngi", "--unit=ngi"
        if (pendingFlag != null) return await ValuesAsync(pendingFlag.Arg!, current, current.Value, 0, src, explicitRequest, ct).ConfigureAwait(false);
        if (spec != null && typed.StartsWith('-') && typed.IndexOf('=') is var eq and > 0 &&
            FindFlag(spec, parents, typed[..eq]) is { TakesValue: true } inline)
            return await ValuesAsync(inline.Arg!, current, typed[(eq + 1)..], eq + 1, src, explicitRequest, ct).ConfigureAwait(false);

        if (spec == null)
            return typed.Length == 0 && !explicitRequest ? [] : await PathsAsync(current, src, dirsOnly: false, explicitRequest, ct).ConfigureAwait(false);

        if (typed.StartsWith('-')) return Flags(current, spec, parents);
        // "docker logs nginx ": the argument is there, a list for another one only on Ctrl+Space
        if (typed.Length == 0 && !explicitRequest && positional > 0) return [];

        var items = new List<CompletionItem>();
        if (positional == 0 && spec.Subs != null)
        {
            items.AddRange(Rank(current, spec.Subs.Select(s => new Candidate(s.Key, s.Value.Description)), "sub"));
            if (spec.Args == null) return items;
        }
        // value-like presets: ps aux, chmod +x / 644
        items.AddRange(Rank(current, spec.Flags.Where(f => f.IsPreset).Select(f => new Candidate(f.Names[0], f.Description)), "flag"));
        items.AddRange(await ValuesAsync(spec.Args ?? "path", current, typed, 0, src, explicitRequest, ct).ConfigureAwait(false));
        return items;
    }

    private static FlagSpec? FindFlag(CommandSpec spec, List<CommandSpec> parents, string name) =>
        spec.FindFlag(name) ?? parents.Select(p => p.FindFlag(name)).LastOrDefault(f => f != null);

    private static bool IsAssignment(string w)
    {
        var eq = w.IndexOf('=');
        return eq > 0 && w[..eq].All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(w[0]);
    }

    private static List<CompletionItem> Commands(ShellWord current, CompletionSource src)
    {
        var typed = current.Value;
        var list = new List<(CompletionItem Item, int Score, int Use)>();
        foreach (var c in src.Commands.Concat(CommandSpecs.Commands.Keys).Distinct(StringComparer.Ordinal))
        {
            var score = Score(c, typed);
            if (score == 0) continue;
            var detail = CommandSpecs.Get(c)?.Description;
            list.Add((Make(current, c, "command", detail, false), score, src.CommandUse.GetValueOrDefault(c)));
        }
        return list.OrderByDescending(x => x.Score).ThenByDescending(x => x.Use).ThenBy(x => x.Item.Label.Length)
            .ThenBy(x => x.Item.Label, StringComparer.Ordinal).Take(MaxItems).Select(x => x.Item).ToList();
    }

    private static List<CompletionItem> Flags(ShellWord current, CommandSpec spec, List<CommandSpec> parents)
    {
        var typed = current.Value;
        var result = new List<(CompletionItem, int)>();
        var seen = new HashSet<string>();
        foreach (var f in spec.Flags.Concat(parents.AsEnumerable().Reverse().SelectMany(p => p.Flags)))
        {
            if (f.IsPreset) continue;
            // the long name for "--", the first matching name otherwise
            var name = f.Names.Where(n => !typed.StartsWith("--") || n.StartsWith("--"))
                .OrderByDescending(n => Score(n, typed)).ThenBy(n => typed.StartsWith("--") ? 0 : n.Length).FirstOrDefault();
            if (name == null || Score(name, typed) is var score && score == 0 || !seen.Add(name)) continue;
            result.Add((Make(current, name, "flag", Join(f), false) with { Label = string.Join(", ", f.Names) }, score));
        }
        return result.OrderByDescending(x => x.Item2).Select(x => x.Item1).ToList();

        static string? Join(FlagSpec f) => f.Description;
    }

    private static async Task<List<CompletionItem>> ValuesAsync(string kind, ShellWord current, string typed, int offset,
        CompletionSource src, bool explicitRequest, CancellationToken ct)
    {
        switch (kind)
        {
            case "none":
                return explicitRequest ? await PathsAsync(current, src, false, explicitRequest, ct).ConfigureAwait(false) : [];
            case "dir":
                return await PathsAsync(current, src, dirsOnly: true, explicitRequest, ct).ConfigureAwait(false);
            case "command":
                return typed.Length == 0 && !explicitRequest ? [] : Commands(current, src);
            case "container":
                return Rank(current, src.Containers, "container", typed, offset);
            case "image":
                return Rank(current, src.Images, "image", typed, offset);
            case "service":
                return Rank(current, src.Services, "service", typed, offset);
            case "compose-service":
            {
                var here = src.ComposeServices.Where(s => s.Dir != null && src.Cwd != null && SamePath(s.Dir, src.Cwd)).ToList();
                var list = (here.Count > 0 ? here : src.ComposeServices).Select(s => s.Service).Distinct()
                    .Select(s => new Candidate(s, here.Count > 0 ? null : src.ComposeServices.First(x => x.Service == s).Dir));
                return Rank(current, list, "service", typed, offset);
            }
            case "package" or "installed" or "branch":
            {
                if (src.Query == null || kind != "branch" && typed.Length < 2) return [];
                var found = await src.Query(kind, typed, ct).ConfigureAwait(false);
                return Rank(current, found, kind == "branch" ? "branch" : "package", typed, offset);
            }
            default:
                return typed.Length == 0 && !explicitRequest ? [] : await PathsAsync(current, src, dirsOnly: false, explicitRequest, ct).ConfigureAwait(false);
        }
    }

    private static bool SamePath(string a, string b) => a.TrimEnd('/') == b.TrimEnd('/');

    private static List<CompletionItem> Rank(ShellWord current, IEnumerable<Candidate> candidates, string kind, string? typed = null,
        int offset = 0)
    {
        typed ??= current.Value;
        return candidates
            .Select(c => (c, Score(c.Name, typed)))
            .Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2).ThenBy(x => x.c.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(x => x.c.Name)
            .Take(MaxItems)
            .Select(x => Make(current, current.Value[..offset] + x.c.Name, kind, x.c.Detail, false) with { Label = x.c.Name })
            .ToList();
    }

    // ---------- files ----------

    private static async Task<List<CompletionItem>> PathsAsync(ShellWord current, CompletionSource src, bool dirsOnly, bool explicitRequest,
        CancellationToken ct)
    {
        if (src.ListDirectory == null) return [];
        var typed = current.Value;
        // "--file=path": complete after the '='
        var offset = typed.StartsWith('-') && typed.IndexOf('=') is var eq and > 0 ? eq + 1 : 0;
        if (offset == 0 && typed.StartsWith('-')) return [];
        var value = typed[offset..];
        var slash = value.LastIndexOf('/');
        var dirPart = slash >= 0 ? value[..(slash + 1)] : "";
        var name = slash >= 0 ? value[(slash + 1)..] : value;
        if (dirPart.Length == 0 && name == "~") return [new CompletionItem("~/", "dir", src.Home, 0, "/")];
        var dir = Resolve(dirPart, src);
        if (dir == null) return [];
        var entries = await src.ListDirectory(dir, ct).ConfigureAwait(false);
        if (entries == null) return [];
        var list = new List<(CompletionItem Item, int Score, bool Dir)>();
        foreach (var e in entries)
        {
            var isDir = e.EndsWith('/');
            if (dirsOnly && !isDir) continue;
            var n = isDir ? e[..^1] : e;
            if (n.Length == 0 || n.StartsWith('.') && !name.StartsWith('.')) continue;
            var score = Score(n, name);
            if (score == 0) continue;
            var item = MakePath(current, typed[..offset] + dirPart, n, isDir);
            list.Add((item, score, isDir));
        }
        return list.OrderByDescending(x => x.Score).ThenByDescending(x => x.Dir).ThenBy(x => x.Item.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxItems).Select(x => x.Item).ToList();
    }

    /// <summary>Absolute directory of the typed dir part: ~ = home, relative = the shell's directory.</summary>
    internal static string? Resolve(string dirPart, CompletionSource src)
    {
        string path;
        if (dirPart.StartsWith('/')) path = dirPart;
        else if (dirPart == "~" || dirPart.StartsWith("~/")) path = src.Home == null ? "" : src.Home.TrimEnd('/') + dirPart[1..];
        else if (dirPart.StartsWith('~')) return null; // ~user
        else if (src.Cwd == null) return null;
        else path = src.Cwd.TrimEnd('/') + "/" + dirPart;
        if (path.Length == 0 || path.Contains('$') || path.Contains('`')) return null;
        return Normalize(path);
    }

    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return "/" + string.Join('/', parts);
    }

    private static CompletionItem MakePath(ShellWord current, string rawPrefix, string name, bool isDir)
    {
        // the typed prefix (dir part) stays as typed; the name is escaped (or quoted like the word)
        var nameRaw = current.Quote != '\0' ? name : Escape(name);
        var full = RawPrefix(current, rawPrefix) + nameRaw + (isDir ? "/" : current.Quote != '\0' ? current.Quote.ToString() : "");
        return Replace(current, full, isDir ? "" : " ", isDir ? name + "/" : name, isDir ? "dir" : "file", null);
    }

    /// <summary>The raw text of the word up to <paramref name="valuePrefix"/> (quotes and escapes kept as typed).</summary>
    private static string RawPrefix(ShellWord current, string valuePrefix)
    {
        if (valuePrefix.Length == 0) return current.Quote != '\0' ? current.Quote.ToString() : "";
        // the raw text that produced valuePrefix: walk the raw word until that many value characters
        var raw = current.Raw;
        var sb = new StringBuilder();
        int produced = 0, i = 0;
        char quote = '\0';
        while (i < raw.Length && produced < valuePrefix.Length)
        {
            var c = raw[i];
            if (quote == '\0' && c is '\'' or '"') { quote = c; sb.Append(c); i++; continue; }
            if (quote != '\0' && c == quote) { quote = '\0'; sb.Append(c); i++; continue; }
            if (c == '\\' && quote != '\'' && i + 1 < raw.Length) { sb.Append(c).Append(raw[i + 1]); i += 2; produced++; continue; }
            sb.Append(c);
            i++;
            produced++;
        }
        return sb.ToString();
    }

    private static CompletionItem Make(ShellWord current, string value, string kind, string? detail, bool isDir)
    {
        var raw = current.Quote != '\0' ? current.Quote + value + current.Quote : Escape(value);
        return Replace(current, raw, isDir ? "" : " ", value, kind, detail);
    }

    /// <summary>Only the missing part when the word is a prefix of the new text, otherwise erase the word first.</summary>
    private static CompletionItem Replace(ShellWord current, string raw, string suffix, string label, string kind, string? detail) =>
        raw.StartsWith(current.Raw, StringComparison.Ordinal)
            ? new CompletionItem(label, kind, detail, 0, raw[current.Raw.Length..] + suffix)
            : new CompletionItem(label, kind, detail, current.Raw.Length, raw + suffix);

    internal static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (" \t'\"\\$`!&;|<>()*?[]#{}".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>4 exact-case prefix, 3 prefix, 1 substring (from 2 typed characters), 0 no match.</summary>
    internal static int Score(string candidate, string typed)
    {
        if (typed.Length == 0) return 2;
        if (candidate.StartsWith(typed, StringComparison.Ordinal)) return 4;
        if (candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 3;
        return typed.Length >= 2 && candidate.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
}

/// <param name="Value">Unquoted, unescaped text.</param>
/// <param name="Raw">The text as typed.</param>
/// <param name="Quote">Open quote at the cursor (' or "), '\0' when none.</param>
/// <param name="Redirect">Follows &gt; or &lt;.</param>
public sealed record ShellWord(string Value, string Raw, char Quote, bool Redirect);

/// <summary>Splits the text before the cursor into the words of the current simple command.</summary>
public static class ShellWords
{
    /// <param name="current">The word at the cursor ("" after a space); null when the cursor is somewhere
    /// completion cannot follow (a comment, an unfinished $( or `).</param>
    /// <returns>The complete words before it, from the start of the current command (after | ; &amp;&amp; …).</returns>
    public static List<ShellWord> Parse(string text, out ShellWord? current)
    {
        var words = new List<ShellWord>();
        var value = new StringBuilder();
        var raw = new StringBuilder();
        var inWord = false;
        var redirectNext = false;
        var wordRedirect = false;
        char quote = '\0';

        void End()
        {
            if (!inWord) return;
            words.Add(new ShellWord(value.ToString(), raw.ToString(), '\0', wordRedirect));
            value.Clear();
            raw.Clear();
            inWord = false;
        }

        void Start()
        {
            if (inWord) return;
            inWord = true;
            wordRedirect = redirectNext;
            redirectNext = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                raw.Append(c);
                if (c == '\'') quote = '\0';
                else value.Append(c);
                continue;
            }
            if (quote == '"')
            {
                raw.Append(c);
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                {
                    raw.Append(text[++i]);
                    value.Append(text[i]);
                }
                else value.Append(c);
                continue;
            }
            switch (c)
            {
                case '\\' when i + 1 < text.Length:
                    Start();
                    raw.Append(c).Append(text[++i]);
                    value.Append(text[i]);
                    break;
                case '\'' or '"':
                    Start();
                    quote = c;
                    raw.Append(c);
                    break;
                case ' ' or '\t' or '\n':
                    End();
                    break;
                case '|' or ';' or '&' or '(' or ')' or '{' or '}' when !inWord || c is not ('{' or '}'):
                    End();
                    words.Clear();
                    redirectNext = false;
                    break;
                case '>' or '<':
                    // "2>" / "2>&1": the fd number is not a word, "&1" is not a new command
                    if (inWord && raw.Length > 0 && raw.ToString().All(char.IsAsciiDigit))
                    {
                        value.Clear();
                        raw.Clear();
                        inWord = false;
                    }
                    End();
                    if (i + 1 < text.Length && text[i + 1] == '&')
                    {
                        i++;
                        while (i + 1 < text.Length && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '-')) i++;
                        break;
                    }
                    if (i + 1 < text.Length && text[i + 1] is '>' or '<') i++;
                    redirectNext = true;
                    break;
                case '#' when !inWord:
                    current = null;
                    return words;
                case '`':
                    End();
                    words.Clear();
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    End();
                    words.Clear();
                    i++;
                    break;
                default:
                    Start();
                    raw.Append(c);
                    value.Append(c);
                    break;
            }
        }
        if (inWord || quote != '\0')
        {
            current = new ShellWord(value.ToString(), raw.ToString(), quote, wordRedirect);
        }
        else current = new ShellWord("", "", '\0', redirectNext);
        return words;
    }
}
