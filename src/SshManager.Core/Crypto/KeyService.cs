using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;
using SshManager.Core.Models;

namespace SshManager.Core.Crypto;

public enum KeyAlgorithm
{
    Ed25519,
    Rsa4096,
}

/// <summary>Creates, imports and loads keys stored in the vault.</summary>
public static class KeyService
{
    public static KeyEntry Generate(string name, string comment, KeyAlgorithm algorithm)
    {
        var pair = algorithm == KeyAlgorithm.Rsa4096 ? SshKeyPair.GenerateRsa(4096) : SshKeyPair.GenerateEd25519();
        return ToEntry(pair, name, comment);
    }

    public static KeyEntry ToEntry(SshKeyPair pair, string name, string comment) => new()
    {
        Name = name,
        Type = pair.Type,
        Comment = comment,
        PublicKey = pair.PublicLine(comment),
        PrivateKey = OpenSshKeyFormat.Write(pair, comment),
        Fingerprint = pair.Fingerprint,
    };

    public static SshKeyPair Load(KeyEntry entry) => OpenSshKeyFormat.Read(entry.PrivateKey).Key;

    /// <summary>
    /// Imports a private key in OpenSSH, PEM (PKCS#1/PKCS#8) or PuTTY format.
    /// Throws <see cref="EncryptedKeyException"/> when a passphrase is required but not given.
    /// </summary>
    public static KeyEntry Import(string text, string name, string? passphrase)
    {
        if (OpenSshKeyFormat.LooksLikeOpenSsh(text))
        {
            try
            {
                var (key, comment) = OpenSshKeyFormat.Read(text);
                return ToEntry(key, name, comment);
            }
            catch (EncryptedKeyException) when (!string.IsNullOrEmpty(passphrase))
            {
                // fall through to SSH.NET, which implements bcrypt-pbkdf
            }
        }

        PrivateKeyFile file;
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(text));
            file = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, passphrase);
        }
        catch (Exception ex) when (string.IsNullOrEmpty(passphrase) &&
                                   ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedKeyException();
        }

        using (file)
        {
            SshKeyPair pair = file.Key switch
            {
                ED25519Key ed => new Ed25519KeyPair(ed.PrivateKey[..32]),
                RsaKey rsa => new RsaKeyPair(new RSAParameters
                {
                    Modulus = Bytes(rsa.Modulus),
                    Exponent = Bytes(rsa.Exponent),
                    D = Bytes(rsa.D),
                    P = Bytes(rsa.P),
                    Q = Bytes(rsa.Q),
                    DP = Bytes(rsa.DP),
                    DQ = Bytes(rsa.DQ),
                    InverseQ = Bytes(rsa.InverseQ),
                }),
                _ => throw new NotSupportedException(
                    $"Тип ключа {file.Key.GetType().Name} не поддерживается. Поддерживаются Ed25519 и RSA."),
            };
            return ToEntry(pair, name, "");
        }
    }

    private static byte[] Bytes(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    /// <summary>A PrivateKeyFile that SSH.NET can use directly (built in memory, never written to disk).</summary>
    public static PrivateKeyFile ToSshNet(KeyEntry entry)
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(entry.PrivateKey));
        return new PrivateKeyFile(ms);
    }

    /// <summary>Writes the public half to data\pub so ssh.exe can pick the matching agent identity.</summary>
    public static string EnsurePublicKeyFile(KeyEntry entry)
    {
        Directory.CreateDirectory(AppPaths.PublicKeyDir);
        var path = Path.Combine(AppPaths.PublicKeyDir, entry.Id.ToString("N") + ".pub");
        var content = entry.PublicKey.Trim() + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
        return path;
    }

    public static string SanitizeComment(string comment) =>
        new string(comment.Where(c => char.IsAsciiLetterOrDigit(c) || "@._-".Contains(c)).ToArray());
}
