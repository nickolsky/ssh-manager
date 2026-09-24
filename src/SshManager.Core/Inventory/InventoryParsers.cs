using System.Text.Json;
using System.Text.RegularExpressions;
using SshManager.Core.Models;

namespace SshManager.Core.Inventory;

/// <summary>/etc/os-release (KEY=value, optionally quoted).</summary>
public static class OsReleaseParser
{
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\$", "$").Replace("\\\\", "\\");
            result[line[..eq].Trim()] = value;
        }
        return result;
    }

    public static void Apply(string text, ServerFacts facts)
    {
        var v = Parse(text);
        if (v.Count == 0) return;
        facts.OsId = v.GetValueOrDefault("ID")?.ToLowerInvariant();
        facts.OsLike = v.GetValueOrDefault("ID_LIKE")?.ToLowerInvariant();
        facts.OsName = v.GetValueOrDefault("NAME") is { } name ? ShortName(name) : null;
        facts.OsVersion = v.GetValueOrDefault("VERSION_ID");
        facts.OsPrettyName = v.GetValueOrDefault("PRETTY_NAME");
    }

    /// <summary>"Debian GNU/Linux" → "Debian", "Red Hat Enterprise Linux" stays.</summary>
    private static string ShortName(string name) => name
        .Replace(" GNU/Linux", "").Replace(" Linux Server", "").Replace(" Server", "").Trim();
}

/// <summary>Output of <c>docker ps -a --format '{{json .}}'</c> (one JSON object per line).</summary>
public static class DockerPsParser
{
    public static List<ContainerInfo> Parse(string text)
    {
        var list = new List<ContainerInfo>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string S(string n) => r.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                var status = S("Status");
                var state = S("State");
                if (state.Length == 0)
                    state = status.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ? "running" : "exited";
                list.Add(new ContainerInfo
                {
                    Id = S("ID"),
                    Name = S("Names"),
                    Image = S("Image"),
                    State = state,
                    Status = status,
                    Ports = S("Ports"),
                });
            }
            catch (JsonException)
            {
            }
        }
        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// <c>systemctl list-units --type=service --all --no-legend --plain</c>, filtered to services worth showing.
/// </summary>
public static class ServiceListParser
{
    public static List<ServiceInfo> Parse(string text)
    {
        var list = new List<ServiceInfo>();
        foreach (var raw in text.Split('\n'))
        {
            // UNIT LOAD ACTIVE SUB DESCRIPTION; a leading "●" marks failed units in some versions
            var parts = raw.Replace("●", " ").Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !parts[0].EndsWith(".service", StringComparison.Ordinal)) continue;
            if (parts[1] != "loaded") continue;
            var unit = parts[0][..^".service".Length];
            if (WellKnownServices.Match(unit) is not { } title) continue;
            list.Add(new ServiceInfo { Unit = unit, Title = title, Active = parts[2], Sub = parts[3] });
        }
        return list.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Unit).ToList();
    }
}

/// <summary>Services an admin of VPN / web servers usually cares about.</summary>
public static class WellKnownServices
{
    private static readonly (Regex Pattern, string Title)[] Known =
    [
        (R("nginx"), "nginx"),
        (R("apache2|httpd"), "Apache"),
        (R("caddy"), "Caddy"),
        (R("haproxy"), "HAProxy"),
        (R("traefik"), "Traefik"),
        (R("docker"), "Docker"),
        (R("containerd"), "containerd"),
        (R("podman"), "Podman"),
        (R("k3s|k3s-agent"), "k3s"),
        (R("xray|xray@.+"), "Xray"),
        (R("v2ray|v2ray@.+"), "V2Ray"),
        (R("sing-box|sing-box@.+"), "sing-box"),
        (R("x-ui|3x-ui"), "3X-UI"),
        (R("marzban|marzban-node"), "Marzban"),
        (R("hysteria|hysteria-server|hysteria-server@.+|hysteria2"), "Hysteria"),
        (R("wg-quick@.+"), "WireGuard"),
        (R("awg-quick@.+|amnezia.*"), "AmneziaWG"),
        (R("openvpn|openvpn@.+|openvpn-server@.+|openvpn-client@.+|openvpnas"), "OpenVPN"),
        (R("outline.*|shadowbox"), "Outline"),
        (R("shadowsocks.*|ss-server|ssserver"), "Shadowsocks"),
        (R("ocserv"), "OpenConnect (ocserv)"),
        (R("strongswan|strongswan-starter|ipsec|xl2tpd"), "IPsec / L2TP"),
        (R("softether-vpnserver|vpnserver"), "SoftEther"),
        (R("mtproto-proxy|mtg"), "MTProto proxy"),
        (R("squid"), "Squid"),
        (R("3proxy"), "3proxy"),
        (R("danted|dante-server"), "Dante SOCKS"),
        (R("tor"), "Tor"),
        (R("mysql|mysqld"), "MySQL"),
        (R("mariadb"), "MariaDB"),
        (R("postgresql|postgresql@.+"), "PostgreSQL"),
        (R("redis|redis-server"), "Redis"),
        (R("mongod"), "MongoDB"),
        (R("php.*-fpm"), "PHP-FPM"),
        (R("fail2ban"), "fail2ban"),
        (R("ufw"), "ufw"),
        (R("firewalld"), "firewalld"),
        (R("netfilter-persistent|iptables"), "iptables (persistent)"),
        (R("crowdsec"), "CrowdSec"),
        (R("zabbix-agent2?"), "Zabbix agent"),
        (R("node_exporter|prometheus-node-exporter"), "node_exporter"),
        (R("netdata"), "Netdata"),
        (R("tailscaled"), "Tailscale"),
        (R("zerotier-one"), "ZeroTier"),
        (R("cloudflared"), "cloudflared"),
        (R("certbot\\.timer|certbot"), "certbot"),
    ];

    private static Regex R(string p) => new("^(?:" + p + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Friendly title for a systemd unit name (without .service), or null if not interesting.</summary>
    public static string? Match(string unit) => Known.FirstOrDefault(k => k.Pattern.IsMatch(unit)).Title;
}

/// <summary>Splits the inventory script output into "@@name" sections.</summary>
public static class SectionParser
{
    public const string Marker = "@@sshm:";

    public static Dictionary<string, string> Split(string output)
    {
        var result = new Dictionary<string, string>();
        string? current = null;
        var sb = new System.Text.StringBuilder();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                if (current != null) result[current] = sb.ToString();
                current = line[Marker.Length..].Trim();
                sb.Clear();
            }
            else if (current != null) sb.Append(line).Append('\n');
        }
        if (current != null) result[current] = sb.ToString();
        return result;
    }
}
