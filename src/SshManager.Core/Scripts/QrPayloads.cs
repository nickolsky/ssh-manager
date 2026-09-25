using System.Text;
using System.Text.RegularExpressions;

namespace SshManager.Core.Scripts;

/// <param name="Title">What to scan it with; null when there is only one way.</param>
/// <param name="Text">The QR code's content.</param>
/// <param name="FileText">A file that goes with it (the .conf of an Amnezia key), else null.</param>
/// <param name="CopyText">What "Copy" puts on the clipboard when it is not <paramref name="Text"/>.</param>
public sealed record QrPayload(string? Title, string Text, string? FileText = null, string? CopyText = null);

/// <summary>
/// What a script result can be shown as a QR code: links (vless://, hysteria2://, https://…) as they are;
/// for an Amnezia key its .conf (scanned by AmneziaWG and Amnezia VPN alike) and the key itself for Amnezia VPN.
/// </summary>
public static partial class QrPayloads
{
    /// <summary>Byte-mode capacity of the largest QR code at the lowest error correction, a little under 2953.</summary>
    public const int MaxBytes = 2900;

    public static IReadOnlyList<QrPayload> For(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        value = value.Trim();
        if (!Link().IsMatch(value)) return [];
        var result = new List<QrPayload>();
        if (value.StartsWith(AmneziaKey.Scheme, StringComparison.Ordinal))
        {
            if (AmneziaKey.WireGuardConfig(value) is { } conf && Fits(conf))
                result.Add(new QrPayload(L.Get("Qr.ForAwg"), conf, conf));
            // Amnezia VPN's scanner takes the key without the "vpn://" prefix (its own QR codes are made that way)
            var bare = value[AmneziaKey.Scheme.Length..];
            if (Fits(bare)) result.Add(new QrPayload(L.Get("Qr.ForAmnezia"), bare, CopyText: value));
            if (result.Count == 1 && result[0].FileText == null) result[0] = result[0] with { Title = null };
            return result;
        }
        return Fits(value) ? [new QrPayload(null, value)] : [];
    }

    public static bool CanShow(string? value) => For(value).Count > 0;

    private static bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= MaxBytes;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://\S+$")]
    private static partial Regex Link();
}
