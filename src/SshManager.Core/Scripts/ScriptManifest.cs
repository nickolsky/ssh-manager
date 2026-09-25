using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Models;

namespace SshManager.Core.Scripts;

public enum ScriptParamType
{
    Text,
    Number,
    Choice,
    Bool,
    /// <summary>Masked input, never stored in the run history.</summary>
    Secret,
}

/// <summary>Input of a script, passed to it as an environment variable.</summary>
public sealed record ScriptParam(
    string Name, ScriptParamType Type, string Label, string? Default, IReadOnlyList<string> Options,
    string? Hint, bool Required, string? WhenName, string? WhenValue)
{
    /// <summary>Value a bool parameter gets when checked / unchecked.</summary>
    public const string True = "1", False = "0";

    /// <summary>Shown only when another parameter has a given value (e.g. XHTTP path only for the xhttp transport).</summary>
    public bool IsVisible(IReadOnlyDictionary<string, string> values) =>
        WhenName == null || (values.TryGetValue(WhenName, out var v) && string.Equals(v, WhenValue, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Value a script reports back; stored as a server attribute.</summary>
/// <param name="MonitorName">When set, the value is a port the app starts monitoring under this name.</param>
/// <param name="Template">Value computed by the app after a successful run, e.g. "http://${SSHM_HOST}:${PORT}".</param>
/// <param name="Name">"AWG_KEY_*" (a trailing star) covers every result starting with "AWG_KEY_": one per client and so on.</param>
public sealed record ScriptResultDef(string Name, string Label, string? MonitorName, string? Template = null)
{
    public bool IsPattern => Name.EndsWith('*');

    public bool Covers(string key) => IsPattern ? key.Length > Name.Length - 1 && key.StartsWith(Name[..^1], StringComparison.Ordinal) : key == Name;
}

/// <summary>
/// Metadata in the script's comment lines:
/// <code>
/// # @name    VLESS REALITY (Docker)
/// # @os      ubuntu,debian:12
/// # @group   VPN
/// # @param   XRAY_PORT number label="Порт" label_en="Port" default=443 required
/// # @param   XRAY_TRANSPORT choice options=xhttp,tcp default=xhttp
/// # @param   XHTTP_PATH text default=/xhttp when=XRAY_TRANSPORT=xhttp
/// # @result  VLESS_URL label="Ссылка VLESS"
/// # @result  XRAY_PORT label="Порт Xray" monitor=Xray
/// # @result  AWG_KEY_* label="Amnezia VPN"          (AWG_KEY_phone, AWG_KEY_laptop… labelled "Amnezia VPN — phone")
/// </code>
/// Results are written by the script as KEY=value lines to the file named by $SSHM_RESULT.
/// </summary>
public sealed partial class ScriptManifest
{
    public string? Name { get; private set; }
    public string? Os { get; private set; }
    public string? Description { get; private set; }
    /// <summary>Submenu in "Install ▸" (VPN, Web, Cloud or any other name); null = top level.</summary>
    public string? Group { get; private set; }
    /// <summary>Compose scripts: folder name under /opt (default: derived from the name).</summary>
    public string? Project { get; private set; }
    public List<ScriptParam> Params { get; } = [];
    public List<ScriptResultDef> Results { get; } = [];

    public bool IsEmpty => Params.Count == 0 && Results.Count == 0;

    public static ScriptManifest Parse(string? body)
    {
        var m = new ScriptManifest();
        string? descRu = null, descEn = null;
        foreach (var raw in (body ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('#')) continue;
            line = line.TrimStart('#').Trim();
            if (!line.StartsWith('@')) continue;
            var space = line.IndexOfAny([' ', '\t']);
            var tag = (space < 0 ? line : line[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (tag)
            {
                case "@name":
                    m.Name = rest;
                    break;
                case "@name_en":
                    if (L.Language == L.English && rest.Length > 0) m.Name = rest;
                    break;
                case "@os":
                    m.Os = rest;
                    break;
                case "@group":
                    if (rest.Length > 0) m.Group = rest;
                    break;
                case "@project":
                    if (ProjectName().IsMatch(rest)) m.Project = rest;
                    break;
                case "@description":
                    descRu = descRu == null ? rest : descRu + "\n" + rest;
                    break;
                case "@description_en":
                    descEn = descEn == null ? rest : descEn + "\n" + rest;
                    break;
                case "@param":
                    if (ParseParam(rest) is { } p && m.Params.All(x => x.Name != p.Name)) m.Params.Add(p);
                    break;
                case "@result":
                    if (ParseResult(rest) is { } r && m.Results.All(x => x.Name != r.Name)) m.Results.Add(r);
                    break;
            }
        }
        m.Description = L.Language == L.English && descEn != null ? descEn : descRu ?? descEn;
        return m;
    }

    /// <summary>The definition of a result; for one matched by a pattern (AWG_KEY_* for AWG_KEY_phone) its label gets the rest of the name.</summary>
    public ScriptResultDef? Result(string name)
    {
        if (Results.FirstOrDefault(r => r.Name == name) is { } exact) return exact;
        if (Results.FirstOrDefault(r => r.IsPattern && r.Covers(name)) is not { } pattern) return null;
        return pattern with { Name = name, Label = $"{pattern.Label} — {name[(pattern.Name.Length - 1)..]}" };
    }

    /// <summary>
    /// Results kept from earlier runs that a pattern covers but this run no longer reported (a client that was removed):
    /// they are dropped after a successful run.
    /// </summary>
    public bool IsStale(string key, IReadOnlyDictionary<string, string> reported) =>
        !reported.ContainsKey(key) && Results.Any(r => r.IsPattern && r.Covers(key));

    private static readonly string[] KnownGroups = ["VPN", "Web", "Cloud"];

    /// <summary>Built-in groups first, in this order; the user's own after them.</summary>
    public static int GroupOrder(string group) =>
        Array.FindIndex(KnownGroups, g => g.Equals(group, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : KnownGroups.Length;

    /// <summary>Menu title of a group: translated for the built-in ones, as written otherwise.</summary>
    public static string GroupLabel(string group) => GroupOrder(group) switch
    {
        0 => L.Get("Scripts.Group.VPN"),
        1 => L.Get("Scripts.Group.Web"),
        2 => L.Get("Scripts.Group.Cloud"),
        _ => group,
    };

    /// <summary>Defaults for every parameter, overridden by <paramref name="previous"/> (last run on this server).</summary>
    public Dictionary<string, string> InitialValues(IReadOnlyDictionary<string, string>? previous)
    {
        var values = new Dictionary<string, string>();
        foreach (var p in Params)
        {
            if (p.Type != ScriptParamType.Secret && previous != null && previous.TryGetValue(p.Name, out var v)) values[p.Name] = v;
            else values[p.Name] = p.Default ?? (p.Type == ScriptParamType.Bool ? ScriptParam.False : p.Type == ScriptParamType.Choice ? p.Options.FirstOrDefault() ?? "" : "");
        }
        return values;
    }

    /// <summary>First problem with the values, or null when they can be passed to the script.</summary>
    public string? Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var p in Params.Where(p => p.IsVisible(values)))
        {
            var v = values.GetValueOrDefault(p.Name) ?? "";
            if (p.Required && string.IsNullOrWhiteSpace(v)) return L.F("ScriptRun.Required", p.Label);
            if (p.Type == ScriptParamType.Number && v.Length > 0 && !long.TryParse(v.Trim(), out _)) return L.F("ScriptRun.NotNumber", p.Label);
            if (p.Type == ScriptParamType.Choice && v.Length > 0 && p.Options.Count > 0 && !p.Options.Contains(v)) return L.F("ScriptRun.BadChoice", p.Label);
            if (v.Contains('\0')) return L.F("ScriptRun.BadChoice", p.Label);
        }
        return null;
    }

    private static ScriptParam? ParseParam(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0 || !EnvName().IsMatch(tokens[0])) return null;
        var name = tokens[0];
        var type = ScriptParamType.Text;
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens.Skip(1))
        {
            var eq = t.IndexOf('=');
            if (eq > 0) attrs[t[..eq]] = t[(eq + 1)..];
            else if (Enum.TryParse<ScriptParamType>(t, ignoreCase: true, out var parsed) && !int.TryParse(t, out _)) type = parsed;
            else if (t.Equals("password", StringComparison.OrdinalIgnoreCase)) type = ScriptParamType.Secret;
            else flags.Add(t);
        }
        var options = attrs.TryGetValue("options", out var o)
            ? o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        if (options.Length > 0 && type == ScriptParamType.Text) type = ScriptParamType.Choice;
        string? whenName = null, whenValue = null;
        if (attrs.TryGetValue("when", out var when) && when.IndexOf('=') is var w and > 0)
        {
            whenName = when[..w];
            whenValue = when[(w + 1)..];
        }
        return new ScriptParam(name, type, Localized(attrs, "label") ?? name, attrs.GetValueOrDefault("default"), options,
            Localized(attrs, "hint"), flags.Contains("required"), whenName, whenValue);
    }

    private static ScriptResultDef? ParseResult(string text)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0 || !EnvName().IsMatch(tokens[0].TrimEnd('*')) || tokens[0].Count(c => c == '*') > 1) return null;
        var attrs = tokens.Skip(1).Where(t => t.IndexOf('=') > 0)
            .ToDictionary(t => t[..t.IndexOf('=')], t => t[(t.IndexOf('=') + 1)..], StringComparer.OrdinalIgnoreCase);
        return new ScriptResultDef(tokens[0], Localized(attrs, "label") ?? tokens[0], attrs.GetValueOrDefault("monitor"),
            attrs.GetValueOrDefault("value"));
    }

    /// <summary>Results with a value= template, filled from the parameters and SSHM_* variables.</summary>
    public Dictionary<string, string> TemplateResults(IReadOnlyDictionary<string, string> values)
    {
        var d = new Dictionary<string, string>();
        foreach (var r in Results.Where(r => r.Template != null))
            d[r.Name] = TemplateVar().Replace(r.Template!, m => values.GetValueOrDefault(m.Groups[1].Value) ?? "");
        return d;
    }

    /// <summary>Folder name for a compose project: @project, else the script name in lower-case latin letters.</summary>
    public static string ProjectFor(ScriptEntry script, ScriptManifest m)
    {
        if (m.Project != null) return m.Project;
        var slug = new string(script.Name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "sshm-app" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    /// <summary>label / label_en: the English one when the UI is English.</summary>
    private static string? Localized(Dictionary<string, string> attrs, string key) =>
        L.Language == L.English && attrs.TryGetValue(key + "_en", out var en) ? en : attrs.GetValueOrDefault(key);

    /// <summary>Splits on spaces; double quotes group (key="a b" → key=a b), \" is a literal quote.</summary>
    internal static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"')
            {
                sb.Append('"');
                i++;
                any = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(sb.ToString());
                sb.Clear();
                any = false;
            }
            else
            {
                sb.Append(c);
                any = true;
            }
        }
        if (any) result.Add(sb.ToString());
        return result;
    }

    /// <summary>KEY=value lines of the result file (last value wins; bad names are ignored).</summary>
    public static Dictionary<string, string> ParseResults(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq].Trim();
            if (EnvName().IsMatch(key)) d[key] = raw[(eq + 1)..].Trim();
        }
        return d;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    internal static partial Regex EnvName();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex TemplateVar();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
    internal static partial Regex ProjectName();
}
