using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Ssh;

/// <summary>Uses SSH.NET for non-interactive operations: connection test and "password → key" setup.</summary>
public sealed class KeySetupService(VaultService vault, SshClientFactory ssh)
{
    public string Test(ServerEntry server)
    {
        using var client = ssh.Connect(server);
        var cmd = client.RunCommand("uname -a 2>/dev/null || ver");
        return (cmd.Result + cmd.Error).Trim();
    }

    /// <summary>
    /// Logs in with the stored password, installs a key into ~/.ssh/authorized_keys, verifies key login
    /// and switches the server to key authentication.
    /// </summary>
    public KeyEntry SetupKeyAuth(ServerEntry server, Guid? existingKeyId, Action<string> log)
    {
        if (string.IsNullOrEmpty(server.Password))
            throw new InvalidOperationException(L.Get("KeySetup.NeedPassword"));

        log(L.F("KeySetup.Connecting", server.Display));
        using var client = ssh.Connect(server, key: null);
        log(L.Get("KeySetup.PasswordOk"));

        KeyEntry key;
        bool isNew;
        if (existingKeyId is { } id)
        {
            key = ssh.FindKey(id) ?? throw new InvalidOperationException(L.Get("KeySetup.KeyNotFound"));
            isNew = false;
            log(L.F("KeySetup.UsingExisting", key.Name, key.Fingerprint));
        }
        else
        {
            var comment = KeyService.SanitizeComment($"{server.Username}@{server.Name}-{DateTime.Now:yyyyMMdd}");
            key = KeyService.Generate($"{server.Username}@{server.Name}", comment, KeyAlgorithm.Ed25519);
            isNew = true;
            log(L.F("KeySetup.Generated", key.Fingerprint));
        }

        var pub = key.PublicKey.Trim();
        if (pub.Contains('\'')) throw new InvalidOperationException(L.Get("KeySetup.BadChar"));
        var script =
            "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && " +
            "chmod 600 ~/.ssh/authorized_keys && " +
            $"(grep -qxF '{pub}' ~/.ssh/authorized_keys || echo '{pub}' >> ~/.ssh/authorized_keys) && echo SSHM_OK";
        log(L.Get("KeySetup.Adding"));
        var cmd = client.RunCommand(script);
        if (!cmd.Result.Contains("SSHM_OK"))
            throw new InvalidOperationException(L.Get("KeySetup.WriteFailed") + " " + (cmd.Error + cmd.Result).Trim());

        if (isNew) vault.Update(d => d.Keys.Add(key));
        KeyService.EnsurePublicKeyFile(key);
        log(L.Get("KeySetup.Installed"));

        using (var check = ssh.Connect(server, key))
        {
            var r = check.RunCommand("echo SSHM_KEY_OK");
            if (!r.Result.Contains("SSHM_KEY_OK"))
                throw new InvalidOperationException(L.Get("KeySetup.CheckFailed"));
        }
        log(L.Get("KeySetup.KeyWorks"));

        vault.Update(d =>
        {
            var s = d.Servers.First(x => x.Id == server.Id);
            s.Auth = AuthMode.Key;
            s.KeyId = key.Id;
        });
        log(L.Get("KeySetup.Switched"));
        return key;
    }
}
