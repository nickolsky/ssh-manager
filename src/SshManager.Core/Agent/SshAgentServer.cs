using System.Buffers.Binary;
using System.IO.Pipes;
using SshManager.Core.Crypto;
using SshManager.Core.Pipes;
using SshManager.Core.Storage;

namespace SshManager.Core.Agent;

/// <summary>
/// OpenSSH agent protocol (draft-miller-ssh-agent) over a Windows named pipe.
/// Serves the keys from the vault; external add/remove requests are refused.
/// </summary>
public sealed class SshAgentServer(VaultService vault) : PipeServerBase
{
    private const byte Failure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    private const int MaxMessage = 256 * 1024;

    private readonly Dictionary<Guid, SshKeyPair> _cache = [];

    /// <summary>Called when a client needs keys while the vault is locked. Returns true once unlocked.</summary>
    public Func<Task<bool>>? UnlockRequested { get; set; }

    public event EventHandler<string>? KeyUsed;

    public string? PipePath => PipeName == null ? null : @"\\.\pipe\" + PipeName;
    public bool UsesDefaultPipe => PipeName == AppPaths.AgentPipeDefault;

    public bool Start() => Start(AppPaths.AgentPipeDefault, AppPaths.AgentPipeFallback);

    internal bool StartOn(string pipeName) => Start(pipeName);

    public void ClearCache()
    {
        lock (_cache) _cache.Clear();
    }

    protected override async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExact(pipe, lenBuf, ct)) return;
            var len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            if (len == 0 || len > MaxMessage) return;
            var msg = new byte[len];
            if (!await ReadExact(pipe, msg, ct)) return;

            byte[] reply;
            try
            {
                reply = await ProcessAsync(msg);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or System.Security.Cryptography.CryptographicException)
            {
                reply = [Failure];
            }

            var outBuf = new byte[4 + reply.Length];
            BinaryPrimitives.WriteUInt32BigEndian(outBuf, (uint)reply.Length);
            reply.CopyTo(outBuf, 4);
            await pipe.WriteAsync(outBuf, ct);
            await pipe.FlushAsync(ct);
        }
    }

    private async Task<byte[]> ProcessAsync(byte[] msg)
    {
        switch (msg[0])
        {
            case RequestIdentities:
            {
                var keys = await GetKeysAsync();
                var w = new SshWriter().Byte(IdentitiesAnswer).UInt32((uint)keys.Count);
                foreach (var (_, pair, comment) in keys)
                    w.String(pair.PublicBlob).String(comment);
                return w.ToArray();
            }
            case SignRequest:
            {
                var r = new SshReader(msg, 1);
                var blob = r.String();
                var data = r.String();
                var flags = r.UInt32();
                var keys = await GetKeysAsync();
                var match = keys.FirstOrDefault(k => k.Pair.PublicBlob.AsSpan().SequenceEqual(blob));
                if (match.Pair == null) return [Failure];
                var sig = match.Pair.Sign(data, flags);
                KeyUsed?.Invoke(this, match.Comment);
                return new SshWriter().Byte(SignResponse).String(sig).ToArray();
            }
            default:
                return [Failure];
        }
    }

    private async Task<List<(Guid Id, SshKeyPair Pair, string Comment)>> GetKeysAsync()
    {
        if (!vault.IsUnlocked && UnlockRequested != null)
            await UnlockRequested();

        if (!vault.TryRead(d => d.Keys.ToList(), out var entries)) return [];

        var result = new List<(Guid, SshKeyPair, string)>();
        lock (_cache)
        {
            foreach (var e in entries)
            {
                if (!_cache.TryGetValue(e.Id, out var pair))
                {
                    try
                    {
                        pair = KeyService.Load(e);
                    }
                    catch (Exception ex) when (ex is FormatException or NotSupportedException)
                    {
                        continue;
                    }
                    _cache[e.Id] = pair;
                }
                result.Add((e.Id, pair, string.IsNullOrEmpty(e.Comment) ? e.Name : e.Comment));
            }
        }
        return result;
    }

    private static async Task<bool> ReadExact(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
