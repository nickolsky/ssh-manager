using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SshManager.Core.Crypto;
using SshManager.Core.Models;

namespace SshManager.Core.Storage;

/// <summary>Holds the decrypted vault in memory while unlocked and persists it encrypted.</summary>
public sealed class VaultService
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxBackups = 20;
    private readonly string _file;
    private readonly object _sync = new();
    private byte[]? _key;
    private VaultCrypto.KdfParams? _kdf;
    private VaultData? _data;

    public VaultService(string? file = null) => _file = file ?? AppPaths.VaultFile;

    public event EventHandler? LockStateChanged;
    public event EventHandler? DataChanged;

    public bool Exists => File.Exists(_file);
    public bool IsUnlocked => _data != null;

    public VaultData Data => _data ?? throw new VaultLockedException();

    public void Create(string password)
    {
        if (Exists) throw new InvalidOperationException("Хранилище уже существует");
        var kdf = VaultCrypto.KdfParams.CreateDefault();
        var key = VaultCrypto.DeriveKey(password, kdf);
        lock (_sync)
        {
            _kdf = kdf;
            _key = key;
            _data = new VaultData();
            SaveLocked(backup: false);
        }
        LockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Slow (Argon2); call from a background thread.</summary>
    public void Unlock(string password)
    {
        var file = File.ReadAllBytes(_file);
        var kdf = VaultCrypto.ReadParams(file);
        var key = VaultCrypto.DeriveKey(password, kdf);
        var plain = VaultCrypto.Decrypt(file, key);
        VaultData data;
        try
        {
            data = JsonSerializer.Deserialize<VaultData>(plain, Json) ?? new VaultData();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        lock (_sync)
        {
            _kdf = kdf;
            _key = key;
            _data = data;
        }
        LockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Lock()
    {
        lock (_sync)
        {
            if (_data == null) return;
            if (_key != null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _data = null;
        }
        LockStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangePassword(string current, string newPassword)
    {
        var file = File.ReadAllBytes(_file);
        VaultCrypto.Decrypt(file, VaultCrypto.DeriveKey(current, VaultCrypto.ReadParams(file)));
        var kdf = VaultCrypto.KdfParams.CreateDefault();
        var key = VaultCrypto.DeriveKey(newPassword, kdf);
        lock (_sync)
        {
            if (_data == null) throw new VaultLockedException();
            _kdf = kdf;
            _key = key;
            SaveLocked(backup: true);
        }
    }

    /// <summary>Runs a mutation under the vault lock and saves.</summary>
    public void Update(Action<VaultData> change)
    {
        lock (_sync)
        {
            change(Data);
            SaveLocked(backup: true);
        }
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Thread-safe read access for background threads (agent, IPC).</summary>
    public T Read<T>(Func<VaultData, T> reader)
    {
        lock (_sync) return reader(Data);
    }

    public bool TryRead<T>(Func<VaultData, T> reader, out T result)
    {
        lock (_sync)
        {
            if (_data == null)
            {
                result = default!;
                return false;
            }
            result = reader(_data);
            return true;
        }
    }

    private void SaveLocked(bool backup)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(_data, Json);
        byte[] enc;
        try
        {
            enc = VaultCrypto.Encrypt(plain, _key!, _kdf!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var dir = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(dir);
        if (backup && File.Exists(_file)) Backup(dir);

        var tmp = _file + ".tmp";
        File.WriteAllBytes(tmp, enc);
        File.Move(tmp, _file, overwrite: true);
    }

    private void Backup(string dir)
    {
        try
        {
            var bdir = Path.Combine(dir, "backups");
            Directory.CreateDirectory(bdir);
            File.Copy(_file, Path.Combine(bdir, $"vault-{DateTime.Now:yyyyMMdd-HHmmss-fff}.dat"), overwrite: true);
            foreach (var old in new DirectoryInfo(bdir).GetFiles("vault-*.dat")
                         .OrderByDescending(f => f.Name).Skip(MaxBackups))
                old.Delete();
        }
        catch (IOException)
        {
            // a failed backup must never block saving
        }
    }
}

public sealed class VaultLockedException() : Exception("Хранилище заблокировано");
