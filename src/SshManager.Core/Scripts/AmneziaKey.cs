using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace SshManager.Core.Scripts;

/// <summary>
/// Amnezia VPN keys: "vpn://" + base64url(qCompress(server JSON)), where Qt's qCompress is a 4-byte big-endian
/// length followed by a zlib stream. The AmneziaWG script makes them; here they are read back to show the
/// client's .conf (as a QR code AmneziaWG and Amnezia VPN both scan) and to check them in tests.
/// </summary>
public static class AmneziaKey
{
    public const string Scheme = "vpn://";

    /// <summary>The server JSON inside a key, or null when it is not one.</summary>
    public static JsonObject? Decode(string key)
    {
        if (!key.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        try
        {
            var b64 = key[Scheme.Length..].Trim().Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var data = Convert.FromBase64String(b64);
            if (data.Length < 6) return null;
            var length = BinaryPrimitives.ReadInt32BigEndian(data);
            using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            var json = output.ToArray();
            // ZLibStream does not check the stream's Adler-32 trailer, but Qt's qUncompress does: check it here too
            if (json.Length != length || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(data.Length - 4)) != Adler32(json)) return null;
            return JsonNode.Parse(Encoding.UTF8.GetString(json)) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    /// <summary>The WireGuard / AmneziaWG config text of the key's default container, or null.</summary>
    public static string? WireGuardConfig(string key) => WireGuardConfig(Decode(key));

    public static string? WireGuardConfig(JsonObject? server)
    {
        try
        {
            if (server?["containers"] is not JsonArray containers) return null;
            var name = server["defaultContainer"]?.GetValue<string>();
            foreach (var c in containers.OfType<JsonObject>())
            {
                if (name != null && c["container"]?.GetValue<string>() != name) continue;
                foreach (var proto in new[] { "awg", "wireguard" })
                    if (c[proto]?["last_config"]?.GetValue<string>() is { } last &&
                        JsonNode.Parse(last)?["config"]?.GetValue<string>() is { Length: > 0 } config)
                        return config;
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The connection name shown in Amnezia VPN.</summary>
    public static string? Description(string key)
    {
        try
        {
            return Decode(key)?["description"]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
