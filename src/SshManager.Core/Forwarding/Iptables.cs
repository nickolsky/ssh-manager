using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Forwarding;

/// <summary>Reads DNAT port forwards from <c>iptables -t nat -S</c>.</summary>
public static class IptablesParser
{
    /// <summary>Forwards in PREROUTING (Docker's own DOCKER chain is container publishing, not forwarding).</summary>
    public static List<PortForward> ParseNat(string text)
    {
        var list = new List<PortForward>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("-A PREROUTING ", StringComparison.Ordinal)) continue;
            var t = Tokenize(line);
            if (Value(t, "-j") != "DNAT") continue;
            var dest = Value(t, "--to-destination");
            if (dest == null) continue;
            var (ip, port) = SplitDestination(dest);
            var f = new PortForward
            {
                Protocol = Value(t, "-p") ?? "all",
                ListenPort = Value(t, "--dport") ?? Value(t, "--dports") ?? "",
                TargetIp = ip,
                TargetPort = port.Replace('-', ':'),
                InInterface = Value(t, "-i"),
                Comment = Value(t, "--comment"),
                Rule = line,
            };
            list.Add(f);
        }
        return list;
    }

    private static (string Ip, string Port) SplitDestination(string dest)
    {
        // 1.2.3.4, 1.2.3.4:443, 1.2.3.4:1000-2000, 1.2.3.4-1.2.3.9:80
        var colon = dest.LastIndexOf(':');
        return colon > 0 ? (dest[..colon], dest[(colon + 1)..]) : (dest, "");
    }

    private static string? Value(List<string> tokens, string option)
    {
        var i = tokens.IndexOf(option);
        return i >= 0 && i + 1 < tokens.Count ? tokens[i + 1] : null;
    }

    /// <summary>Shell-like split: double quotes group words (iptables -S quotes comments).</summary>
    public static List<string> Tokenize(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        var any = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && quoted && i + 1 < line.Length)
            {
                sb.Append(line[++i]);
            }
            else if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0 || any) result.Add(sb.ToString());
                sb.Clear();
                any = false;
            }
            else sb.Append(c);
        }
        if (sb.Length > 0 || any) result.Add(sb.ToString());
        return result;
    }
}

/// <summary>Shell scripts that add, remove and persist port forwards. All run as root.</summary>
public static partial class IptablesCommands
{
    public const string OkMarker = "SSHM_OK";
    public const string PersistMarker = "SSHM_PERSIST=";

    [GeneratedRegex(@"^\d{1,5}(:\d{1,5})?$")]
    private static partial Regex PortRegex();

    public static string NewId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public static void Validate(string protocol, string listenPort, string targetIp, string targetPort)
    {
        if (protocol is not ("tcp" or "udp")) throw new ArgumentException(L.Get("Fwd.BadProtocol"));
        if (!ValidPort(listenPort)) throw new ArgumentException(L.F("Fwd.BadPort", listenPort));
        if (targetPort.Length > 0 && !ValidPort(targetPort)) throw new ArgumentException(L.F("Fwd.BadPort", targetPort));
        if (!IPAddress.TryParse(targetIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(L.F("Fwd.BadIp", targetIp));
    }

    private static bool ValidPort(string p)
    {
        if (!PortRegex().IsMatch(p)) return false;
        return p.Split(':').All(x => int.TryParse(x, out var n) && n is >= 1 and <= 65535);
    }

    public static string Add(string id, string protocol, string listenPort, string targetIp, string targetPort) =>
        "set -e\n" + AddBody(id, protocol, listenPort, targetIp, targetPort) + "\necho " + OkMarker + "\n" + Persist;

    private static string AddBody(string id, string protocol, string listenPort, string targetIp, string targetPort)
    {
        Validate(protocol, listenPort, targetIp, targetPort);
        var tport = targetPort.Length == 0 ? listenPort : targetPort;
        var toDest = $"{targetIp}:{tport.Replace(':', '-')}";
        var c = $"-m comment --comment {PortForward.CommentPrefix}{id}";
        return $$"""
            command -v iptables >/dev/null 2>&1 || { echo 'iptables not found' >&2; exit 3; }
            [ "$(cat /proc/sys/net/ipv4/ip_forward 2>/dev/null)" = 1 ] || sysctl -w net.ipv4.ip_forward=1 >/dev/null
            mkdir -p /etc/sysctl.d && echo 'net.ipv4.ip_forward=1' > /etc/sysctl.d/99-sshm-forward.conf
            iptables -t nat -A PREROUTING -p {{protocol}} --dport {{listenPort}} {{c}} -j DNAT --to-destination {{toDest}}
            iptables -t nat -A POSTROUTING -p {{protocol}} -d {{targetIp}} --dport {{tport}} {{c}} -j MASQUERADE
            iptables -I FORWARD 1 -p {{protocol}} -d {{targetIp}} --dport {{tport}} {{c}} -j ACCEPT
            iptables -I FORWARD 1 -p {{protocol}} -s {{targetIp}} --sport {{tport}} -m conntrack --ctstate ESTABLISHED,RELATED {{c}} -j ACCEPT
            """;
    }

    /// <summary>Managed forwards: every rule tagged with its id; external ones: just their DNAT rule.</summary>
    public static string Remove(PortForward f) => "set -e\n" + RemoveBody(f) + "\necho " + OkMarker + "\n" + Persist;

    private static string RemoveBody(PortForward f)
    {
        if (f.ManagedId is { } id) return RemoveById(id);
        if (f.Rule == null || !f.Rule.StartsWith("-A PREROUTING ", StringComparison.Ordinal) || f.Rule.Contains('\n'))
            throw new ArgumentException(L.Get("Fwd.NoRule"));
        return "eval \"iptables -t nat " + Escape("-D " + f.Rule[3..]) + "\"";
    }

    private static string RemoveById(string id)
    {
        if (!IdRegex().IsMatch(id)) throw new ArgumentException(L.F("Fwd.BadId", id));
        return $$"""
            for t in nat filter; do
              iptables -t "$t" -S | grep -E -- '--comment "?{{PortForward.CommentPrefix}}{{id}}"?( |$)' | sed 's/^-A /-D /' |
              while IFS= read -r rule; do eval "iptables -t $t $rule"; done
            done
            """;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    /// <summary>
    /// Edits a forward in one go: removes <paramref name="old"/>, then adds the new one (one forward per protocol, each
    /// with its own id). When adding fails, the partly added rules go and the old forward is put back.
    /// </summary>
    public static string Replace(PortForward old, IReadOnlyList<(string Id, string Protocol)> adds, string listenPort, string targetIp, string targetPort)
    {
        var add = string.Join("\n", adds.Select(a => AddBody(a.Id, a.Protocol, listenPort, targetIp, targetPort)));
        var undo = string.Join("\n", adds.Select(a => RemoveById(a.Id)));
        string restore;
        if (old.ManagedId != null)
            restore = AddBody(old.ManagedId, old.Protocol, old.ListenPort, old.TargetIp, old.TargetPort);
        else
            restore = "eval \"iptables -t nat " + Escape(old.Rule!) + "\""; // RemoveBody checked it is one -A PREROUTING line
        // the subshell runs as a plain command: inside "if ! ( … )" bash would ignore its set -e and go on after a failure
        return $$"""
            set -e
            {{RemoveBody(old)}}
            set +e
            ( set -e
            {{add}}
            )
            added=$?
            if [ "$added" -ne 0 ]; then
            {{undo}}
            {{restore}}
              echo 'The new forward could not be added; the old one is back' >&2
              exit 5
            fi
            set -e
            echo {{OkMarker}}
            """ + "\n" + Persist;
    }

    [GeneratedRegex("^[0-9a-f]{1,32}$")]
    private static partial Regex IdRegex();

    /// <summary>Saves the ruleset so it survives a reboot; prints which mechanism was used (or "none").</summary>
    public const string Persist = """
        if command -v netfilter-persistent >/dev/null 2>&1; then
          netfilter-persistent save >/dev/null 2>&1 && echo SSHM_PERSIST=netfilter-persistent
        elif [ -d /etc/iptables ]; then
          iptables-save > /etc/iptables/rules.v4 && echo SSHM_PERSIST=/etc/iptables/rules.v4
        elif [ -f /etc/sysconfig/iptables ]; then
          iptables-save > /etc/sysconfig/iptables && echo SSHM_PERSIST=/etc/sysconfig/iptables
        else
          echo SSHM_PERSIST=none
        fi
        """;

    /// <summary>Installs the package that restores rules at boot, then saves the current rules.</summary>
    public const string InstallPersistence = """
        set -e
        if command -v apt-get >/dev/null 2>&1; then
          echo iptables-persistent iptables-persistent/autosave_v4 boolean true | debconf-set-selections
          echo iptables-persistent iptables-persistent/autosave_v6 boolean true | debconf-set-selections
          DEBIAN_FRONTEND=noninteractive apt-get install -y iptables-persistent >/dev/null
        elif command -v dnf >/dev/null 2>&1; then
          dnf install -y iptables-services >/dev/null && systemctl enable iptables >/dev/null 2>&1
          touch /etc/sysconfig/iptables
        elif command -v yum >/dev/null 2>&1; then
          yum install -y iptables-services >/dev/null && systemctl enable iptables >/dev/null 2>&1
          touch /etc/sysconfig/iptables
        else
          echo 'unsupported package manager' >&2; exit 4
        fi
        echo SSHM_OK
        """ + "\n" + Persist;

    public static string? PersistResult(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PersistMarker, StringComparison.Ordinal));
        return line?[PersistMarker.Length..];
    }

    public static string Quote(string s) => RemoteShell.Quote(s);
}
