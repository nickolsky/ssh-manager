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

    public string Display => $"{Username}@{Host}" + (Port != 22 ? $":{Port}" : "");

    public ServerEntry Clone() => (ServerEntry)MemberwiseClone();
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
}

/// <summary>Install script from the settings, run on a server via "Install ▸".</summary>
public sealed class ScriptEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Comma-separated os-release IDs the script is meant for (e.g. "debian,ubuntu"); empty = any.</summary>
    public string OsFilter { get; set; } = "";
    public bool UseSudo { get; set; } = true;
    public string Body { get; set; } = "";

    public bool Matches(ServerFacts? facts)
    {
        var ids = OsFilter.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return true;
        if (facts?.OsId == null) return false;
        var own = new[] { facts.OsId }.Concat((facts.OsLike ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return ids.Any(i => own.Contains(i, StringComparer.OrdinalIgnoreCase));
    }

    public ScriptEntry Clone() => (ScriptEntry)MemberwiseClone();
}
