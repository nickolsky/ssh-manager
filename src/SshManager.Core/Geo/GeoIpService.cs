using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SshManager.Core.Models;
using SshManager.Core.Storage;

namespace SshManager.Core.Geo;

/// <summary>Server location from an online GeoIP service (ipwho.is, falling back to ip-api.com).</summary>
public sealed class GeoIpService(VaultService vault, SettingsService settings)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly SemaphoreSlim _gate = new(2);

    static GeoIpService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("SSHManager/1.0");

    public bool Enabled => settings.Settings.GeoIpEnabled;

    /// <summary>Looks up the location if it is unknown, outdated, in another language or the IP changed.</summary>
    public Task RefreshAsync(Guid serverId, bool force = false)
    {
        if (!Enabled) return Task.CompletedTask;
        return _running.GetOrAdd(serverId, id => Task.Run(async () =>
        {
            try
            {
                await RunAsync(id, force);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException
                                           or JsonException or VaultLockedException or NotSupportedException)
            {
            }
            finally
            {
                _running.TryRemove(id, out _);
            }
        }));
    }

    private async Task RunAsync(Guid id, bool force)
    {
        if (!vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id) is { } s ? (s.Host, s.Facts?.Geo) : default,
                out var info) || info.Host == null)
            return;
        var ip = await ResolveAsync(info.Host);
        if (ip == null) return;
        var lang = L.Language;
        var geo = info.Geo;
        if (!force && geo != null && geo.Ip == ip && geo.Language == lang && DateTime.Now - geo.Updated < MaxAge) return;

        await _gate.WaitAsync();
        try
        {
            var result = await LookupAsync(ip, lang);
            if (result != null) vault.UpdateFacts(id, f => f.Geo = result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<string?> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return IsPublic(literal) ? literal.ToString() : null;
        var addresses = await Dns.GetHostAddressesAsync(host);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        return ip != null && IsPublic(ip) ? ip.ToString() : null;
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254) || b[0] == 0);
    }

    public static async Task<GeoInfo?> LookupAsync(string ip, string lang)
    {
        try
        {
            var r = await Http.GetFromJsonAsync<JsonElement>($"https://ipwho.is/{ip}?lang={lang}");
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return new GeoInfo
                {
                    Ip = ip,
                    Country = Str(r, "country"),
                    CountryCode = Str(r, "country_code"),
                    Region = Str(r, "region"),
                    City = Str(r, "city"),
                    Isp = r.TryGetProperty("connection", out var c) ? Str(c, "isp") ?? Str(c, "org") : null,
                    Language = lang,
                    Updated = DateTime.Now,
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }

        var a = await Http.GetFromJsonAsync<JsonElement>(
            $"http://ip-api.com/json/{ip}?fields=status,country,countryCode,regionName,city,isp&lang={lang}");
        if (Str(a, "status") != "success") return null;
        return new GeoInfo
        {
            Ip = ip,
            Country = Str(a, "country"),
            CountryCode = Str(a, "countryCode"),
            Region = Str(a, "regionName"),
            City = Str(a, "city"),
            Isp = Str(a, "isp"),
            Language = lang,
            Updated = DateTime.Now,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;
}
