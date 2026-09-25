using SshManager.Core.Models;

namespace SshManager.Core.Forwarding;

/// <summary>One point of a forward chain: a server (when it is in SSH Manager) or just an address, and the port there.</summary>
public sealed record ForwardPoint(ServerEntry? Server, string Address, string Port)
{
    /// <summary>What listens on the port at the end of the chain (from the server's facts), null when unknown.</summary>
    public string? Listener { get; init; }

    public string Name => Server?.Name ?? Address;
    public string Label => $"{Name}:{Port}";
}

/// <summary>
/// Forwards chained over several servers: A:443 → B:8443 → C:443 → D. A hop continues when the target is a server
/// in SSH Manager that has a forward of the same protocol on the target port.
/// </summary>
public static class ForwardChains
{
    private const int MaxHops = 16;

    /// <summary>From <paramref name="from"/>'s listen port through every following hop.</summary>
    public static List<ForwardPoint> Downstream(ServerEntry from, PortForward forward, Func<string, ServerEntry?> byIp)
    {
        var chain = new List<ForwardPoint> { new(from, from.Host, forward.ListenPort) };
        var seen = new HashSet<(Guid, string)> { (from.Id, forward.ListenPort) };
        var f = forward;
        var port = forward.ListenPort;
        while (chain.Count <= MaxHops)
        {
            port = f.TargetPort.Length > 0 ? f.TargetPort : port;
            var server = byIp(f.TargetIp);
            var next = server?.Facts?.Forwards.FirstOrDefault(g => SameProtocol(g, f) && Covers(g.ListenPort, port));
            if (server == null || next == null || !seen.Add((server.Id, port)))
            {
                chain.Add(new ForwardPoint(server, f.TargetIp, port) { Listener = ListenerOn(server, port, f.Protocol) });
                return chain;
            }
            chain.Add(new ForwardPoint(server, f.TargetIp, port));
            f = next;
        }
        return chain;
    }

    /// <summary>
    /// The whole chain this forward is part of: the servers that forward into <paramref name="from"/>'s listen port
    /// (followed back while there is exactly one), then <see cref="Downstream"/>.
    /// </summary>
    public static List<ForwardPoint> Full(ServerEntry from, PortForward forward, IEnumerable<ServerEntry> servers, Func<string, ServerEntry?> byIp)
    {
        var all = servers.ToList();
        var chain = Downstream(from, forward, byIp);
        var head = (Server: from, Forward: forward);
        var seen = new HashSet<Guid> { from.Id };
        for (var i = 0; i < MaxHops; i++)
        {
            var into = Into(head.Server, head.Forward.ListenPort, head.Forward, all, byIp).ToList();
            if (into.Count != 1 || !seen.Add(into[0].From.Id)) break;
            head = into[0];
            chain.Insert(0, new ForwardPoint(head.Server, head.Server.Host, head.Forward.ListenPort));
        }
        return chain;
    }

    /// <summary>Forwards on other servers whose traffic arrives at <paramref name="target"/>:<paramref name="port"/>.</summary>
    public static IEnumerable<(ServerEntry From, PortForward Forward)> Into(ServerEntry target, string port, PortForward like,
        IEnumerable<ServerEntry> servers, Func<string, ServerEntry?> byIp)
    {
        foreach (var s in servers)
        {
            if (s.Id == target.Id || s.Facts == null) continue;
            foreach (var f in s.Facts.Forwards)
                if (SameProtocol(f, like) && byIp(f.TargetIp)?.Id == target.Id && Covers(port, f.EffectiveTargetPort))
                    yield return (s, f);
        }
    }

    public static string Format(IEnumerable<ForwardPoint> chain)
    {
        var points = chain.ToList();
        var text = string.Join(" → ", points.Select(p => p.Label));
        return points.Count > 0 && points[^1].Listener is { } l ? $"{text} ({l})" : text;
    }

    /// <summary>One hop per line (for tooltips): "A:443", "→ B:8443", "→ C:443  (nginx)".</summary>
    public static string FormatLines(IEnumerable<ForwardPoint> chain) =>
        string.Join("\n", chain.Select((p, i) => (i == 0 ? "" : "→ ") + p.Label + (p.Listener is { } l ? $"  ({l})" : "")));

    private static bool SameProtocol(PortForward a, PortForward b) =>
        a.Protocol == b.Protocol || a.Protocol == "all" || b.Protocol == "all";

    /// <summary>A port or range ("1000:2000") that takes in <paramref name="port"/> (a port or a range inside it).</summary>
    public static bool Covers(string range, string port)
    {
        if (!TryRange(range, out var lo, out var hi) || !TryRange(port, out var plo, out var phi)) return range == port;
        return plo >= lo && phi <= hi;
    }

    private static bool TryRange(string s, out int lo, out int hi)
    {
        var parts = s.Split(':', '-');
        lo = hi = 0;
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out lo)) return false;
        if (parts.Length == 1)
        {
            hi = lo;
            return true;
        }
        return int.TryParse(parts[1], out hi);
    }

    private static string? ListenerOn(ServerEntry? server, string port, string protocol)
    {
        if (server?.Facts == null || protocol == "udp" || !int.TryParse(port, out var p)) return null; // TCP ports only are collected
        return server.Facts.ListeningPorts.FirstOrDefault(l => l.Port == p)?.Process;
    }
}
