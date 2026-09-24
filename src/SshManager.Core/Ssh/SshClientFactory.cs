using Renci.SshNet;
using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Ssh;

public sealed record HostKeyInfo(string Host, int Port, string KeyType, string Fingerprint, HostKeyStatus Status);

/// <summary>The server's host key is unknown or changed and nobody confirmed it (background connections).</summary>
public sealed class HostKeyNotTrustedException(string host) : Exception(L.F("Ssh.HostKeyNotTrusted", host));

/// <summary>SSH.NET connections with the vault's credentials and our known_hosts.</summary>
public sealed class SshClientFactory(VaultService vault, KnownHostsService knownHosts)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Asks the user to trust an unknown or changed host key. Called on a background thread.</summary>
    public Func<HostKeyInfo, bool>? ConfirmHostKey { get; set; }

    public KeyEntry? FindKey(Guid? id) =>
        id is { } k ? vault.Read(d => d.Keys.FirstOrDefault(x => x.Id == k)) : null;

    /// <summary>Connects with the server's configured authentication.</summary>
    /// <param name="interactive">false: never ask about host keys, unknown keys fail the connection.</param>
    public SshClient Connect(ServerEntry server, bool interactive = true) =>
        Connect(server, server.Auth == AuthMode.Key ? RequireKey(server) : null, interactive);

    /// <param name="key">Key to log in with; null = the stored password.</param>
    public SshClient Connect(ServerEntry server, KeyEntry? key, bool interactive = true) =>
        Open(server, key, interactive, info => new SshClient(info));

    public SftpClient ConnectSftp(ServerEntry server, bool interactive = true) =>
        Open(server, server.Auth == AuthMode.Key ? RequireKey(server) : null, interactive, info => new SftpClient(info));

    private KeyEntry RequireKey(ServerEntry server) =>
        FindKey(server.KeyId) ?? throw new InvalidOperationException(L.F("Launch.NoKey", server.Name));

    private T Open<T>(ServerEntry server, KeyEntry? key, bool interactive, Func<ConnectionInfo, T> create) where T : BaseClient
    {
        AuthenticationMethod[] methods;
        if (key != null)
        {
            methods = [new PrivateKeyAuthenticationMethod(server.Username, KeyService.ToSshNet(key))];
        }
        else
        {
            var password = server.Password ?? "";
            var kbd = new KeyboardInteractiveAuthenticationMethod(server.Username);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = password;
            };
            methods = [new PasswordAuthenticationMethod(server.Username, password), kbd];
        }

        var info = new ConnectionInfo(server.Host, server.Port, server.Username, methods) { Timeout = Timeout };
        var client = create(info);
        var rejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            var blob = e.HostKey;
            var status = knownHosts.Check(server.Host, server.Port, blob);
            if (status == HostKeyStatus.Match)
            {
                e.CanTrust = true;
                return;
            }
            var hk = new HostKeyInfo(server.Host, server.Port, KnownHostsService.KeyType(blob),
                SshKeyPair.FingerprintOf(blob), status);
            e.CanTrust = interactive && ConfirmHostKey?.Invoke(hk) == true;
            if (!e.CanTrust)
            {
                rejected = true;
                return;
            }
            if (status == HostKeyStatus.Mismatch) knownHosts.Replace(server.Host, server.Port, blob);
            else knownHosts.Add(server.Host, server.Port, blob);
        };
        try
        {
            client.Connect();
        }
        catch (Exception) when (rejected)
        {
            client.Dispose();
            throw new HostKeyNotTrustedException(KnownHostsService.HostPattern(server.Host, server.Port));
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }
}
