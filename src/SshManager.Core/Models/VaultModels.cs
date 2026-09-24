namespace SshManager.Core.Models;

public enum AuthMode
{
    Password,
    Key,
}

public sealed class ServerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public AuthMode Auth { get; set; } = AuthMode.Password;
    public string? Password { get; set; }
    public Guid? KeyId { get; set; }
    public string? JumpHost { get; set; }
    public string? ExtraArgs { get; set; }
    public string? Notes { get; set; }
    public DateTime? LastConnected { get; set; }
    /// <summary>Availability check interval in minutes: null = global default, 0 = disabled.</summary>
    public int? MonitorIntervalMinutes { get; set; }
    /// <summary>What the app learned about the server (OS, location, containers, forwards). Not user-edited.</summary>
    public ServerFacts? Facts { get; set; }
    /// <summary>TCP ports checked together with the server (e.g. 443 of a VPN, 2053 of a panel).</summary>
    public List<MonitoredPort> MonitoredPorts { get; set; } = [];
    /// <summary>Values returned by scripts (e.g. a VLESS link), latest per key.</summary>
    public List<ServerAttribute> Attributes { get; set; } = [];
    /// <summary>Recent script runs, newest last.</summary>
    public List<ScriptRun> ScriptRuns { get; set; } = [];

    public string Display => $"{Username}@{Host}" + (Port != 22 ? $":{Port}" : "");

    public ServerEntry Clone()
    {
        var c = (ServerEntry)MemberwiseClone();
        c.MonitoredPorts = MonitoredPorts.Select(p => p.Clone()).ToList();
        c.Attributes = Attributes.Select(a => a.Clone()).ToList();
        c.ScriptRuns = ScriptRuns.Select(r => r.Clone()).ToList();
        return c;
    }
}

public sealed class ServerAttribute
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>Name of the script that produced it.</summary>
    public string? Source { get; set; }
    public DateTime Updated { get; set; } = DateTime.Now;

    public ServerAttribute Clone() => (ServerAttribute)MemberwiseClone();
}

public sealed class ScriptRun
{
    public Guid ScriptId { get; set; }
    public string ScriptName { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime? Finished { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    /// <summary>Parameter values without secrets.</summary>
    public Dictionary<string, string> Params { get; set; } = [];
    public Dictionary<string, string> Results { get; set; } = [];
    /// <summary>Last lines of the output.</summary>
    public string? OutputTail { get; set; }

    public ScriptRun Clone()
    {
        var c = (ScriptRun)MemberwiseClone();
        c.Params = new Dictionary<string, string>(Params);
        c.Results = new Dictionary<string, string>(Results);
        return c;
    }
}

public sealed class MonitoredPort
{
    public int Port { get; set; }
    /// <summary>What runs there (free text or the detected process).</summary>
    public string? Name { get; set; }

    public string Label => string.IsNullOrWhiteSpace(Name) ? Port.ToString() : $"{Port} {Name}";

    public MonitoredPort Clone() => (MonitoredPort)MemberwiseClone();
}

public sealed class KeyEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>OpenSSH key type: ssh-ed25519 or ssh-rsa.</summary>
    public string Type { get; set; } = "";
    public string Comment { get; set; } = "";
    /// <summary>Single line in authorized_keys format.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>Unencrypted openssh-key-v1 PEM. Only ever stored inside the encrypted vault.</summary>
    public string PrivateKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
}

public sealed class VaultData
{
    public int Version { get; set; } = 1;
    public List<ServerEntry> Servers { get; set; } = [];
    public List<KeyEntry> Keys { get; set; } = [];
    public List<ScriptEntry> Scripts { get; set; } = [];
    /// <summary>Built-in scripts the user deleted; they are not added again.</summary>
    public List<string> RemovedBuiltins { get; set; } = [];
}

public enum ScriptKind
{
    /// <summary>A bash script.</summary>
    Bash,
    /// <summary>A docker-compose.yml: deployed to /opt/&lt;project&gt; and started with "docker compose up -d".</summary>
    Compose,
}

/// <summary>Install script from the settings, run on a server via "Install ▸".</summary>
public sealed class ScriptEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>
    /// Comma-separated os-release IDs the script is meant for, optionally with a version:
    /// "debian:12,ubuntu" = Debian 12 or any Ubuntu. Empty = any OS.
    /// </summary>
    public string OsFilter { get; set; } = "";
    public bool UseSudo { get; set; } = true;
    public ScriptKind Kind { get; set; } = ScriptKind.Bash;
    public string Body { get; set; } = "";
    /// <summary>Shipped with the app (file name of the built-in script).</summary>
    public string? BuiltinId { get; set; }
    /// <summary>Hash of the built-in body this entry was last taken from; equal to the body's hash = not edited.</summary>
    public string? BuiltinHash { get; set; }

    public static string[] SplitOs(string? filter) =>
        (filter ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Matches(ServerFacts? facts)
    {
        var ids = SplitOs(OsFilter);
        if (ids.Length == 0) return true;
        if (facts?.OsId == null) return false;
        var own = new[] { facts.OsId }.Concat((facts.OsLike ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        foreach (var token in ids)
        {
            var (id, version) = token.IndexOf(':') is var i and > 0 ? (token[..i], token[(i + 1)..]) : (token, null);
            if (version == null)
            {
                if (own.Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(facts.OsId, id, StringComparison.OrdinalIgnoreCase) && facts.OsVersion is { } v &&
                     (v == version || v.StartsWith(version + ".", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public ScriptEntry Clone() => (ScriptEntry)MemberwiseClone();
}
