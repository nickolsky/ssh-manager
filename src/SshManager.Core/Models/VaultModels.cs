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
}
