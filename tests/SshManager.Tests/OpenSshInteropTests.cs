using SshManager.Core.Agent;
using SshManager.Core.Crypto;
using SshManager.Core.Storage;

namespace SshManager.Tests;

/// <summary>Checks our formats and agent against the real Windows OpenSSH tools.</summary>
public class OpenSshInteropTests
{
    [Theory]
    [InlineData(KeyAlgorithm.Ed25519)]
    [InlineData(KeyAlgorithm.Rsa4096)]
    public void SshKeygen_Reads_Our_Keys(KeyAlgorithm alg)
    {
        if (!OpenSsh.Available) return;
        using var tmp = new TempDir();
        var entry = KeyService.Generate("t", "test@sshm", alg);

        var pub = tmp.File("id.pub");
        File.WriteAllText(pub, entry.PublicKey + "\n");
        var fp = OpenSsh.Run("ssh-keygen", ["-l", "-E", "sha256", "-f", pub]);
        Assert.Equal(0, fp.Code);
        Assert.Contains(entry.Fingerprint, fp.Out);

        var priv = tmp.File("id");
        FileAcl.WritePrivate(priv, entry.PrivateKey);
        var y = OpenSsh.Run("ssh-keygen", ["-y", "-f", priv]);
        Assert.True(y.Code == 0, y.Err);
        Assert.StartsWith(string.Join(' ', entry.PublicKey.Split(' ').Take(2)), y.Out);
    }

    [Theory]
    [InlineData("ed25519", "")]
    [InlineData("rsa", "")]
    [InlineData("ed25519", "pass phrase")]
    public void Imports_SshKeygen_Keys(string type, string passphrase)
    {
        if (!OpenSsh.Available) return;
        using var tmp = new TempDir();
        var path = tmp.File("key");
        var gen = OpenSsh.Run("ssh-keygen", ["-q", "-t", type, "-b", type == "rsa" ? "2048" : "256", "-N", passphrase, "-C", "imp", "-f", path]);
        Assert.True(gen.Code == 0, gen.Err);

        var text = File.ReadAllText(path);
        if (passphrase.Length > 0)
            Assert.Throws<EncryptedKeyException>(() => KeyService.Import(text, "k", null));
        var entry = KeyService.Import(text, "k", passphrase.Length > 0 ? passphrase : null);

        var expected = File.ReadAllText(path + ".pub").Split(' ').Take(2);
        Assert.Equal(string.Join(' ', expected), string.Join(' ', entry.PublicKey.Split(' ').Take(2)));
    }

    [Fact]
    public void SshAdd_Lists_And_Agent_Signs()
    {
        if (!OpenSsh.Available) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var ed = KeyService.Generate("ed", "ed@test", KeyAlgorithm.Ed25519);
        var rsa = KeyService.Generate("rsa", "rsa@test", KeyAlgorithm.Rsa4096);
        vault.Update(d =>
        {
            d.Keys.Add(ed);
            d.Keys.Add(rsa);
        });

        var pipe = "sshm-test-" + Guid.NewGuid().ToString("N");
        using var agent = new SshAgentServer(vault);
        Assert.True(agent.StartOn(pipe));
        var env = new Dictionary<string, string> { ["SSH_AUTH_SOCK"] = @"\\.\pipe\" + pipe };

        var list = OpenSsh.Run("ssh-add", ["-L"], env);
        Assert.True(list.Code == 0, list.Err);
        Assert.Contains(ed.PublicKey, list.Out);
        Assert.Contains(rsa.PublicKey, list.Out);

        // Sign with the agent-held key and verify the signature with ssh-keygen.
        foreach (var key in new[] { ed, rsa })
        {
            var pub = tmp.File(key.Name + ".pub");
            File.WriteAllText(pub, key.PublicKey + "\n");
            var data = tmp.File("data.txt");
            File.WriteAllText(data, "hello from sshm");
            File.Delete(data + ".sig");
            var sign = OpenSsh.Run("ssh-keygen", ["-Y", "sign", "-n", "file", "-f", pub, data], env);
            Assert.True(sign.Code == 0, sign.Err);
            var check = OpenSsh.Run("ssh-keygen", ["-Y", "check-novalidate", "-n", "file", "-s", data + ".sig"], env,
                stdin: File.ReadAllText(data));
            Assert.True(check.Code == 0, check.Err + check.Out);
        }

        vault.Lock();
        agent.ClearCache();
        var locked = OpenSsh.Run("ssh-add", ["-L"], env);
        Assert.DoesNotContain(ed.PublicKey, locked.Out);
    }
}
