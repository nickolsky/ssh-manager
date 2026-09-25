using System.Net;
using System.Net.Sockets;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Scripts;
using SshManager.Core.Storage;

namespace SshManager.Tests;

public class ScriptManifestTests
{
    private const string Body = """
        #!/usr/bin/env bash
        # @name Demo
        # @name_en Demo EN
        # @os ubuntu,debian:12
        # @description line one
        # @description line two
        # @param DOMAIN text required label="Домен маски" label_en="Mask domain" default=dl.google.com hint="Сайт \"для\" SNI"
        # @param MODE choice options=xhttp,tcp default=xhttp
        # @param PATH_X text default=/x when=MODE=xhttp
        # @param PORT number default=443
        # @param DEBUG bool
        # @param TOKEN secret
        # @param bad-name text
        # @result URL label="Ссылка"
        # @result PORT label=Порт monitor=Xray
        echo hi
        """;

    [Fact]
    public void Parses_Params_And_Results()
    {
        var before = L.Language;
        try
        {
            L.Language = L.Russian;
            var m = ScriptManifest.Parse(Body);
            Assert.Equal("Demo", m.Name);
            Assert.Equal("ubuntu,debian:12", m.Os);
            Assert.Equal("line one\nline two", m.Description);
            Assert.Equal(["DOMAIN", "MODE", "PATH_X", "PORT", "DEBUG", "TOKEN"], m.Params.Select(p => p.Name).ToArray());

            var domain = m.Params[0];
            Assert.Equal("Домен маски", domain.Label);
            Assert.Equal("dl.google.com", domain.Default);
            Assert.Equal("Сайт \"для\" SNI", domain.Hint);
            Assert.True(domain.Required);

            Assert.Equal(ScriptParamType.Choice, m.Params[1].Type);
            Assert.Equal(["xhttp", "tcp"], m.Params[1].Options);
            Assert.Equal(ScriptParamType.Number, m.Params[3].Type);
            Assert.Equal(ScriptParamType.Bool, m.Params[4].Type);
            Assert.Equal(ScriptParamType.Secret, m.Params[5].Type);

            var path = m.Params[2];
            Assert.True(path.IsVisible(new Dictionary<string, string> { ["MODE"] = "xhttp" }));
            Assert.False(path.IsVisible(new Dictionary<string, string> { ["MODE"] = "tcp" }));

            Assert.Equal("Ссылка", m.Result("URL")!.Label);
            Assert.Equal("Xray", m.Result("PORT")!.MonitorName);

            L.Language = L.English;
            var en = ScriptManifest.Parse(Body);
            Assert.Equal("Demo EN", en.Name);
            Assert.Equal("Mask domain", en.Params[0].Label);
        }
        finally
        {
            L.Language = before;
        }
    }

    [Fact]
    public void Initial_Values_And_Validation()
    {
        var m = ScriptManifest.Parse(Body);
        var v = m.InitialValues(new Dictionary<string, string> { ["PORT"] = "8443", ["TOKEN"] = "never-restored" });
        Assert.Equal("dl.google.com", v["DOMAIN"]);
        Assert.Equal("xhttp", v["MODE"]);
        Assert.Equal("8443", v["PORT"]);
        Assert.Equal(ScriptParam.False, v["DEBUG"]);
        Assert.Equal("", v["TOKEN"]);
        Assert.Null(m.Validate(v));

        v["PORT"] = "abc";
        Assert.NotNull(m.Validate(v));
        v["PORT"] = "443";
        v["DOMAIN"] = " ";
        Assert.NotNull(m.Validate(v));
        v["DOMAIN"] = "x";
        v["MODE"] = "quic";
        Assert.NotNull(m.Validate(v));
    }

    [Fact]
    public void Result_Lines_And_Output_Split()
    {
        var r = ScriptManifest.ParseResults("A=1\r\nURL=vless://x@y:443?a=b&c=d#name\nnot a line\n1BAD=2\nA=3\n");
        Assert.Equal("3", r["A"]);
        Assert.Equal("vless://x@y:443?a=b&c=d#name", r["URL"]);
        Assert.Equal(2, r.Count);

        var (output, results) = ScriptRunner.Split("install...\ndone\n\n" + ScriptRunner.ResultMarker + "\nURL=u\n");
        Assert.Equal("install...\ndone\n\n", output);
        Assert.Equal("u", results["URL"]);
        Assert.Empty(ScriptRunner.Split("no marker").Results);
    }

    [Fact]
    public void Output_Cleanup()
    {
        Assert.Equal("red ok", ScriptRunner.CleanOutput("\u001b[31mred\u001b[0m ok"));
        Assert.Equal("100%\nnext", ScriptRunner.CleanOutput("10%\r50%\r100%\r\nnext"));
    }

    [Fact]
    public void Env_File_Quotes_Values_And_Skips_Bad_Names()
    {
        var server = new ServerEntry { Name = "it's", Host = "1.2.3.4" };
        var env = ScriptRunner.EnvFile(server, new Dictionary<string, string>
        {
            ["DOMAIN"] = "a b'c",
            ["bad-name"] = "x",
            ["SSHM_RESULT"] = "/etc/passwd",
            ["MULTI"] = "one\ntwo",
        }, "/tmp/r");
        Assert.Contains("SSHM_SERVER_NAME='it'\\''s'", env);
        Assert.Contains("DOMAIN='a b'\\''c'", env);
        Assert.Contains("MULTI='one two'", env);
        Assert.Contains("SSHM_RESULT='/tmp/r'", env);
        Assert.DoesNotContain("bad-name", env);
        Assert.DoesNotContain("/etc/passwd", env);
    }

    [Fact]
    public void Os_Filter_With_Versions()
    {
        var debian12 = new ServerFacts { OsId = "debian", OsVersion = "12" };
        var ubuntu = new ServerFacts { OsId = "ubuntu", OsLike = "debian", OsVersion = "24.04" };
        Assert.True(new ScriptEntry { OsFilter = "debian:12" }.Matches(debian12));
        Assert.False(new ScriptEntry { OsFilter = "debian:12" }.Matches(ubuntu));
        Assert.True(new ScriptEntry { OsFilter = "debian" }.Matches(ubuntu)); // ID_LIKE
        Assert.True(new ScriptEntry { OsFilter = "ubuntu:24" }.Matches(ubuntu));
        Assert.False(new ScriptEntry { OsFilter = "ubuntu:22.04" }.Matches(ubuntu));
        Assert.True(new ScriptEntry { OsFilter = "" }.Matches(null));
        Assert.False(new ScriptEntry { OsFilter = "ubuntu" }.Matches(null));

        // the CentOS / RHEL family by ID_LIKE, as their /etc/os-release has it
        var el = new ScriptEntry { OsFilter = "ubuntu,debian,centos,rhel" };
        Assert.True(el.Matches(new ServerFacts { OsId = "centos", OsLike = "rhel fedora", OsVersion = "9" }));
        Assert.True(el.Matches(new ServerFacts { OsId = "rocky", OsLike = "rhel centos fedora", OsVersion = "9.4" }));
        Assert.True(el.Matches(new ServerFacts { OsId = "almalinux", OsLike = "rhel centos fedora", OsVersion = "9.4" }));
        Assert.True(el.Matches(new ServerFacts { OsId = "rhel", OsLike = "fedora", OsVersion = "9.4" }));
        Assert.False(el.Matches(new ServerFacts { OsId = "fedora", OsVersion = "40" }));
    }
}

public class ContainerControlTests
{
    [Fact]
    public void Inspect_Adds_Policy_And_Compose_Labels()
    {
        var list = new List<ContainerInfo> { new() { Name = "uptime-kuma", Image = "louislam/uptime-kuma:1" }, new() { Name = "xray-reality" } };
        Core.Inventory.DockerInspectParser.Apply(
            "/uptime-kuma|unless-stopped|uptime-kuma|/opt/uptime-kuma|uptime-kuma\n/xray-reality|no|<no value>|<no value>|<no value>\n/gone|always|||\n", list);
        Assert.Equal("unless-stopped", list[0].RestartPolicy);
        Assert.True(list[0].Autostart);
        Assert.Equal("/opt/uptime-kuma", list[0].ComposeDir);
        Assert.Equal("no", list[1].RestartPolicy);
        Assert.False(list[1].Autostart);
        Assert.Null(list[1].ComposeProject);
    }

    [Fact]
    public void Remove_Commands()
    {
        var single = new ContainerInfo { Name = "web", Image = "nginx:alpine" };
        var cmd = Core.Inventory.ContainerCommands.Remove(single, new(false, true, true, true));
        Assert.Contains("docker rm -fv 'web'", cmd);
        Assert.Contains("docker rmi 'nginx:alpine'", cmd);
        Assert.DoesNotContain("rm -rf", cmd);

        var kuma = new ContainerInfo { Name = "uptime-kuma", ComposeProject = "uptime-kuma", ComposeDir = "/opt/uptime-kuma" };
        cmd = Core.Inventory.ContainerCommands.Remove(kuma, new(true, true, false, true));
        Assert.Contains("docker compose -p 'uptime-kuma' down --remove-orphans -v", cmd);
        Assert.Contains("rm -rf -- '/opt/uptime-kuma'", cmd);

        var risky = new ContainerInfo { Name = "x", ComposeProject = "x", ComposeDir = "/etc" };
        Assert.DoesNotContain("rm -rf", Core.Inventory.ContainerCommands.Remove(risky, new(true, false, false, true)));
        Assert.Equal("docker update --restart=always 'web'", Core.Inventory.ContainerCommands.SetRestart(single, "always"));
        Assert.Throws<ArgumentException>(() => Core.Inventory.ContainerCommands.SetRestart(single, "sometimes; rm -rf /"));
    }

    [Fact]
    public void Service_Autostart_And_Commands()
    {
        var services = new List<ServiceInfo>
        {
            new() { Unit = "nginx", Title = "nginx", Active = "active" },
            new() { Unit = "x-ui", Title = "3X-UI", Active = "inactive" },
            new() { Unit = "ssh", Title = "OpenSSH", Active = "active" },
        };
        Core.Inventory.ServiceListParser.ApplyUnitFiles(
            "nginx.service enabled enabled\nx-ui.service disabled enabled\nssh.service enabled enabled\nother.service static -\n", services);
        Assert.True(services[0].Autostart);
        Assert.False(services[1].Autostart);
        Assert.Equal("disabled", services[1].Enabled);
        Assert.Equal("systemctl disable 'nginx.service'", Core.Inventory.ServiceCommands.Disable(services[0]));
        Assert.Equal("systemctl start 'x-ui.service'", Core.Inventory.ServiceCommands.Start(services[1]));
        Assert.True(Core.Inventory.ServiceCommands.IsSsh(services[2]));
        Assert.False(Core.Inventory.ServiceCommands.IsSsh(services[0]));
    }

    [Fact]
    public void Kuma_Uses_Current_Major_Version() =>
        Assert.Contains("image: louislam/uptime-kuma:2", BuiltinScripts.All.Single(b => b.Id == "uptime-kuma").Body);

    [Theory]
    [InlineData("/opt/app", true)]
    [InlineData("/root/app", true)]
    [InlineData("/home/bob/app", true)]
    [InlineData("/srv/data/app", true)]
    [InlineData("/opt", false)]
    [InlineData("/home/bob", false)]
    [InlineData("/etc/app", false)]
    [InlineData("/var/lib/docker", false)]
    [InlineData("/opt/../etc", false)]
    [InlineData("relative/app", false)]
    [InlineData(null, false)]
    public void Only_Project_Folders_Are_Deleted(string? dir, bool ok) =>
        Assert.Equal(ok, Core.Inventory.ContainerCommands.SafeFolder(dir));
}

public class ComposeScriptTests
{
    [Fact]
    public void Template_Results_And_Project_Name()
    {
        var m = ScriptManifest.Parse("# @project my-app\n# @result URL label=U value=\"http://${SSHM_HOST}:${PORT}/x\"\n# @result RAW label=R\n");
        var r = m.TemplateResults(new Dictionary<string, string> { ["SSHM_HOST"] = "1.2.3.4", ["PORT"] = "8080" });
        Assert.Equal("http://1.2.3.4:8080/x", r["URL"]);
        Assert.False(r.ContainsKey("RAW"));
        Assert.Equal("my-app", ScriptManifest.ProjectFor(new ScriptEntry { Name = "x" }, m));

        var none = ScriptManifest.Parse("# @project ../etc\n");
        Assert.Null(none.Project); // not a safe folder name
        Assert.Equal("uptime-kuma-2", ScriptManifest.ProjectFor(new ScriptEntry { Name = "Uptime Kuma 2" }, none));
        Assert.Equal("sshm-app", ScriptManifest.ProjectFor(new ScriptEntry { Name = "Мониторинг" }, none));
    }

    [Fact]
    public void DotEnv_Quotes_And_Deploy_Script()
    {
        var env = ScriptRunner.DotEnv(new Dictionary<string, string> { ["A"] = "plain $x", ["B"] = "it's \"q\" $y", ["bad-name"] = "z" });
        Assert.Contains("A='plain $x'", env);
        Assert.Contains("B=\"it's \\\"q\\\" $$y\"", env);
        Assert.DoesNotContain("bad-name", env);

        var deploy = ScriptRunner.ComposeDeploy("uptime-kuma");
        Assert.DoesNotContain("\r", deploy);
        Assert.Contains("DIR=\"/opt/uptime-kuma\"", deploy);
        Assert.Contains("docker compose up -d", deploy);
    }

    [Fact]
    public void Docker_And_Compose_Builtins()
    {
        var docker = BuiltinScripts.All.Single(b => b.Id == BuiltinScripts.DockerId);
        Assert.Equal(ScriptKind.Bash, docker.Kind);
        Assert.NotNull(docker.Manifest.Result("DOCKER_VERSION"));
        Assert.Contains("get.docker.com", docker.Body);

        var kuma = BuiltinScripts.All.Single(b => b.Id == "uptime-kuma");
        Assert.Equal(ScriptKind.Compose, kuma.Kind);
        Assert.Equal("uptime-kuma", kuma.Manifest.Project);
        Assert.Equal("Uptime Kuma", kuma.Manifest.Result("KUMA_PORT")!.MonitorName);

        var data = new VaultData();
        BuiltinScripts.Sync(data);
        Assert.Equal(ScriptKind.Compose, data.Scripts.Single(s => s.BuiltinId == "uptime-kuma").Kind);
        data.Scripts.RemoveAll(s => s.BuiltinId == BuiltinScripts.DockerId);
        Assert.Equal(docker.Body, BuiltinScripts.DockerScript(data).Body); // still available after deleting it
    }
}

public class BuiltinScriptTests
{
    [Fact]
    public void Vless_Scripts_Are_Embedded_With_Metadata()
    {
        Assert.Contains(BuiltinScripts.All, b => b.Id == "vless-reality-ubuntu");
        Assert.Contains(BuiltinScripts.All, b => b.Id == "vless-reality-debian12");
        foreach (var b in BuiltinScripts.All.Where(b => b.Id.StartsWith("vless-")))
        {
            var m = b.Manifest;
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.Contains(m.Params, p => p.Name == "XRAY_PORT" && p.Type == ScriptParamType.Number);
            Assert.Contains(m.Params, p => p.Name == "XHTTP_PATH" && p.WhenName == "XRAY_TRANSPORT");
            Assert.NotNull(m.Result("VLESS_URL"));
            Assert.Equal("Xray", m.Result("XRAY_PORT")!.MonitorName);
            Assert.Contains("sshm_result VLESS_URL", b.Body);
            Assert.DoesNotContain("\r", b.Body);
            Assert.Null(m.Validate(m.InitialValues(null)));
        }
        Assert.Equal("debian:12", BuiltinScripts.All.Single(b => b.Id == "vless-reality-debian12").Manifest.Os);
    }

    [Theory]
    [InlineData("hysteria2", "VPN", "HY2_URL")]
    [InlineData("amneziawg", "VPN", "AWG_CLIENTS_DIR")]
    [InlineData("static-site-nginx", "Web", "SITE_URL")]
    [InlineData("static-site-docker", "Web", "SITE_URL")]
    [InlineData("nextcloud", "Cloud", "NC_URL")]
    [InlineData("seafile", "Cloud", "SF_URL")]
    [InlineData("filebrowser", "Cloud", "FB_URL")]
    public void Ubuntu_Debian_CentOS_Builtins_Have_Metadata(string id, string group, string mainResult)
    {
        var b = BuiltinScripts.All.Single(x => x.Id == id);
        var m = b.Manifest;
        Assert.Equal(ScriptKind.Bash, b.Kind);
        Assert.StartsWith("#!/usr/bin/env bash\n", b.Body);
        Assert.DoesNotContain("\r", b.Body);
        Assert.DoesNotContain("#@@", b.Body); // shared helpers were spliced in
        Assert.Equal("ubuntu,debian,centos,rhel", m.Os);
        Assert.Equal(group, m.Group);
        Assert.False(string.IsNullOrWhiteSpace(m.Name));
        Assert.False(string.IsNullOrWhiteSpace(m.Description));
        Assert.NotNull(m.Result(mainResult));
        Assert.Null(m.Validate(m.InitialValues(null)));
        var code = string.Join('\n', b.Body.Split('\n').Where(l => !l.StartsWith('#')));
        foreach (var p in m.Params) Assert.Contains(p.Name, code);
        foreach (var r in m.Results)
            Assert.Contains(r.IsPattern ? $"sshm_result \"{r.Name[..^1]}" : $"sshm_result {r.Name} ", code);
        Assert.Contains("require_os", code);
        Assert.DoesNotContain("apt_install", code); // pkg_install: apt or dnf
        // docker compose prefers the environment over .env, and the parameters are exported: a ${NAME} in a generated
        // compose file (\${NAME} in the script) named like a parameter would take the raw parameter value instead
        foreach (System.Text.RegularExpressions.Match x in System.Text.RegularExpressions.Regex.Matches(code, @"\\\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
            Assert.DoesNotContain(m.Params, p => p.Name == x.Groups[1].Value);
    }

    /// <summary>The built-in scripts are standalone files, so shared helpers are copied: every copy must stay the same.</summary>
    [Fact]
    public void Shared_Helper_Blocks_Are_Identical_In_Every_Script()
    {
        var blocks = new Dictionary<string, (string Script, string Text)>();
        var found = 0;
        foreach (var b in BuiltinScripts.All)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(b.Body,
                         @"# ---- shared helpers: (\w+) .*?\n(.*?)# ---- end of shared helpers: \1 ----", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                found++;
                var tag = m.Groups[1].Value;
                if (blocks.TryGetValue(tag, out var first))
                    Assert.True(first.Text == m.Groups[2].Value, $"shared helpers '{tag}' differ between {first.Script} and {b.Id}");
                else blocks[tag] = (b.Id, m.Groups[2].Value);
            }
        }
        Assert.Equal(["base", "services", "site"], blocks.Keys.Order());
        Assert.True(found >= 7 + 7 + 2, $"only {found} blocks");
    }

    [Fact]
    public void Pattern_Results_Get_Labels_And_Stale_Ones_Are_Found()
    {
        var m = ScriptManifest.Parse("# @result AWG_KEY_* label=\"Amnezia VPN\"\n# @result AWG_PORT label=Port\n# @result BAD*X*\n");
        Assert.Equal(2, m.Results.Count);
        Assert.Equal("Amnezia VPN — phone", m.Result("AWG_KEY_phone")!.Label);
        Assert.Equal("AWG_KEY_phone", m.Result("AWG_KEY_phone")!.Name);
        Assert.Null(m.Result("AWG_KEY_")); // the bare prefix is not a client
        Assert.Equal("Port", m.Result("AWG_PORT")!.Label);
        var reported = new Dictionary<string, string> { ["AWG_KEY_phone"] = "vpn://x", ["AWG_PORT"] = "1" };
        Assert.True(m.IsStale("AWG_KEY_laptop", reported));
        Assert.False(m.IsStale("AWG_KEY_phone", reported));
        Assert.False(m.IsStale("OTHER", reported)); // only what a pattern covers is dropped
    }

    /// <summary>Qt's qCompress: 4-byte big-endian length + zlib stream, as the AmneziaWG script writes it.</summary>
    private static string VpnKey(string json)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        ms.Write([(byte)(raw.Length >> 24), (byte)(raw.Length >> 16), (byte)(raw.Length >> 8), (byte)raw.Length]);
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        return "vpn://" + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public void Amnezia_Keys_Decode_To_The_Config()
    {
        var conf = "[Interface]\nPrivateKey = abc\nJc = 4\n\n[Peer]\nEndpoint = 1.2.3.4:51820\n";
        var last = System.Text.Json.JsonSerializer.Serialize(new { config = conf, hostName = "1.2.3.4" });
        var key = VpnKey(System.Text.Json.JsonSerializer.Serialize(new
        {
            containers = new object[] { new Dictionary<string, object> { ["container"] = "amnezia-awg", ["awg"] = new { last_config = last } } },
            defaultContainer = "amnezia-awg",
            description = "vps — phone",
        }));
        Assert.Equal(conf, AmneziaKey.WireGuardConfig(key));
        Assert.Equal("vps — phone", AmneziaKey.Description(key));
        Assert.Null(AmneziaKey.Decode("vpn://not-base64!"));
        Assert.Null(AmneziaKey.Decode("vless://x"));
        Assert.Null(AmneziaKey.Decode(key[..^6])); // truncated

        var qr = QrPayloads.For(key);
        Assert.Equal(2, qr.Count);
        Assert.Equal(conf, qr[0].Text);       // AmneziaWG and Amnezia VPN scan the .conf
        Assert.Equal(conf, qr[0].FileText);
        Assert.Equal(key[6..], qr[1].Text);   // Amnezia VPN's own QR codes carry the key without "vpn://"
        Assert.Equal(key, qr[1].CopyText);
    }

    [Fact]
    public void Qr_Codes_Are_Offered_For_Links_Only()
    {
        Assert.Single(QrPayloads.For("vless://id@1.2.3.4:443?security=reality#x"));
        Assert.Null(QrPayloads.For("hysteria2://pw@1.2.3.4:443/?sni=a#b")[0].Title);
        Assert.True(QrPayloads.CanShow("https://cloud.example.com"));
        Assert.False(QrPayloads.CanShow("443"));
        Assert.False(QrPayloads.CanShow("/opt/amneziawg/clients"));
        Assert.False(QrPayloads.CanShow("vless://a b"));
        Assert.False(QrPayloads.CanShow("vless://" + new string('a', QrPayloads.MaxBytes)));
        Assert.Single(QrPayloads.For("vpn://AAAA")); // not decodable: only the key itself
    }

    [Fact]
    public void Groups_Are_Ordered_And_Translated()
    {
        Assert.True(ScriptManifest.GroupOrder("VPN") < ScriptManifest.GroupOrder("web"));
        Assert.True(ScriptManifest.GroupOrder("Cloud") < ScriptManifest.GroupOrder("Mine"));
        Assert.Equal("Mine", ScriptManifest.GroupLabel("Mine"));
        Assert.Equal("VPN", ScriptManifest.Parse("# @group VPN\necho").Group);
        Assert.Null(ScriptManifest.Parse("# @group\necho").Group);
        Assert.All(BuiltinScripts.All.Where(b => b.Id.StartsWith("vless-")), b => Assert.Equal("VPN", b.Manifest.Group));
    }

    [Fact]
    public void Sync_Adds_Updates_And_Respects_Edits()
    {
        var v1 = new BuiltinScript("demo", "# @name Demo\n# @os ubuntu\necho 1\n");
        var v2 = new BuiltinScript("demo", "# @name Demo\n# @os ubuntu\necho 2\n");
        var data = new VaultData();

        Assert.True(BuiltinScripts.Sync(data, [v1]));
        var s = Assert.Single(data.Scripts);
        Assert.Equal("Demo", s.Name);
        Assert.Equal("ubuntu", s.OsFilter);
        Assert.False(BuiltinScripts.Sync(data, [v1]));

        Assert.True(BuiltinScripts.Sync(data, [v2])); // unedited copy follows the update
        Assert.Contains("echo 2", s.Body);

        s.Body += "echo mine\n";
        var v3 = new BuiltinScript("demo", "# @name Demo\necho 3\n");
        Assert.False(BuiltinScripts.Sync(data, [v3])); // edited copy is left alone
        Assert.Contains("echo mine", s.Body);

        // a built-in the app no longer ships: an unedited copy goes, an edited one stays as the user's script
        var gone = new BuiltinScript("gone", "# @name Gone\necho gone\n");
        var kept = new BuiltinScript("kept", "# @name Kept\necho kept\n");
        var fresh = new VaultData();
        BuiltinScripts.Sync(fresh, [gone, kept, v3]);
        fresh.Scripts.Single(x => x.BuiltinId == "kept").Body += "echo mine\n";
        Assert.True(BuiltinScripts.Sync(fresh, [v3]));
        Assert.DoesNotContain(fresh.Scripts, x => x.BuiltinId == "gone");
        Assert.Contains(fresh.Scripts, x => x.BuiltinId == "kept");
        Assert.False(BuiltinScripts.Sync(fresh, [v3]));

        data.Scripts.Clear();
        data.RemovedBuiltins.Add("demo");
        Assert.False(BuiltinScripts.Sync(data, [v3])); // deleted by the user
        Assert.Empty(data.Scripts);
    }
}

public class UptimeTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UptimeSample S(int minute, bool up, bool ssh = true, int interval = 5, int? latency = 40) =>
        new(T0.AddMinutes(minute), up, up && ssh, up ? latency : null, interval);

    [Fact]
    public void Percent_Is_Time_Weighted_And_Ignores_Gaps()
    {
        // up 0..10, down 10..20, up 20..30; then the app was closed for an hour; up 90..100
        var samples = new[] { S(0, true), S(5, true), S(10, false), S(15, false), S(20, true), S(25, true), S(90, true), S(95, true) };
        var to = T0.AddMinutes(100);
        var p = UptimeLog.Percent(samples, T0, to)!.Value;
        // observed: 0..36 (last sample before the gap covers 2 intervals + 1 min) and 90..100
        Assert.Equal(100.0 * (36 - 10 + 10) / (36 + 10), p, 3);

        var spans = UptimeLog.Spans(samples, T0, to);
        Assert.Contains(spans, s => s.State == UptimeState.NoData && s.FromUtc == T0.AddMinutes(36) && s.ToUtc == T0.AddMinutes(90));
        var outage = Assert.Single(UptimeLog.Outages(samples, T0, to));
        Assert.Equal(TimeSpan.FromMinutes(10), outage.Duration);

        Assert.Null(UptimeLog.Percent([], T0, to));
    }

    [Fact]
    public void Port_Only_Counts_As_Up_But_Is_Marked()
    {
        var samples = new[] { S(0, true), S(5, true, ssh: false), S(10, true) };
        var to = T0.AddMinutes(15);
        Assert.Equal(100, UptimeLog.Percent(samples, T0, to));
        Assert.Contains(UptimeLog.Spans(samples, T0, to), s => s.State == UptimeState.Degraded && s.Duration == TimeSpan.FromMinutes(5));

        var buckets = UptimeLog.Buckets(samples, T0, to, 3);
        Assert.Equal(3, buckets.Count);
        Assert.Equal(0, buckets[0].DegradedShare);
        Assert.Equal(1, buckets[1].DegradedShare, 3);
        Assert.Equal(100, buckets[1].UpPercent);
        Assert.Equal(40, buckets[0].AvgLatencyMs);
    }

    [Fact]
    public void Buckets_Split_Down_Time()
    {
        var samples = new[] { S(0, true), S(5, false), S(10, true) };
        var b = UptimeLog.Buckets(samples, T0, T0.AddMinutes(20), 2);
        Assert.Equal(50, b[0].UpPercent!.Value, 3);
        Assert.Equal(100, b[1].UpPercent!.Value, 3);
        Assert.Null(UptimeLog.Buckets(samples, T0.AddDays(-1), T0.AddDays(-1).AddMinutes(10), 1)[0].UpPercent);
    }

    [Fact]
    public void Log_Round_Trips_And_Drops_Old_Samples()
    {
        using var tmp = new TempDir();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var old = new UptimeSample(now - UptimeLog.Retention - TimeSpan.FromDays(1), true, true, 10, 5);
        var fresh = new UptimeSample(new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc), false, false, null, 5);
        Directory.CreateDirectory(tmp.File("uptime"));
        File.WriteAllText(Path.Combine(tmp.File("uptime"), id.ToString("N") + ".csv"),
            UptimeLog.Format(old) + "\ngarbage\n" + UptimeLog.Format(fresh) + "\n");

        var log = new UptimeLog(tmp.File("uptime"));
        Assert.Equal([fresh], log.Read(id, now.AddDays(-1)));
        log.Append(id, fresh with { Utc = fresh.Utc.AddMinutes(5), Up = true, SshUp = true, LatencyMs = 12 });

        var reread = new UptimeLog(tmp.File("uptime")).Read(id, now.AddDays(-1));
        Assert.Equal(2, reread.Count);
        Assert.Equal(12, reread[1].LatencyMs);
        Assert.Equal(DateTimeKind.Utc, reread[0].Utc.Kind);

        log.Delete(id);
        Assert.Empty(log.Read(id, DateTime.MinValue));
    }

    [Fact]
    public async Task Open_Monitored_Port_Keeps_Server_Up_Without_Ssh()
    {
        using var tmp = new TempDir();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop(); // nothing listens there any more
        try
        {
            var vault = new VaultService(tmp.File("vault.dat"));
            vault.Create("password123");
            var server = new ServerEntry
            {
                Name = "vpn", Host = "127.0.0.1", Port = closedPort,
                MonitoredPorts = [new MonitoredPort { Port = openPort, Name = "xray" }],
            };
            vault.Update(d => d.Servers.Add(server));
            var log = new UptimeLog(tmp.File("uptime"));
            using var monitor = new HealthMonitor(vault, new SettingsService(), log);
            var done = new TaskCompletionSource();
            monitor.HealthChanged += (_, id) =>
            {
                if (monitor.Get(id).State is HealthState.Online or HealthState.Offline) done.TrySetResult();
            };
            monitor.CheckNow(server.Id);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var h = monitor.Get(server.Id);
            Assert.Equal(HealthState.Online, h.State);
            Assert.True(h.SshDown);
            Assert.NotNull(h.Error);
            var sample = Assert.Single(log.Read(server.Id, DateTime.UtcNow.AddMinutes(-5)));
            Assert.True(sample.Up);
            Assert.False(sample.SshUp);
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class SettingsMigrationTests
{
    [Fact]
    public void Old_Default_Interval_Becomes_Five_Minutes()
    {
        var old = new AppSettings { MonitorIntervalMinutes = 10, SettingsVersion = 0 };
        Assert.True(SettingsService.Migrate(old));
        Assert.Equal(5, old.MonitorIntervalMinutes);
        Assert.False(SettingsService.Migrate(old));

        var custom = new AppSettings { MonitorIntervalMinutes = 15, SettingsVersion = 0 };
        SettingsService.Migrate(custom);
        Assert.Equal(15, custom.MonitorIntervalMinutes);
        Assert.Equal(5, new AppSettings().MonitorIntervalMinutes);
    }
}
