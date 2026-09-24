using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace SshManager.Core.Crypto;

/// <summary>
/// Vault file format:
/// magic "SSHMVLT1" | int32 memoryKiB | int32 iterations | int32 parallelism | salt[16] | nonce[12] | tag[16] | ciphertext.
/// Key = Argon2id(master password, salt); cipher = AES-256-GCM with the whole header as associated data.
/// </summary>
public static class VaultCrypto
{
    private static readonly byte[] Magic = "SSHMVLT1"u8.ToArray();
    private const int SaltLen = 16, NonceLen = 12, TagLen = 16;
    private const int HeaderLen = 8 + 12 + SaltLen + NonceLen;

    public sealed record KdfParams(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
    {
        public static KdfParams CreateDefault() => new(64 * 1024, 3, 4, RandomNumberGenerator.GetBytes(SaltLen));
    }

    public static byte[] DeriveKey(string password, KdfParams p)
    {
        var args = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithMemoryAsKB(p.MemoryKiB)
            .WithIterations(p.Iterations)
            .WithParallelism(p.Parallelism)
            .WithSalt(p.Salt)
            .Build();
        var gen = new Argon2BytesGenerator();
        gen.Init(args);
        var key = new byte[32];
        var pwd = Encoding.UTF8.GetBytes(password);
        try
        {
            gen.GenerateBytes(pwd, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
        }
        return key;
    }

    public static KdfParams ReadParams(byte[] file)
    {
        if (file.Length < HeaderLen + TagLen || !file.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Файл хранилища повреждён или имеет неизвестный формат");
        var s = file.AsSpan(8);
        return new KdfParams(
            BinaryPrimitives.ReadInt32LittleEndian(s),
            BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
            s.Slice(12, SaltLen).ToArray());
    }

    public static byte[] Encrypt(byte[] plaintext, byte[] key, KdfParams p)
    {
        var file = new byte[HeaderLen + TagLen + plaintext.Length];
        Magic.CopyTo(file, 0);
        var s = file.AsSpan(8);
        BinaryPrimitives.WriteInt32LittleEndian(s, p.MemoryKiB);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], p.Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], p.Parallelism);
        p.Salt.CopyTo(s[12..]);
        var nonce = s.Slice(12 + SaltLen, NonceLen);
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(nonce, plaintext, file.AsSpan(HeaderLen + TagLen), file.AsSpan(HeaderLen, TagLen),
            file.AsSpan(0, HeaderLen));
        return file;
    }

    /// <summary>Throws <see cref="WrongPasswordException"/> if the key is wrong or data was tampered with.</summary>
    public static byte[] Decrypt(byte[] file, byte[] key)
    {
        ReadParams(file);
        var plaintext = new byte[file.Length - HeaderLen - TagLen];
        using var gcm = new AesGcm(key, TagLen);
        try
        {
            gcm.Decrypt(file.AsSpan(HeaderLen - NonceLen, NonceLen), file.AsSpan(HeaderLen + TagLen),
                file.AsSpan(HeaderLen, TagLen), plaintext, file.AsSpan(0, HeaderLen));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new WrongPasswordException();
        }
        return plaintext;
    }
}

public sealed class WrongPasswordException() : Exception("Неверный мастер-пароль");
