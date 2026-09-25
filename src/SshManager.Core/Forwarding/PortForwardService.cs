using Renci.SshNet;
using SshManager.Core.Models;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Core.Forwarding;

public sealed record ForwardChange(List<PortForward> Forwards, string? PersistedWith);

/// <summary>Adds / removes iptables DNAT forwards on a server (requires root or sudo).</summary>
public sealed class PortForwardService(VaultService vault, SshClientFactory ssh)
{
    public List<PortForward> List(ServerEntry server)
    {
        using var client = Open(server);
        return ReadAndStore(client, server);
    }

    public ForwardChange Add(ServerEntry server, string protocol, string listenPort, string targetIp, string targetPort,
        Action<string> log)
    {
        var script = IptablesCommands.Add(IptablesCommands.NewId(), protocol, listenPort, targetIp, targetPort);
        return Execute(server, script, log);
    }

    /// <summary>Changes a forward (removed and added again in one script; the old one comes back when adding fails).</summary>
    public ForwardChange Replace(ServerEntry server, PortForward old, IReadOnlyList<string> protocols, string listenPort, string targetIp,
        string targetPort, Action<string> log)
    {
        var script = IptablesCommands.Replace(old, protocols.Select(p => (IptablesCommands.NewId(), p)).ToList(), listenPort, targetIp, targetPort);
        return Execute(server, script, log);
    }

    public ForwardChange Remove(ServerEntry server, PortForward forward, Action<string> log) =>
        Execute(server, IptablesCommands.Remove(forward), log);

    public ForwardChange InstallPersistence(ServerEntry server, Action<string> log) =>
        Execute(server, IptablesCommands.InstallPersistence, log);

    private ForwardChange Execute(ServerEntry server, string script, Action<string> log)
    {
        log(L.F("Fwd.Connecting", server.Display));
        using var client = Open(server);
        var r = RemoteShell.Run(client, server, script, elevated: true, TimeSpan.FromMinutes(5));
        if (RemoteShell.SudoFailed(r)) throw new InvalidOperationException(L.Get("Fwd.NeedRoot") + "\n" + r.Error.Trim());
        if (!r.Output.Contains(IptablesCommands.OkMarker))
            throw new InvalidOperationException(L.Get("Fwd.Failed") + "\n" + r.Combined);
        var persisted = IptablesCommands.PersistResult(r.Output);
        log(persisted is null or "none" ? L.Get("Fwd.NotPersisted") : L.F("Fwd.Persisted", persisted));
        var list = ReadAndStore(client, server);
        return new ForwardChange(list, persisted is "none" ? null : persisted);
    }

    private SshClient Open(ServerEntry server)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) throw new InvalidOperationException(L.Get("Inventory.JumpHost"));
        return ssh.Connect(server);
    }

    private List<PortForward> ReadAndStore(SshClient client, ServerEntry server)
    {
        var r = RemoteShell.Run(client, server, "iptables -t nat -S", elevated: true);
        if (RemoteShell.SudoFailed(r)) throw new InvalidOperationException(L.Get("Fwd.NeedRoot") + "\n" + r.Error.Trim());
        if (!r.Ok) throw new InvalidOperationException(r.Combined);
        var list = IptablesParser.ParseNat(r.Output);
        vault.UpdateFacts(server.Id, f => f.Forwards = list);
        return list;
    }
}
