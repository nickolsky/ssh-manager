using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SshManager.Core.Models;

namespace SshManager.Core.Scripts;

public sealed record BuiltinScript(string Id, string Body, ScriptKind Kind = ScriptKind.Bash)
{
    public ScriptManifest Manifest => ScriptManifest.Parse(Body);
}

/// <summary>
/// Scripts shipped inside the app (Scripts\Builtin\*.sh, embedded). They are copied into the vault so they show up
/// next to the user's scripts; an unedited copy follows app updates, an edited one is left alone.
/// </summary>
public static class BuiltinScripts
{
    private const string Prefix = "SshManager.Core.Scripts.Builtin.";

    public static IReadOnlyList<BuiltinScript> All { get; } = Load();

    private static List<BuiltinScript> Load()
    {
        var asm = typeof(BuiltinScripts).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && (n.EndsWith(".sh", StringComparison.Ordinal) || n.EndsWith(".yml", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .Select(n => n.EndsWith(".yml", StringComparison.Ordinal)
                ? new BuiltinScript(n[Prefix.Length..^4], Read(asm, n), ScriptKind.Compose)
                : new BuiltinScript(n[Prefix.Length..^3], Read(asm, n)))
            .ToList();
    }

    private static string Read(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }

    public static string Hash(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n")))).ToLowerInvariant()[..16];

    /// <summary>Adds missing built-ins and refreshes unedited ones. Returns true when <paramref name="data"/> changed.</summary>
    public static bool Sync(VaultData data, IReadOnlyList<BuiltinScript>? builtins = null)
    {
        var changed = false;
        foreach (var b in builtins ?? All)
        {
            var hash = Hash(b.Body);
            var m = b.Manifest;
            var entry = data.Scripts.FirstOrDefault(s => s.BuiltinId == b.Id);
            if (entry == null)
            {
                if (data.RemovedBuiltins.Contains(b.Id)) continue;
                data.Scripts.Add(new ScriptEntry
                {
                    Name = m.Name ?? b.Id,
                    OsFilter = m.Os ?? "",
                    UseSudo = true,
                    Kind = b.Kind,
                    Body = b.Body,
                    BuiltinId = b.Id,
                    BuiltinHash = hash,
                });
                changed = true;
            }
            else if (entry.BuiltinHash != hash && Hash(entry.Body) == entry.BuiltinHash)
            {
                entry.Body = b.Body;
                entry.Kind = b.Kind;
                entry.OsFilter = m.Os ?? entry.OsFilter;
                entry.BuiltinHash = hash;
                changed = true;
            }
        }
        return changed;
    }

    public const string DockerId = "docker-install";

    /// <summary>The Docker installer: the vault copy, or the shipped one when the user deleted it.</summary>
    public static ScriptEntry DockerScript(VaultData data)
    {
        if (data.Scripts.FirstOrDefault(s => s.BuiltinId == DockerId) is { } s) return s.Clone();
        var b = All.First(x => x.Id == DockerId);
        return new ScriptEntry { Name = b.Manifest.Name ?? b.Id, Body = b.Body, BuiltinId = b.Id, UseSudo = true };
    }

    /// <summary>True when a built-in copy still has the shipped text.</summary>
    public static bool IsUnedited(ScriptEntry s) => s.BuiltinId != null && Hash(s.Body) == s.BuiltinHash;
}
