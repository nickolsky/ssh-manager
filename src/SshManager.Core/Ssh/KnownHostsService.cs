using SshManager.Core.Crypto;

namespace SshManager.Core.Ssh;

public enum HostKeyStatus
{
    Unknown,
    Match,
    Mismatch,
}

/// <summary>App-managed known_hosts (plain OpenSSH format) shared by SSH.NET checks and ssh.exe.</summary>
public sealed class KnownHostsService(string? file = null)
{
    private readonly string _file = file ?? AppPaths.KnownHostsFile;
    private readonly object _sync = new();

    public string FilePath => _file;

    public static string HostPattern(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    /// <summary>Key type from the blob itself (the negotiated algorithm may be rsa-sha2-*, known_hosts wants ssh-rsa).</summary>
    public static string KeyType(byte[] blob) => new SshReader(blob).StringUtf8();

    public HostKeyStatus Check(string host, int port, byte[] blob)
    {
        var pattern = HostPattern(host, port);
        var type = KeyType(blob);
        var b64 = Convert.ToBase64String(blob);
        var status = HostKeyStatus.Unknown;
        foreach (var (hosts, t, key) in Entries())
        {
            if (!hosts.Split(',').Contains(pattern, StringComparer.OrdinalIgnoreCase) || t != type) continue;
            if (key == b64) return HostKeyStatus.Match;
            status = HostKeyStatus.Mismatch;
        }
        return status;
    }

    public void Add(string host, int port, byte[] blob)
    {
        var line = $"{HostPattern(host, port)} {KeyType(blob)} {Convert.ToBase64String(blob)}";
        lock (_sync)
        {
            var needsNewline = File.Exists(_file) && new FileInfo(_file).Length > 0 &&
                               !File.ReadAllText(_file).EndsWith('\n');
            File.AppendAllText(_file, (needsNewline ? "\n" : "") + line + "\n");
        }
    }

    /// <summary>Replaces all keys of this type for the host (after the user confirmed a changed key).</summary>
    public void Replace(string host, int port, byte[] blob)
    {
        var pattern = HostPattern(host, port);
        var type = KeyType(blob);
        lock (_sync)
        {
            if (File.Exists(_file))
            {
                var kept = File.ReadAllLines(_file).Where(l =>
                {
                    var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length < 3 || parts[1] != type ||
                           !parts[0].Split(',').Contains(pattern, StringComparer.OrdinalIgnoreCase);
                });
                File.WriteAllLines(_file, kept);
            }
        }
        Add(host, port, blob);
    }

    private IEnumerable<(string Hosts, string Type, string Key)> Entries()
    {
        string[] lines;
        lock (_sync)
            lines = File.Exists(_file) ? File.ReadAllLines(_file) : [];
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#') || l.StartsWith('@')) continue;
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) yield return (parts[0], parts[1], parts[2]);
        }
    }
}
