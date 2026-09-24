using System.IO.Compression;
using System.Text.RegularExpressions;
using SshManager.Core;
using SshManager.Core.Backup;
using SshManager.Core.Forwarding;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Tests;

public class LocalizationTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, AppPaths.SolutionMarker))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src");
    }

    [Fact]
    public void Every_Used_Key_Exists()
    {
        var used = new HashSet<string>();
        foreach (var f in Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"L\.(?:Get|F)\(""([A-Za-z0-9_.]+)""")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\{l:Tr ([A-Za-z0-9_.]+)\}")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"L\.Get\([^)]*?\?\s*""([A-Za-z0-9_.]+)""\s*:\s*""([A-Za-z0-9_.]+)""\)"))
            {
                used.Add(m.Groups[1].Value);
                used.Add(m.Groups[2].Value);
            }
        }
        Assert.True(used.Count > 200, $"only {used.Count} keys found — is the scan broken?");
        var missing = used.Where(k => !Strings.Table.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Missing translations: " + string.Join(", ", missing));
    }

    [Fact]
    public void Both_Languages_Have_Same_Placeholders()
    {
        foreach (var (key, (ru, en)) in Strings.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(ru), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
            Assert.True(Holes(ru) == Holes(en), $"{key}: '{Holes(ru)}' vs '{Holes(en)}'");
        }
    }

    [Fact]
    public void Switching_Language_Changes_Strings()
    {
        var before = L.Language;
        try
        {
            L.Language = L.English;
            Assert.Equal("Servers", L.Get("Tab.Servers"));
            L.Language = L.Russian;
            Assert.Equal("Серверы", L.Get("Tab.Servers"));
            Assert.Equal("no.such.key", L.Get("no.such.key"));
        }
        finally
        {
            L.Language = before;
        }
    }
}

public class InventoryParserTests
{
    [Fact]
    public void OsRelease_Ubuntu()
    {
        var f = new ServerFacts();
        OsReleaseParser.Apply("""
            PRETTY_NAME="Ubuntu 24.04.1 LTS"
            NAME="Ubuntu"
            VERSION_ID="24.04"
            ID=ubuntu
            ID_LIKE=debian
            """, f);
        Assert.Equal("ubuntu", f.OsId);
        Assert.Equal("debian", f.OsLike);
        Assert.Equal("Ubuntu 24.04", f.OsLabel);
        Assert.Equal("Ubuntu 24.04.1 LTS", f.OsPrettyName);
    }

    [Fact]
    public void OsRelease_Debian_Shortens_Name()
    {
        var f = new ServerFacts();
        OsReleaseParser.Apply("NAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\nID=debian\n", f);
        Assert.Equal("Debian 12", f.OsLabel);
    }

    [Fact]
    public void Docker_Ps_Json_Lines()
    {
        var list = DockerPsParser.Parse("""
            {"ID":"a1","Image":"ghcr.io/mhsanaei/3x-ui:latest","Names":"3x-ui","Ports":"0.0.0.0:2053->2053/tcp","State":"running","Status":"Up 3 days"}
            {"ID":"b2","Image":"nginx","Names":"web","Ports":"","Status":"Exited (0) 2 hours ago"}
            Cannot connect to the Docker daemon
            """);
        Assert.Equal(2, list.Count);
        Assert.True(list.Single(c => c.Name == "3x-ui").IsRunning);
        Assert.False(list.Single(c => c.Name == "web").IsRunning);
    }

    [Fact]
    public void Systemctl_Only_Well_Known()
    {
        var list = ServiceListParser.Parse("""
            cron.service                loaded active running Regular background program processing daemon
            nginx.service               loaded active running A high performance web server
            wg-quick@wg0.service        loaded active exited  WireGuard via wg-quick(8) for wg0
            ● x-ui.service              loaded failed failed  x-ui Service
            systemd-journald.service    loaded active running Journal Service
            docker.service              not-found inactive dead docker.service
            """);
        Assert.Equivalent(new[] { "WireGuard", "3X-UI", "nginx" }, list.Select(s => s.Title).ToArray(), strict: true);
        Assert.Equal("failed", list.Single(s => s.Unit == "x-ui").Active);
    }

    [Fact]
    public void Sections_Apply_To_Facts()
    {
        var sections = SectionParser.Split("""
            @@sshm:os
            ID=debian
            NAME="Debian GNU/Linux"
            VERSION_ID="12"
            @@sshm:kernel
            Linux 6.1.0-18-amd64 x86_64
            @@sshm:docker
            {"ID":"a1","Image":"nginx","Names":"web","State":"running","Status":"Up 1 hour"}
            @@sshm:nat
            -P PREROUTING ACCEPT
            -A PREROUTING -p tcp -m tcp --dport 443 -m comment --comment "sshm:ab12" -j DNAT --to-destination 10.1.2.3:8443
            @@sshm:end
            """);
        var f = new ServerFacts();
        ServerInventoryService.Apply(f, sections);
        Assert.Equal("Debian 12", f.OsLabel);
        Assert.Equal("Linux 6.1.0-18-amd64 x86_64", f.Kernel);
        Assert.True(f.DockerAvailable);
        Assert.Single(f.Containers);
        var fw = Assert.Single(f.Forwards);
        Assert.Equal("ab12", fw.ManagedId);
        Assert.NotNull(f.InventoryUpdated);
    }

    [Fact]
    public void Nat_Without_Root_Keeps_Previous_Forwards()
    {
        var f = new ServerFacts { Forwards = [new PortForward { ListenPort = "80", TargetIp = "1.1.1.1" }] };
        ServerInventoryService.Apply(f, SectionParser.Split("@@sshm:nat\niptables: Permission denied (you must be root).\n@@sshm:end\n"));
        Assert.Single(f.Forwards);
    }

    [Fact]
    public void Script_Matches_Os_Family()
    {
        var s = new ScriptEntry { OsFilter = "debian" };
        Assert.True(s.Matches(new ServerFacts { OsId = "ubuntu", OsLike = "debian" }));
        Assert.False(s.Matches(new ServerFacts { OsId = "almalinux", OsLike = "rhel centos fedora" }));
        Assert.False(s.Matches(null));
        Assert.True(new ScriptEntry().Matches(null));
    }
}

public class IptablesTests
{
    private const string Nat = """
        -P PREROUTING ACCEPT
        -P POSTROUTING ACCEPT
        -N DOCKER
        -A PREROUTING -m addrtype --dst-type LOCAL -j DOCKER
        -A PREROUTING -p tcp -m tcp --dport 443 -m comment --comment "sshm:0a1b2c3d" -j DNAT --to-destination 203.0.113.5:443
        -A PREROUTING -i eth0 -p udp -m udp --dport 51820 -j DNAT --to-destination 198.51.100.7
        -A POSTROUTING -d 203.0.113.5/32 -p tcp -m tcp --dport 443 -m comment --comment "sshm:0a1b2c3d" -j MASQUERADE
        -A DOCKER ! -i docker0 -p tcp -m tcp --dport 8080 -j DNAT --to-destination 172.17.0.2:80
        """;

    [Fact]
    public void Parses_Prerouting_Dnat_Only()
    {
        var list = IptablesParser.ParseNat(Nat);
        Assert.Equal(2, list.Count);
        var managed = list[0];
        Assert.True(managed.Managed);
        Assert.Equal("0a1b2c3d", managed.ManagedId);
        Assert.Equal(("tcp", "443", "203.0.113.5", "443"), (managed.Protocol, managed.ListenPort, managed.TargetIp, managed.TargetPort));
        var external = list[1];
        Assert.False(external.Managed);
        Assert.Equal("eth0", external.InInterface);
        Assert.Equal("51820", external.EffectiveTargetPort);
    }

    [Fact]
    public void Add_Script_Has_All_Rules_And_Persists()
    {
        var s = IptablesCommands.Add("abcd", "tcp", "443", "203.0.113.5", "");
        Assert.Contains("-t nat -A PREROUTING -p tcp --dport 443 -m comment --comment sshm:abcd -j DNAT --to-destination 203.0.113.5:443", s);
        Assert.Contains("-j MASQUERADE", s);
        Assert.Contains("-I FORWARD 1", s);
        Assert.Contains("net.ipv4.ip_forward=1", s);
        Assert.Contains("netfilter-persistent save", s);

        var range = IptablesCommands.Add("abcd", "udp", "1000:2000", "10.0.0.1", "3000:4000");
        Assert.Contains("--to-destination 10.0.0.1:3000-4000", range);
    }

    [Theory]
    [InlineData("tcp", "443; rm -rf /", "1.2.3.4", "")]
    [InlineData("icmp", "443", "1.2.3.4", "")]
    [InlineData("tcp", "70000", "1.2.3.4", "")]
    [InlineData("tcp", "443", "1.2.3.4 -j ACCEPT", "")]
    [InlineData("tcp", "443", "::1", "")]
    [InlineData("tcp", "443", "1.2.3.4", "80`id`")]
    public void Add_Rejects_Bad_Input(string proto, string port, string ip, string tport) =>
        Assert.Throws<ArgumentException>(() => IptablesCommands.Add("abcd", proto, port, ip, tport));

    [Fact]
    public void Remove_Managed_Deletes_By_Comment()
    {
        var f = IptablesParser.ParseNat(Nat)[0];
        var s = IptablesCommands.Remove(f);
        Assert.Contains("sshm:0a1b2c3d", s);
        Assert.Contains("s/^-A /-D /", s);
    }

    [Fact]
    public void Remove_External_Deletes_Exact_Rule()
    {
        var f = IptablesParser.ParseNat(Nat)[1];
        var s = IptablesCommands.Remove(f);
        Assert.Contains("iptables -t nat -D PREROUTING -i eth0 -p udp -m udp --dport 51820 -j DNAT --to-destination 198.51.100.7", s);
    }

    [Fact]
    public void Persist_Result()
    {
        Assert.Equal("none", IptablesCommands.PersistResult("SSHM_OK\nSSHM_PERSIST=none\n"));
        Assert.Null(IptablesCommands.PersistResult("SSHM_OK\n"));
    }

    [Fact]
    public void Shell_Quote() => Assert.Equal("'it'\\''s'", RemoteShell.Quote("it's"));
}

public class MetricsTests
{
    [Fact]
    public void Parses_Proc_Output()
    {
        var sections = SectionParser.Split("""
            @@sshm:stat1
            cpu  100 0 100 800 0 0 0 0 0 0
            @@sshm:stat2
            cpu  150 0 150 900 0 0 0 0 0 0
            @@sshm:load
            0.42 0.30 0.25 1/123 4567
            @@sshm:cores
            2
            @@sshm:mem
            MemTotal:        4000000 kB
            MemFree:          500000 kB
            MemAvailable:    3000000 kB
            @@sshm:disk
            /dev/vda1 41152736 10288184 28750844 27% /
            @@sshm:uptime
            90061.5 170000.0
            @@sshm:end
            """);
        var m = MetricsCollector.Parse(sections, DateTime.Now);
        Assert.Equal(50, m.CpuPercent!.Value, 1);
        Assert.Equal(0.42, m.Load1);
        Assert.Equal(2, m.Cores);
        Assert.Equal(25, m.MemPercent!.Value, 1);
        Assert.Equal(25, m.DiskPercent!.Value, 0);
        Assert.Equal(1, (int)m.Uptime!.Value.TotalDays);
    }
}

public class BackupTests
{
    [Fact]
    public async Task Folder_Backup_Zips_Data_And_Rotates()
    {
        using var tmp = new TempDir();
        var data = Directory.CreateDirectory(tmp.File("data")).FullName;
        var vault = new VaultService(Path.Combine(data, "vault.dat"));
        vault.Create("password123");
        File.WriteAllText(Path.Combine(data, "known_hosts"), "host ssh-ed25519 AAAA\n");
        File.WriteAllText(Path.Combine(data, "vault.dat.tmp"), "junk");
        Directory.CreateDirectory(Path.Combine(data, "pub"));
        File.WriteAllText(Path.Combine(data, "pub", "k.pub"), "ssh-ed25519 AAAA");

        var settings = new SettingsService();
        settings.Settings.Backup = new BackupSettings { Target = BackupTarget.Folder, Folder = tmp.File("out"), Keep = 2 };
        var ssh = new SshClientFactory(vault, new KnownHostsService(tmp.File("kh")));
        var backup = new BackupService(vault, settings, ssh, data);

        var first = await backup.RunAsync();
        using (var zip = ZipFile.OpenRead(first))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("vault.dat", names);
            Assert.Contains("known_hosts", names);
            Assert.Contains("pub/k.pub", names);
            Assert.DoesNotContain("vault.dat.tmp", names);
        }
        Assert.NotNull(settings.Settings.Backup.LastBackup);

        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(1100); // file names have second resolution
            await backup.RunAsync();
        }
        Assert.Equal(2, Directory.GetFiles(tmp.File("out"), BackupService.FilePrefix + "*.zip").Length);
    }
}
