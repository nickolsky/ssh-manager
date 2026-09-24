using Renci.SshNet;
using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Ssh;

public sealed record HostKeyInfo(string Host, int Port, string KeyType, string Fingerprint, HostKeyStatus Status);

/// <summary>Uses SSH.NET for non-interactive operations: connection test and "password → key" setup.</summary>
public sealed class KeySetupService(VaultService vault, KnownHostsService knownHosts)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Asks the user to trust an unknown or changed host key. Called on a background thread.</summary>
    public Func<HostKeyInfo, bool>? ConfirmHostKey { get; set; }

    public string Test(ServerEntry server)
    {
        using var client = Connect(server, server.Auth == AuthMode.Key ? FindKey(server.KeyId) : null);
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
            throw new InvalidOperationException("Для настройки нужен сохранённый пароль сервера");

        log($"Подключаюсь к {server.Display} по паролю…");
        using var client = Connect(server, key: null);
        log("Вход по паролю выполнен.");

        KeyEntry key;
        bool isNew;
        if (existingKeyId is { } id)
        {
            key = FindKey(id) ?? throw new InvalidOperationException("Выбранный ключ не найден");
            isNew = false;
            log($"Использую существующий ключ «{key.Name}» ({key.Fingerprint}).");
        }
        else
        {
            var comment = KeyService.SanitizeComment($"{server.Username}@{server.Name}-{DateTime.Now:yyyyMMdd}");
            key = KeyService.Generate($"{server.Username}@{server.Name}", comment, KeyAlgorithm.Ed25519);
            isNew = true;
            log($"Сгенерирован новый ключ Ed25519 ({key.Fingerprint}).");
        }

        var pub = key.PublicKey.Trim();
        if (pub.Contains('\'')) throw new InvalidOperationException("Недопустимый символ в публичном ключе");
        var script =
            "umask 077; mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && " +
            "chmod 600 ~/.ssh/authorized_keys && " +
            $"(grep -qxF '{pub}' ~/.ssh/authorized_keys || echo '{pub}' >> ~/.ssh/authorized_keys) && echo SSHM_OK";
        log("Добавляю ключ в ~/.ssh/authorized_keys…");
        var cmd = client.RunCommand(script);
        if (!cmd.Result.Contains("SSHM_OK"))
            throw new InvalidOperationException("Не удалось записать authorized_keys: " + (cmd.Error + cmd.Result).Trim());

        if (isNew) vault.Update(d => d.Keys.Add(key));
        KeyService.EnsurePublicKeyFile(key);
        log("Ключ установлен. Проверяю вход по ключу…");

        using (var check = Connect(server, key))
        {
            var r = check.RunCommand("echo SSHM_KEY_OK");
            if (!r.Result.Contains("SSHM_KEY_OK"))
                throw new InvalidOperationException("Проверка входа по ключу не прошла");
        }
        log("Вход по ключу работает.");

        vault.Update(d =>
        {
            var s = d.Servers.First(x => x.Id == server.Id);
            s.Auth = AuthMode.Key;
            s.KeyId = key.Id;
        });
        log("Сервер переключён на авторизацию по ключу. Пароль остаётся сохранённым — его можно удалить в настройках сервера.");
        return key;
    }

    private KeyEntry? FindKey(Guid? id) =>
        id is { } k ? vault.Read(d => d.Keys.FirstOrDefault(x => x.Id == k)) : null;

    private SshClient Connect(ServerEntry server, KeyEntry? key)
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
        var client = new SshClient(info);
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
            e.CanTrust = ConfirmHostKey?.Invoke(hk) == true;
            if (!e.CanTrust) return;
            if (status == HostKeyStatus.Mismatch) knownHosts.Replace(server.Host, server.Port, blob);
            else knownHosts.Add(server.Host, server.Port, blob);
        };
        try
        {
            client.Connect();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }
}
