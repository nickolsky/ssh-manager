using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Tests;

public class CryptoTests
{
    [Fact]
    public void Vault_RoundTrip_And_WrongPassword()
    {
        using var tmp = new TempDir();
        var file = tmp.File("vault.dat");
        var v = new VaultService(file);
        v.Create("correct horse battery");
        v.Update(d => d.Servers.Add(new ServerEntry { Name = "web", Host = "10.0.0.1", Password = "s3cret" }));
        v.Lock();
        Assert.False(v.IsUnlocked);

        Assert.DoesNotContain("s3cret", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        Assert.Throws<WrongPasswordException>(() => new VaultService(file).Unlock("wrong password"));

        var v2 = new VaultService(file);
        v2.Unlock("correct horse battery");
        Assert.Equal("s3cret", v2.Data.Servers.Single().Password);

        v2.ChangePassword("correct horse battery", "new password 123");
        Assert.Throws<WrongPasswordException>(() => new VaultService(file).Unlock("correct horse battery"));
        new VaultService(file).Unlock("new password 123");
    }

    [Fact]
    public void Vault_Tampering_Is_Detected()
    {
        using var tmp = new TempDir();
        var file = tmp.File("vault.dat");
        new VaultService(file).Create("password123");
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 1;
        File.WriteAllBytes(file, bytes);
        Assert.Throws<WrongPasswordException>(() => new VaultService(file).Unlock("password123"));
    }

    [Theory]
    [InlineData(KeyAlgorithm.Ed25519)]
    [InlineData(KeyAlgorithm.Rsa4096)]
    public void Generated_Key_RoundTrips_Through_OpenSsh_Format(KeyAlgorithm alg)
    {
        var entry = KeyService.Generate("test", "me@pc", alg);
        var (pair, comment) = OpenSshKeyFormat.Read(entry.PrivateKey);
        Assert.Equal("me@pc", comment);
        Assert.Equal(entry.PublicKey, pair.PublicLine("me@pc"));
        Assert.Equal(entry.Fingerprint, pair.Fingerprint);

        // SSH.NET must be able to use it for authentication
        using var sshNet = KeyService.ToSshNet(entry);
        Assert.NotNull(sshNet.Key);
    }

    [Fact]
    public void Ed25519_Signature_Verifies()
    {
        var pair = (Ed25519KeyPair)SshKeyPair.GenerateEd25519();
        var data = RandomNumberGenerator.GetBytes(100);
        var r = new SshReader(pair.Sign(data));
        Assert.Equal("ssh-ed25519", r.StringUtf8());
        var sig = r.String();
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(pair.PublicKey));
        verifier.BlockUpdate(data, 0, data.Length);
        Assert.True(verifier.VerifySignature(sig));
    }

    [Theory]
    [InlineData(0u, "ssh-rsa")]
    [InlineData(2u, "rsa-sha2-256")]
    [InlineData(4u, "rsa-sha2-512")]
    public void Rsa_Signature_Verifies(uint flags, string expectedAlg)
    {
        var pair = SshKeyPair.GenerateRsa(2048);
        var data = RandomNumberGenerator.GetBytes(100);
        var r = new SshReader(pair.Sign(data, flags));
        Assert.Equal(expectedAlg, r.StringUtf8());
        var sig = r.String();

        var pub = new SshReader(pair.PublicBlob);
        pub.String();
        var e = pub.MpInt();
        var n = pub.MpInt();
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Exponent = e, Modulus = n });
        var hash = flags == 4 ? HashAlgorithmName.SHA512 : flags == 2 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
        Assert.True(rsa.VerifyData(data, sig, hash, RSASignaturePadding.Pkcs1));
    }
}
