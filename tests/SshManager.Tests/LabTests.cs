using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;
using Xunit.Abstractions;

namespace SshManager.Tests;

/// <summary>
/// Built-in install scripts on real Ubuntu / Debian / CentOS servers (tools/script-lab: systemd + sshd + Docker, or real VPSes),
/// run the way the app runs them, then checked with a real client: VPN links carry traffic, sites answer, logins work.
/// Enable with SSHM_LAB="ubuntu=127.0.0.1:2201:root:lab-root;debian=127.0.0.1:2202:root:lab-root".
/// SSHM_LAB_ONLY=vless,hysteria2 limits the scripts; SSHM_LAB_HEAVY=1 adds Nextcloud and Seafile (several GB of images).
/// Full script output goes to %TEMP%\sshm-lab\&lt;script&gt;-&lt;server&gt;.log.
/// </summary>
public partial class LabTests(ITestOutputHelper log)
{
    private sealed record Lab(ServerEntry Server, SshClientFactory Ssh, ScriptRunner Runner, VaultService Vault);

    private static readonly string LogDir = Path.Combine(Path.GetTempPath(), "sshm-lab");

    private static List<(string Name, string Host, int Port, string User, string Password)> Targets()
    {
        var v = Environment.GetEnvironmentVariable("SSHM_LAB");
        if (string.IsNullOrWhiteSpace(v)) return [];
        return v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t =>
        {
            var eq = t.IndexOf('=');
            var p = t[(eq + 1)..].Split(':', 4);
            return (t[..eq], p[0], int.Parse(p[1]), p[2], p[3]);
        }).ToList();
    }

    private static bool Enabled(string id, bool heavy = false)
    {
        if (Targets().Count == 0) return false;
        if (Environment.GetEnvironmentVariable("SSHM_LAB_ONLY") is { Length: > 0 } only)
            return only.Split(',', StringSplitOptions.TrimEntries).Contains(id);
        return !heavy || Environment.GetEnvironmentVariable("SSHM_LAB_HEAVY") == "1";
    }

    /// <summary>Runs <paramref name="check"/> for every lab server in parallel.</summary>
    private async Task OnEachServer(Func<Lab, Task> check)
    {
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var ssh = new SshClientFactory(vault, new KnownHostsService(tmp.File("known_hosts"))) { ConfirmHostKey = _ => true };
        var labs = Targets().Select(t => new ServerEntry { Name = "lab-" + t.Name, Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password })
            .ToList();
        vault.Update(d => d.Servers.AddRange(labs));
        var runner = new ScriptRunner(ssh);
        var failures = new List<string>();
        await Task.WhenAll(labs.Select(s => Task.Run(async () =>
        {
            try
            {
                await check(new Lab(s, ssh, runner, vault));
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add($"{s.Name}: {ex.Message}");
            }
        })));
        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    private async Task<Dictionary<string, string>> Run(Lab lab, string id, Dictionary<string, string> values, string tag = "")
    {
        var builtin = BuiltinScripts.All.Single(b => b.Id == id);
        var script = new ScriptEntry { Name = builtin.Manifest.Name ?? id, Body = builtin.Body, UseSudo = true, BuiltinId = id };
        var all = builtin.Manifest.InitialValues(null);
        foreach (var (k, v) in values) all[k] = v;
        Assert.Null(builtin.Manifest.Validate(all));
        var output = new StringBuilder();
        var started = DateTime.Now;
        var r = await lab.Runner.RunAsync(lab.Server, script, all, c => { lock (output) output.Append(c); }, CancellationToken.None);
        Directory.CreateDirectory(LogDir);
        var file = Path.Combine(LogDir, $"{id}{tag}-{lab.Server.Name}.log");
        File.WriteAllText(file, output.ToString());
        log.WriteLine($"{id}{tag} on {lab.Server.Name}: exit {r.ExitCode} in {(DateTime.Now - started).TotalSeconds:0}s, log {file}");
        var tail = string.Join('\n', output.ToString().Split('\n').TakeLast(40));
        Assert.True(r.Ok, $"{id}{tag} exited with {r.ExitCode}:\n{tail}");
        foreach (var res in builtin.Manifest.Results.Where(x => !x.Name.EndsWith("_PASSWORD") && !x.IsPattern))
            Assert.True(r.Results.ContainsKey(res.Name), $"{id}: no result {res.Name}\n{tail}");
        return r.Results;
    }

    private static string Sh(Lab lab, string command, bool check = true)
    {
        using var c = lab.Ssh.Connect(lab.Server);
        using var cmd = c.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(5);
        var output = cmd.Execute() + cmd.Error;
        if (check) Assert.True(cmd.ExitStatus == 0, $"`{command}` exited with {cmd.ExitStatus}:\n{output}");
        return output;
    }

    /// <summary>Pulls a client image on the server, with retries (registries time out now and then).</summary>
    private static void Pull(Lab lab, string image) =>
        Sh(lab, $"for i in 1 2 3 4 5; do docker pull -q {image} >/dev/null && exit 0; sleep $((i * 5)); done; exit 1");

    private static void Upload(Lab lab, string path, string text)
    {
        using var sftp = lab.Ssh.ConnectSftp(lab.Server);
        using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        sftp.UploadFile(ms, path);
    }

    /// <summary>curl through a local SOCKS proxy on the server: the HTTP status of a page on the internet.</summary>
    private static string ThroughSocks(Lab lab, int port) =>
        Sh(lab, $"for i in 1 2 3 4 5; do c=$(curl -s -o /dev/null -w '%{{http_code}}' --max-time 15 --socks5-hostname 127.0.0.1:{port} https://www.cloudflare.com/cdn-cgi/trace) && [ \"$c\" = 200 ] && break; sleep 2; done; echo \"$c\"", check: false).Trim();

    private static Dictionary<string, string> Query(string url)
    {
        var q = url[(url.IndexOf('?') + 1)..];
        q = q[..(q.IndexOf('#') is var h and >= 0 ? h : q.Length)];
        return q.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
    }

    // ---------- VPN ----------

    /// <summary>The VLESS script for the server's OS: the Ubuntu / CentOS one, or the Debian 12 one (other releases are skipped).</summary>
    private static string? VlessScriptFor(Lab lab)
    {
        var os = Sh(lab, ". /etc/os-release; echo \"$ID $VERSION_ID $ID_LIKE\"").Trim();
        if (os.StartsWith("ubuntu ") || os.Contains(" rhel") || os.Contains(" centos")) return "vless-reality-ubuntu";
        return os.StartsWith("debian 12 ") || os == "debian 12" ? "vless-reality-debian12" : null;
    }

    [Fact]
    public async Task Vless_Reality_Link_Carries_Traffic_Over_Xhttp_And_Tcp()
    {
        if (!Enabled("vless")) return;
        await OnEachServer(async lab =>
        {
            if (VlessScriptFor(lab) is not { } id)
            {
                log.WriteLine($"vless: no script for {lab.Server.Name}, skipped");
                return;
            }
            foreach (var transport in new[] { "xhttp", "tcp" })
            {
                var r = await Run(lab, id, new() { ["XRAY_PORT"] = "443", ["XRAY_TRANSPORT"] = transport, ["SERVER_ADDRESS"] = "127.0.0.1" }, "-" + transport);
                var url = r["VLESS_URL"];
                Assert.StartsWith("vless://", url);
                var uuid = url["vless://".Length..url.IndexOf('@')];
                var q = Query(url);
                Assert.Equal(transport, q["type"]);
                var stream = transport == "xhttp"
                    ? $$"""
                      "network": "xhttp",
                      "xhttpSettings": { "path": "{{q["path"]}}", "host": "{{q["host"]}}", "mode": "{{q["mode"]}}" },
                      """
                    : "\"network\": \"tcp\",";
                var flow = q.GetValueOrDefault("flow") is { } f ? $", \"flow\": \"{f}\"" : "";
                Upload(lab, "/tmp/sshm-xc.json", $$"""
                    {
                      "log": { "loglevel": "warning" },
                      "inbounds": [ { "listen": "127.0.0.1", "port": 10808, "protocol": "socks" } ],
                      "outbounds": [ {
                        "protocol": "vless",
                        "settings": { "vnext": [ { "address": "127.0.0.1", "port": 443, "users": [ { "id": "{{uuid}}", "encryption": "none"{{flow}} } ] } ] },
                        "streamSettings": {
                          {{stream}}
                          "security": "reality",
                          "realitySettings": { "serverName": "{{q["sni"]}}", "fingerprint": "{{q["fp"]}}", "publicKey": "{{q["pbk"]}}", "shortId": "{{q["sid"]}}", "spiderX": "{{q["spx"]}}" }
                        }
                      } ]
                    }
                    """);
                try
                {
                    Pull(lab, "ghcr.io/xtls/xray-core:latest");
                    Sh(lab, "docker rm -f sshm-xc >/dev/null 2>&1; docker run -d --name sshm-xc --network host -v /tmp/sshm-xc.json:/c.json:ro ghcr.io/xtls/xray-core:latest run -c /c.json");
                    var code = ThroughSocks(lab, 10808);
                    Assert.True(code == "200", $"{transport}: HTTP {code}\n" + Sh(lab, "docker logs --tail 30 sshm-xc", check: false));
                }
                finally
                {
                    Sh(lab, "docker rm -f sshm-xc; rm -f /tmp/sshm-xc.json", check: false);
                }
            }
            // the VPN port does not stay taken by the test's Xray for the next scripts
            Sh(lab, "cd /opt/xray && docker compose down >/dev/null 2>&1; true", check: false);
        });
    }

    [Fact]
    public async Task Hysteria2_Link_Works_With_And_Without_Obfs()
    {
        if (!Enabled("hysteria2")) return;
        await OnEachServer(async lab =>
        {
            foreach (var obfs in new[] { "0", "1" })
            {
                var r = await Run(lab, "hysteria2", new() { ["HY2_PORT"] = "443", ["HY2_OBFS"] = obfs, ["SERVER_ADDRESS"] = "127.0.0.1" }, "-obfs" + obfs);
                var url = r["HY2_URL"];
                Assert.StartsWith("hysteria2://", url);
                Assert.Contains("pinSHA256=", url);
                Assert.Equal(obfs == "1", url.Contains("obfs=salamander"));
                // the client takes the share link as is
                Upload(lab, "/tmp/sshm-hc.yaml", $"server: \"{url}\"\nsocks5:\n  listen: 127.0.0.1:10809\n");
                try
                {
                    Pull(lab, "tobyxdd/hysteria:v2");
                    Sh(lab, "docker rm -f sshm-hc >/dev/null 2>&1; docker run -d --name sshm-hc --network host -v /tmp/sshm-hc.yaml:/c.yaml:ro tobyxdd/hysteria:v2 client -c /c.yaml");
                    var code = ThroughSocks(lab, 10809);
                    Assert.True(code == "200", $"obfs={obfs}: HTTP {code}\n" + Sh(lab, "docker logs --tail 30 sshm-hc", check: false));
                }
                finally
                {
                    Sh(lab, "docker rm -f sshm-hc; rm -f /tmp/sshm-hc.yaml", check: false);
                }
            }
        });
    }

    [Fact]
    public async Task AmneziaWG_Keys_Carry_A_Working_Config_And_Names_Add_Or_Remove_Clients()
    {
        if (!Enabled("amneziawg")) return;
        await OnEachServer(async lab =>
        {
            var ip = Sh(lab, "hostname -I | awk '{print $1}'").Trim();
            // REGENERATE: starts from scratch even when an earlier run left clients behind
            var r = await Run(lab, "amneziawg", new() { ["AWG_PORT"] = "51820", ["AWG_CLIENTS"] = "phone, laptop", ["SERVER_ADDRESS"] = ip, ["REGENERATE"] = "1" });
            Assert.Equal("2", r["AWG_CLIENT_COUNT"]);
            var phone = Sh(lab, "cat /opt/amneziawg/clients/phone.conf");
            Assert.Contains($"Endpoint = {ip}:51820", phone);
            Assert.Matches(@"(?m)^Jc = \d+$", phone);
            Assert.Matches(@"(?m)^H4 = \d+$", phone);

            // the Amnezia VPN key holds exactly that config, in the JSON Amnezia VPN builds when it imports a .conf
            var key = r["AWG_KEY_phone"];
            Assert.StartsWith("vpn://", key);
            Assert.True(QrPayloads.CanShow(key));
            var json = AmneziaKey.Decode(key) ?? throw new InvalidOperationException("the key does not decode: " + key);
            Assert.Equal(phone.TrimEnd('\n'), AmneziaKey.WireGuardConfig(json)!.TrimEnd('\n'));
            Assert.Equal("amnezia-awg", (string?)json["defaultContainer"]);
            Assert.Equal($"{lab.Server.Name} — phone", (string?)json["description"]);
            Assert.Equal(ip, (string?)json["hostName"]);
            Assert.Equal("1.1.1.1", (string?)json["dns1"]);
            Assert.Equal("1.0.0.1", (string?)json["dns2"]);
            var awg = json["containers"]![0]!["awg"]!;
            Assert.Equal("51820", (string?)awg["port"]);
            var last = System.Text.Json.Nodes.JsonNode.Parse((string)awg["last_config"]!)!;
            Assert.Equal(51820, (int)last["port"]!);
            Assert.Equal(Regex.Match(phone, @"(?m)^Jc = (\d+)$").Groups[1].Value, (string?)last["Jc"]);
            Assert.StartsWith("10.66.66.", (string?)last["client_ip"]);
            Assert.NotEqual(r["AWG_KEY_phone"], r["AWG_KEY_laptop"]);

            // new name added, removed name dropped, the rest untouched
            var r2 = await Run(lab, "amneziawg", new() { ["AWG_PORT"] = "51820", ["AWG_CLIENTS"] = "phone,my-tablet", ["SERVER_ADDRESS"] = ip }, "-rerun");
            Assert.Equal("2", r2["AWG_CLIENT_COUNT"]);
            Assert.Equal(phone, Sh(lab, "cat /opt/amneziawg/clients/phone.conf"));
            Assert.Equal(key, r2["AWG_KEY_phone"]);
            Assert.True(r2.ContainsKey("AWG_KEY_my_tablet"));
            Assert.False(r2.ContainsKey("AWG_KEY_laptop"));
            Assert.Contains("gone", Sh(lab, "test -e /opt/amneziawg/clients/laptop.conf || echo gone"));
            Assert.DoesNotContain("# laptop", Sh(lab, "cat /opt/amneziawg/config/awg0.conf"));

            // a client in its own container, set up from the config taken out of the key: no resolvconf there and
            // /proc/sys is read-only (a default route needs a sysctl), so no DNS and only the tunnel subnet and 1.1.1.1
            var client = Regex.Replace(AmneziaKey.WireGuardConfig(r2["AWG_KEY_my_tablet"])!, @"(?m)^DNS = .*\n", "")
                .Replace("AllowedIPs = 0.0.0.0/0, ::/0", "AllowedIPs = 10.66.66.0/24, 1.1.1.1/32");
            Upload(lab, "/tmp/sshm-awg.conf", client);
            try
            {
                Pull(lab, "amneziavpn/amneziawg-go:latest");
                Sh(lab, "chmod 600 /tmp/sshm-awg.conf; docker rm -f sshm-awgc >/dev/null 2>&1; " +
                        "docker run -d --name sshm-awgc --cap-add NET_ADMIN --device /dev/net/tun " +
                        "-v /tmp/sshm-awg.conf:/etc/amnezia/amneziawg/awgc.conf:ro amneziavpn/amneziawg-go:latest " +
                        "sh -c 'awg-quick up awgc && sleep 600'");
                var ping = Sh(lab, "for i in $(seq 1 10); do docker exec sshm-awgc ping -c 1 -W 2 10.66.66.1 >/dev/null 2>&1 && { echo PING_OK; break; }; sleep 1; done; docker exec sshm-awgc awg show", check: false);
                Assert.True(ping.Contains("PING_OK"), ping + Sh(lab, "docker logs --tail 30 sshm-awgc", check: false));
                Assert.Contains("latest handshake", ping);
                // and the internet through the tunnel (NAT on the server)
                var web = Sh(lab, "for i in 1 2 3 4 5; do docker exec sshm-awgc wget -qO- -T 10 http://1.1.1.1/cdn-cgi/trace 2>&1 | grep -q ip= && { echo ip=ok; break; }; sleep 2; done", check: false);
                Assert.Contains("ip=", web);
            }
            finally
            {
                Sh(lab, "docker rm -f sshm-awgc; rm -f /tmp/sshm-awg.conf", check: false);
            }
        });
    }

    // ---------- scheduled jobs ----------

    [Fact]
    public async Task Cron_Jobs_Of_All_Users_Files_And_Timers_Are_Collected()
    {
        if (!Enabled("cron")) return;
        await OnEachServer(lab =>
        {
            // noninteractive: without it cron's recommended mail server asks questions on the console and hangs
            Sh(lab, "export DEBIAN_FRONTEND=noninteractive; command -v crontab >/dev/null || if command -v dnf >/dev/null; then dnf -y -q install cronie; " +
                    "else apt-get -o DPkg::Lock::Timeout=300 update -qq && apt-get install -y -qq --no-install-recommends cron; fi >/dev/null 2>&1; " +
                    "systemctl start cron 2>/dev/null || systemctl start crond 2>/dev/null; " +
                    "(crontab -l 2>/dev/null | grep -v sshm-lab; echo '*/7 * * * * /bin/echo sshm-lab-root  # sshm-lab') | crontab - && " +
                    "id lab >/dev/null 2>&1 && echo '@daily /bin/true sshm-lab-user' | crontab -u lab - ; " +
                    "printf 'SHELL=/bin/sh\\n15 4 * * 1 nobody /bin/echo sshm-lab-file\\n' > /etc/cron.d/sshm-lab");
            try
            {
                var jobs = CronCollector.Collect(lab.Ssh, lab.Server);
                var root = Assert.Single(jobs, j => j.Command.Contains("sshm-lab-root"));
                Assert.Equal((CronKind.Crontab, "root", "*/7 * * * *"), (root.Kind, root.User, root.Schedule));
                if (Sh(lab, "id lab >/dev/null 2>&1 && echo yes", check: false).Contains("yes"))
                    Assert.Equal("lab", Assert.Single(jobs, j => j.Command.Contains("sshm-lab-user")).User);
                var file = Assert.Single(jobs, j => j.Source == "/etc/cron.d/sshm-lab");
                Assert.Equal(("nobody", "15 4 * * 1"), (file.User, file.Schedule));
                Assert.Contains(jobs, j => j.Kind == CronKind.Timer && j.Command.EndsWith(".service"));
                log.WriteLine($"{lab.Server.Name}: {jobs.Count} jobs\n" + string.Join("\n", CronCollector.Format(jobs)));
            }
            finally
            {
                Sh(lab, "crontab -l 2>/dev/null | grep -v sshm-lab | crontab - ; crontab -r -u lab 2>/dev/null; rm -f /etc/cron.d/sshm-lab", check: false);
            }
            return Task.CompletedTask;
        });
    }

    // ---------- reboot ----------

    /// <summary>Restarts the lab servers (heavy: other checks on them are interrupted). The lab restarts stopped containers.</summary>
    [Fact]
    public async Task Reboot_Goes_Through_And_The_Server_Comes_Back()
    {
        if (!Enabled("reboot", heavy: true)) return;
        await OnEachServer(async lab =>
        {
            var uptimeBefore = double.Parse(Sh(lab, "cut -d' ' -f1 /proc/uptime").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var bootId = ServerPower.Reboot(lab.Ssh, lab.Server, interactive: false);
            Assert.Matches("^[0-9a-f-]{36} [0-9]+$", bootId); // boot id + start time of PID 1
            var back = await ServerPower.WaitBackAsync(lab.Ssh, lab.Server, bootId, TimeSpan.FromMinutes(4), pollEvery: TimeSpan.FromSeconds(3));
            Assert.True(back != null, "the server did not come back");
            log.WriteLine($"{lab.Server.Name}: back after {back!.Value.TotalSeconds:0} s");
            var uptimeAfter = double.Parse(Sh(lab, "cut -d' ' -f1 /proc/uptime").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            // a container's /proc/uptime is the host's: only a real VM shows the restart there
            log.WriteLine($"uptime before {uptimeBefore:0} s, after {uptimeAfter:0} s; systemd: " + Sh(lab, "systemctl is-system-running", check: false).Trim());
        });
    }

    // ---------- web ----------

    [Fact]
    public async Task Static_Site_Nginx_Serves_The_Page_Through_Sudo()
    {
        if (!Enabled("static-site-nginx")) return;
        await OnEachServer(async lab =>
        {
            // through the sudo user when the lab has one (password fed to sudo on stdin)
            var user = new ServerEntry { Name = lab.Server.Name + "-sudo", Host = lab.Server.Host, Port = lab.Server.Port, Username = "lab", Password = "lab-user" };
            var sudoLab = lab with { Server = user };
            var hasUser = Sh(lab, "id lab >/dev/null 2>&1 && echo yes", check: false).Contains("yes");
            var r = await Run(hasUser ? sudoLab : lab, "static-site-nginx", new() { ["SITE_PORT"] = "80", ["SITE_TITLE"] = "Lab <nginx>", ["SITE_OVERWRITE"] = "1" });
            Assert.StartsWith("http://", r["SITE_URL"]);
            Assert.Equal("/var/www/sshm-site", r["SITE_ROOT"]);
            Assert.Contains("Lab &lt;nginx&gt;", Sh(lab, "curl -s http://127.0.0.1/"));
            // a second run keeps the page the user uploaded
            Sh(lab, "echo mine > /var/www/sshm-site/index.html");
            await Run(lab, "static-site-nginx", new() { ["SITE_PORT"] = "80" }, "-rerun");
            Assert.Equal("mine", Sh(lab, "curl -s http://127.0.0.1/").Trim());
        });
    }

    [Fact]
    public async Task Static_Site_Docker_Serves_The_Page()
    {
        if (!Enabled("static-site-docker")) return;
        await OnEachServer(async lab =>
        {
            var r = await Run(lab, "static-site-docker", new() { ["SITE_PORT"] = "8080", ["SITE_TITLE"] = "Lab caddy", ["SITE_OVERWRITE"] = "1" });
            Assert.EndsWith(":8080", r["SITE_URL"]);
            Assert.Contains("Lab caddy", Sh(lab, "curl -s http://127.0.0.1:8080/"));
        });
    }

    // ---------- cloud ----------

    [Fact]
    public async Task FileBrowser_Login_And_Password_Change()
    {
        if (!Enabled("filebrowser")) return;
        await OnEachServer(async lab =>
        {
            Sh(lab, "cd /opt/filebrowser 2>/dev/null && docker compose down >/dev/null 2>&1; rm -rf /opt/filebrowser", check: false);
            var r = await Run(lab, "filebrowser", new() { ["FB_PORT"] = "8081" });
            var password = r["FB_ADMIN_PASSWORD"];
            string Login(string pw) => Sh(lab, "curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' " +
                                                $"-d '{{\"username\":\"admin\",\"password\":\"{pw}\"}}' http://127.0.0.1:8081/api/login", check: false).Trim();
            Assert.Equal("200", Login(password));

            var r2 = await Run(lab, "filebrowser", new() { ["FB_PORT"] = "8081", ["FB_ADMIN_PASSWORD"] = "lab-password-2026" }, "-rerun");
            Assert.False(r2.ContainsKey("FB_ADMIN_PASSWORD"));
            Assert.Equal("200", Login("lab-password-2026"));
            Assert.NotEqual("200", Login(password));
        });
    }

    [Fact]
    public async Task Nextcloud_Installs_And_Admin_Logs_In()
    {
        if (!Enabled("nextcloud", heavy: true)) return;
        await OnEachServer(async lab =>
        {
            var r = await Run(lab, "nextcloud", new() { ["NC_PORT"] = "8090", ["ADD_SWAP"] = "0" });
            Assert.Contains("\"installed\":true", Sh(lab, "curl -s http://127.0.0.1:8090/status.php"));
            var password = r.GetValueOrDefault("NC_ADMIN_PASSWORD") ?? throw new InvalidOperationException("already installed: remove /opt/nextcloud first");
            Assert.Equal("200", Sh(lab, $"curl -s -o /dev/null -w '%{{http_code}}' -u 'admin:{password}' -H 'OCS-APIRequest: true' " +
                                         "http://127.0.0.1:8090/ocs/v2.php/cloud/user").Trim());
            Assert.DoesNotContain(password, Sh(lab, "cat /opt/nextcloud/.env"));
        });
    }

    [Fact]
    public async Task Seafile_Installs_And_Admin_Gets_A_Token()
    {
        if (!Enabled("seafile", heavy: true)) return;
        await OnEachServer(async lab =>
        {
            var r = await Run(lab, "seafile", new() { ["SF_PORT"] = "8000", ["SF_ADMIN_EMAIL"] = "admin@lab.test", ["ADD_SWAP"] = "0" });
            var password = r.GetValueOrDefault("SF_ADMIN_PASSWORD") ?? throw new InvalidOperationException("already installed: remove /opt/seafile first");
            var token = Sh(lab, $"curl -s -d 'username=admin@lab.test' -d 'password={password}' http://127.0.0.1:8000/api2/auth-token/");
            Assert.Contains("\"token\"", token);
            Assert.DoesNotContain(password, Sh(lab, "cat /opt/seafile/.env"));
        });
    }
}
