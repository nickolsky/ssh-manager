using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace SshManager.Core.Crypto;

/// <summary>A decrypted private key that can produce SSH signatures.</summary>
public abstract class SshKeyPair
{
    public abstract string Type { get; }
    public abstract byte[] PublicBlob { get; }

    /// <summary>Returns an SSH signature blob (string algorithm, string signature).</summary>
    /// <param name="flags">SSH agent flags: 2 = rsa-sha2-256, 4 = rsa-sha2-512.</param>
    public abstract byte[] Sign(byte[] data, uint flags = 0);

    /// <summary>Key-specific part of the openssh-key-v1 private section (after the key type string).</summary>
    internal abstract void WritePrivateFields(SshWriter w);

    public string PublicLine(string comment) =>
        $"{Type} {Convert.ToBase64String(PublicBlob)}" + (string.IsNullOrWhiteSpace(comment) ? "" : " " + comment.Trim());

    public string Fingerprint => FingerprintOf(PublicBlob);

    public static string FingerprintOf(byte[] publicBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

    public static SshKeyPair GenerateEd25519()
    {
        var priv = new Ed25519PrivateKeyParameters(new SecureRandom());
        return new Ed25519KeyPair(priv.GetEncoded());
    }

    public static SshKeyPair GenerateRsa(int bits = 4096)
    {
        using var rsa = RSA.Create(bits);
        return new RsaKeyPair(rsa.ExportParameters(true));
    }
}

public sealed class Ed25519KeyPair : SshKeyPair
{
    private readonly byte[] _seed;
    private readonly byte[] _public;

    public Ed25519KeyPair(byte[] seed)
    {
        if (seed.Length != 32) throw new ArgumentException("Ed25519 seed must be 32 bytes");
        _seed = seed.ToArray();
        _public = new Ed25519PrivateKeyParameters(_seed).GeneratePublicKey().GetEncoded();
        PublicBlob = new SshWriter().String("ssh-ed25519").String(_public).ToArray();
    }

    public override string Type => "ssh-ed25519";
    public override byte[] PublicBlob { get; }
    public byte[] PublicKey => _public.ToArray();

    public override byte[] Sign(byte[] data, uint flags = 0)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(_seed));
        signer.BlockUpdate(data, 0, data.Length);
        var sig = signer.GenerateSignature();
        return new SshWriter().String("ssh-ed25519").String(sig).ToArray();
    }

    internal override void WritePrivateFields(SshWriter w)
    {
        w.String(_public);
        var full = new byte[64];
        _seed.CopyTo(full, 0);
        _public.CopyTo(full, 32);
        w.String(full);
    }
}

public sealed class RsaKeyPair : SshKeyPair
{
    private readonly RSAParameters _p;

    public RsaKeyPair(RSAParameters p)
    {
        _p = Normalize(p);
        PublicBlob = new SshWriter().String("ssh-rsa").MpInt(_p.Exponent).MpInt(_p.Modulus).ToArray();
    }

    /// <summary>Builds a key from the components stored in OpenSSH files (no dp/dq).</summary>
    public static RsaKeyPair FromComponents(byte[] n, byte[] e, byte[] d, byte[] iqmp, byte[] p, byte[] q)
    {
        var dBig = BigEndian.ToBigInteger(d);
        var pBig = BigEndian.ToBigInteger(p);
        var qBig = BigEndian.ToBigInteger(q);
        return new RsaKeyPair(new RSAParameters
        {
            Modulus = n,
            Exponent = e,
            D = d,
            P = p,
            Q = q,
            InverseQ = iqmp,
            DP = BigEndian.FromBigInteger(BigInteger.Remainder(dBig, pBig - 1)),
            DQ = BigEndian.FromBigInteger(BigInteger.Remainder(dBig, qBig - 1)),
        });
    }

    public override string Type => "ssh-rsa";
    public override byte[] PublicBlob { get; }
    public int KeySize => _p.Modulus!.Length * 8;

    public override byte[] Sign(byte[] data, uint flags = 0)
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(_p);
        var (alg, hash) = (flags & 4) != 0 ? ("rsa-sha2-512", HashAlgorithmName.SHA512)
            : (flags & 2) != 0 ? ("rsa-sha2-256", HashAlgorithmName.SHA256)
            : ("ssh-rsa", HashAlgorithmName.SHA1);
        var sig = rsa.SignData(data, hash, RSASignaturePadding.Pkcs1);
        return new SshWriter().String(alg).String(sig).ToArray();
    }

    internal override void WritePrivateFields(SshWriter w)
    {
        w.MpInt(_p.Modulus).MpInt(_p.Exponent).MpInt(_p.D).MpInt(_p.InverseQ).MpInt(_p.P).MpInt(_p.Q);
    }

    /// <summary>.NET requires D to be as long as the modulus and the CRT parts half as long.</summary>
    private static RSAParameters Normalize(RSAParameters p)
    {
        var n = Trim(p.Modulus!);
        int half = (n.Length + 1) / 2;
        return new RSAParameters
        {
            Modulus = n,
            Exponent = Trim(p.Exponent!),
            D = BigEndian.Pad(p.D!, n.Length),
            P = BigEndian.Pad(p.P!, half),
            Q = BigEndian.Pad(p.Q!, half),
            DP = BigEndian.Pad(p.DP!, half),
            DQ = BigEndian.Pad(p.DQ!, half),
            InverseQ = BigEndian.Pad(p.InverseQ!, half),
        };
    }

    private static byte[] Trim(byte[] b)
    {
        int i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }
}
