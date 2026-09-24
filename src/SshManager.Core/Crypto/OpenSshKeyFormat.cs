using System.Security.Cryptography;
using System.Text;

namespace SshManager.Core.Crypto;

/// <summary>Reads and writes unencrypted "openssh-key-v1" private keys (the format of ssh-keygen).</summary>
public static class OpenSshKeyFormat
{
    private const string Begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string End = "-----END OPENSSH PRIVATE KEY-----";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    public static string Write(SshKeyPair key, string comment)
    {
        var check = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        var priv = new SshWriter().UInt32(check).UInt32(check).String(key.Type);
        key.WritePrivateFields(priv);
        priv.String(comment ?? "");
        for (byte pad = 1; priv.Length % 8 != 0; pad++) priv.Byte(pad);

        var body = new SshWriter()
            .Raw(Magic)
            .String("none")
            .String("none")
            .String([])
            .UInt32(1)
            .String(key.PublicBlob)
            .String(priv.ToArray())
            .ToArray();

        var b64 = Convert.ToBase64String(body);
        var sb = new StringBuilder();
        sb.Append(Begin).Append('\n');
        for (int i = 0; i < b64.Length; i += 70)
            sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        sb.Append(End).Append('\n');
        return sb.ToString();
    }

    public static bool LooksLikeOpenSsh(string text) => text.Contains(Begin, StringComparison.Ordinal);

    /// <summary>Parses an unencrypted openssh-key-v1 key. Throws if encrypted or unsupported.</summary>
    public static (SshKeyPair Key, string Comment) Read(string pem)
    {
        var start = pem.IndexOf(Begin, StringComparison.Ordinal);
        var end = pem.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new FormatException("Not an OpenSSH private key");
        var b64 = new string(pem[(start + Begin.Length)..end].Where(c => !char.IsWhiteSpace(c)).ToArray());
        var data = Convert.FromBase64String(b64);

        if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new FormatException("Bad openssh-key-v1 magic");
        var r = new SshReader(data, Magic.Length);
        var cipher = r.StringUtf8();
        var kdf = r.StringUtf8();
        r.String(); // kdf options
        if (cipher != "none" || kdf != "none") throw new EncryptedKeyException();
        if (r.UInt32() != 1) throw new FormatException("Multiple keys in one file are not supported");
        r.String(); // public blob

        var p = new SshReader(r.String());
        if (p.UInt32() != p.UInt32()) throw new FormatException("Corrupted private key (check mismatch)");
        var type = p.StringUtf8();
        SshKeyPair key;
        switch (type)
        {
            case "ssh-ed25519":
                p.String(); // public
                var full = p.String();
                key = new Ed25519KeyPair(full[..32]);
                break;
            case "ssh-rsa":
                var n = p.MpInt();
                var e = p.MpInt();
                var d = p.MpInt();
                var iqmp = p.MpInt();
                var pp = p.MpInt();
                var q = p.MpInt();
                key = RsaKeyPair.FromComponents(n, e, d, iqmp, pp, q);
                break;
            default:
                throw new NotSupportedException($"Key type {type} is not supported (use ed25519 or rsa)");
        }
        var comment = p.StringUtf8();
        return (key, comment);
    }
}

public sealed class EncryptedKeyException() : Exception("The private key is protected with a passphrase");
