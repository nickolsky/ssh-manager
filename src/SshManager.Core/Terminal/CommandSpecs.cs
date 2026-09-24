using System.Text.Json;

namespace SshManager.Core.Terminal;

/// <summary>A command or subcommand from specs.json: what follows it (subcommands, flags, the kind of arguments).</summary>
public sealed class CommandSpec
{
    public string? Description { get; init; }
    /// <summary>path, dir, container, image, service, package, installed, branch, compose-service, command, none.</summary>
    public string? Args { get; init; }
    public Dictionary<string, CommandSpec>? Subs { get; init; }
    public List<FlagSpec> Flags { get; init; } = [];

    public FlagSpec? FindFlag(string name) => Flags.FirstOrDefault(f => f.Names.Contains(name));
}

/// <param name="Names">"-f", "--follow"; value-like presets ("aux", "+x", "644") too.</param>
/// <param name="Arg">Kind of the flag's value, null = no value.</param>
public sealed record FlagSpec(string[] Names, string? Arg, string? Description)
{
    public bool TakesValue => Arg != null;
    public bool IsPreset => !Names[0].StartsWith('-');
}

/// <summary>Command specs shipped in Terminal\specs.json.</summary>
public static class CommandSpecs
{
    private static readonly Lazy<Dictionary<string, CommandSpec>> All = new(Load);

    public static CommandSpec? Get(string command) => All.Value.GetValueOrDefault(command);

    public static IReadOnlyDictionary<string, CommandSpec> Commands => All.Value;

    private static Dictionary<string, CommandSpec> Load()
    {
        using var s = typeof(CommandSpecs).Assembly.GetManifestResourceStream("SshManager.Core.Terminal.specs.json")
                      ?? throw new InvalidOperationException("specs.json is not embedded");
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var raw = doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var result = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);
        foreach (var name in raw.Keys) result[name] = Parse(raw[name], raw, 0);
        return result;
    }

    private static CommandSpec Parse(JsonElement e, Dictionary<string, JsonElement> top, int depth)
    {
        string? S(JsonElement x, string n) => x.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var description = S(e, "d");
        // "ref": the same spec as another top-level command (docker compose = docker-compose, apt-get = apt)
        if (S(e, "ref") is { } r && top.TryGetValue(r, out var target) && depth < 4)
        {
            var spec = Parse(target, top, depth + 1);
            return new CommandSpec { Description = description ?? spec.Description, Args = spec.Args, Subs = spec.Subs, Flags = spec.Flags };
        }
        Dictionary<string, CommandSpec>? subs = null;
        if (e.TryGetProperty("subs", out var subsElement) && subsElement.ValueKind == JsonValueKind.Object)
            subs = subsElement.EnumerateObject().ToDictionary(p => p.Name, p => Parse(p.Value, top, depth + 1), StringComparer.Ordinal);
        var flags = new List<FlagSpec>();
        if (e.TryGetProperty("flags", out var flagsElement) && flagsElement.ValueKind == JsonValueKind.Array)
            foreach (var f in flagsElement.EnumerateArray())
                if (f.GetString() is { Length: > 0 } text)
                    flags.Add(ParseFlag(text));
        return new CommandSpec { Description = description, Args = S(e, "args"), Subs = subs, Flags = flags };
    }

    /// <summary>"-u,--unit=service|Unit": names, value kind after '=' (empty = a value without suggestions), description.</summary>
    internal static FlagSpec ParseFlag(string text)
    {
        var bar = text.IndexOf('|');
        var left = bar < 0 ? text : text[..bar];
        var description = bar < 0 ? null : text[(bar + 1)..];
        string? arg = null;
        var eq = left.IndexOf('=');
        if (eq >= 0)
        {
            arg = left[(eq + 1)..] is { Length: > 0 } k ? k : "none";
            left = left[..eq];
        }
        return new FlagSpec(left.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), arg, description);
    }
}
