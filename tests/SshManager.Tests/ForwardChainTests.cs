using System.Diagnostics;
using SshManager.Core.Forwarding;
using SshManager.Core.Models;

namespace SshManager.Tests;

public class ForwardChainTests
{
    private static ServerEntry Server(string name, string ip, params PortForward[] forwards) =>
        new() { Name = name, Host = ip, Facts = new ServerFacts { Forwards = forwards.ToList() } };

    private static PortForward Fwd(string listen, string ip, string port = "", string proto = "tcp") =>
        new() { Protocol = proto, ListenPort = listen, TargetIp = ip, TargetPort = port, Comment = "sshm:abcd" };

    private static Func<string, ServerEntry?> ByIp(params ServerEntry[] servers) => ip => servers.FirstOrDefault(s => s.Host == ip);

    [Fact]
    public void A_Chain_Over_Four_Servers_Is_Followed_To_Its_End()
    {
        // A:443 → B:8443 → C:443 → D:443, where nginx listens
        var d = Server("D", "10.0.0.4");
        d.Facts!.ListeningPorts = [new ListeningPort { Port = 443, Process = "nginx" }];
        var c = Server("C", "10.0.0.3", Fwd("443", "10.0.0.4"));
        var b = Server("B", "10.0.0.2", Fwd("8443", "10.0.0.3", "443"));
        var a = Server("A", "10.0.0.1", Fwd("443", "10.0.0.2", "8443"));
        var byIp = ByIp(a, b, c, d);

        var chain = ForwardChains.Downstream(a, a.Facts!.Forwards[0], byIp);
        Assert.Equal("A:443 → B:8443 → C:443 → D:443 (nginx)", ForwardChains.Format(chain));

        // from the middle, the whole chain: who forwards into B, then onwards
        var full = ForwardChains.Full(b, b.Facts!.Forwards[0], [a, b, c, d], byIp);
        Assert.Equal("A:443 → B:8443 → C:443 → D:443 (nginx)", ForwardChains.Format(full));
        Assert.Equal("A:443\n→ B:8443\n→ C:443\n→ D:443  (nginx)", ForwardChains.FormatLines(full));
    }

    [Fact]
    public void The_Chain_Stops_At_Unknown_Addresses_Other_Protocols_And_Loops()
    {
        var b = Server("B", "10.0.0.2", Fwd("53", "10.0.0.9", proto: "udp"));
        var a = Server("A", "10.0.0.1", Fwd("53", "10.0.0.2")); // tcp: B only forwards udp 53
        Assert.Equal("A:53 → B:53", ForwardChains.Format(ForwardChains.Downstream(a, a.Facts!.Forwards[0], ByIp(a, b))));
        Assert.Equal("B:53 → 10.0.0.9:53", ForwardChains.Format(ForwardChains.Downstream(b, b.Facts!.Forwards[0], ByIp(a, b))));

        // X → Y → X: stops instead of going round
        var x = Server("X", "10.1.0.1", Fwd("80", "10.1.0.2"));
        var y = Server("Y", "10.1.0.2", Fwd("80", "10.1.0.1"));
        var loop = ForwardChains.Downstream(x, x.Facts!.Forwards[0], ByIp(x, y));
        Assert.Equal("X:80 → Y:80 → X:80", ForwardChains.Format(loop));
        Assert.True(ForwardChains.Full(x, x.Facts!.Forwards[0], [x, y], ByIp(x, y)).Count <= 4); // and back the same way
    }

    [Fact]
    public void Ranges_Carry_The_Port_On()
    {
        var c = Server("C", "10.0.0.3");
        var b = Server("B", "10.0.0.2", Fwd("40000:40100", "10.0.0.3")); // the same port on C
        var a = Server("A", "10.0.0.1", Fwd("40005", "10.0.0.2"));
        Assert.Equal("A:40005 → B:40005 → C:40005", ForwardChains.Format(ForwardChains.Downstream(a, a.Facts!.Forwards[0], ByIp(a, b, c))));
        Assert.True(ForwardChains.Covers("1000:2000", "1500"));
        Assert.True(ForwardChains.Covers("1000:2000", "1100:1200"));
        Assert.False(ForwardChains.Covers("1000:2000", "2001"));
        Assert.True(ForwardChains.Covers("443", "443"));
    }

    [Fact]
    public void Replace_Removes_Adds_And_Puts_The_Old_One_Back_On_Failure()
    {
        var old = IptablesParser.ParseNat("-A PREROUTING -p tcp -m tcp --dport 443 -m comment --comment \"sshm:0a1b2c3d\" -j DNAT --to-destination 203.0.113.5:443")[0];
        var s = IptablesCommands.Replace(old, [("1111aaaa", "tcp"), ("2222bbbb", "udp")], "8443", "203.0.113.9", "443");
        var removeOld = s.IndexOf("sshm:0a1b2c3d", StringComparison.Ordinal);
        var addNew = s.IndexOf("--comment sshm:1111aaaa -j DNAT --to-destination 203.0.113.9:443", StringComparison.Ordinal);
        Assert.True(removeOld >= 0 && addNew > removeOld, s);
        Assert.Contains("-p udp --dport 8443 -m comment --comment sshm:2222bbbb", s);
        // the undo: the new ids go, the old forward is added again with its own id
        Assert.Contains("-p tcp --dport 443 -m comment --comment sshm:0a1b2c3d -j DNAT --to-destination 203.0.113.5:443", s);
        Assert.Contains("the old one is back", s);
        Assert.EndsWith(IptablesCommands.Persist, s);

        // a forward made elsewhere comes back as its exact rule
        var external = IptablesParser.ParseNat("-A PREROUTING -i eth0 -p udp -m udp --dport 51820 -j DNAT --to-destination 198.51.100.7")[0];
        var e = IptablesCommands.Replace(external, [("3333cccc", "udp")], "51820", "198.51.100.8", "");
        Assert.Contains("iptables -t nat -D PREROUTING -i eth0 -p udp -m udp --dport 51820", e);
        Assert.Contains("iptables -t nat -A PREROUTING -i eth0 -p udp -m udp --dport 51820 -j DNAT --to-destination 198.51.100.7", e);
        CheckSyntax(s);
        CheckSyntax(e);
    }

    /// <summary>bash -n with Git's bash, when it is installed.</summary>
    private static void CheckSyntax(string script)
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var p = Process.Start(new ProcessStartInfo(bash, "-n") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.StandardInput.Write(script.Replace("\r\n", "\n"));
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }
}
