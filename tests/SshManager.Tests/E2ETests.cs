using System.Runtime.CompilerServices;
using SshManager.Core;
using SshManager.Core.Agent;
using SshManager.Core.Models;
using SshManager.Core.Pipes;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Tests;

internal static class TestSetup
{
    /// <summary>Keep tests away from the real data folder.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("SSHMANAGER_DATA", Directory.CreateTempSubdirectory("sshm-data-").FullName);
}

/// <summary>
/// End-to-end against a real sshd. Enable with SSHM_E2E=host:port:user:password (e.g. a throwaway container).
/// </summary>
public class E2ETests
{
    private static (string Host, int Port, string User, string Password)? Target()
    {
        var v = Environment.GetEnvironmentVariable("SSHM_E2E");
        if (string.IsNullOrEmpty(v)) return null;
        var p = v.Split(':', 4);
        return (p[0], int.Parse(p[1]), p[2], p[3]);
    }

    /// <summary>Inventory, metrics, iptables forwards and SFTP backup. The target must allow iptables (root, NET_ADMIN).</summary>
    [Fact]
    public async Task Inventory_Metrics_Forwards_And_Backup()
    {
        if (Target() is not { } t) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var server = new ServerEntry { Name = "e2e", Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password };
        vault.Update(d => d.Servers.Add(server));
        var known = new KnownHostsService(tmp.File("known_hosts"));
        var ssh = new SshClientFactory(vault, known) { ConfirmHostKey = _ => true };
        using (ssh.Connect(server)) { } // trust the host key once, background jobs never prompt

        await new Core.Inventory.ServerInventoryService(vault, ssh).RefreshAsync(server.Id);
        var facts = vault.Data.Servers.Single().Facts!;
        Assert.Null(facts.InventoryError);
        Assert.NotNull(facts.OsId);
        Assert.NotNull(facts.Kernel);
        Assert.Contains(facts.ListeningPorts, p => p.Port == 22 && !p.LocalOnly);

        var metrics = new Core.Monitoring.MetricsCollector(ssh);
        await metrics.CollectAsync(server);
        var m = metrics.Get(server.Id)!;
        Assert.Null(m.Error);
        Assert.NotNull(m.CpuPercent);
        Assert.True(m.MemTotalKb > 0);

        var health = await Core.Monitoring.HealthMonitor.ProbeAsync(t.Host, t.Port, TimeSpan.FromSeconds(5));
        Assert.Equal(Core.Monitoring.HealthState.Online, health.State);
        Assert.Null(health.Error);
        var closed = await Core.Monitoring.HealthMonitor.ProbeAsync(t.Host, 1, TimeSpan.FromSeconds(3), expectSsh: false);
        Assert.Equal(Core.Monitoring.HealthState.Offline, closed.State);

        var fwd = new Core.Forwarding.PortForwardService(vault, ssh);
        var logs = new List<string>();
        var added = fwd.Add(server, "tcp", "18443", "203.0.113.9", "443", logs.Add);
        var f = Assert.Single(added.Forwards, x => x.ListenPort == "18443");
        Assert.True(f.Managed);
        Assert.Equal("203.0.113.9", f.TargetIp);
        var removed = fwd.Remove(server, f, logs.Add);
        Assert.DoesNotContain(removed.Forwards, x => x.ListenPort == "18443");

        var settings = new SettingsService();
        settings.Settings.Backup = new BackupSettings { Target = BackupTarget.Ssh, ServerId = server.Id, RemotePath = "sshm-e2e-backups", Keep = 1 };
        var where = await new Core.Backup.BackupService(vault, settings, ssh, tmp.Path).RunAsync(interactive: false);
        Assert.Contains("sshm-e2e-backups/sshmanager-data-", where);
        using var client = ssh.Connect(server);
        Assert.Contains("sshmanager-data-", client.RunCommand("ls sshm-e2e-backups").Result);
        client.RunCommand("rm -rf sshm-e2e-backups");

        // install script: uploaded to /tmp, run with the env vars, removed afterwards
        var runner = new Core.Scripts.ScriptRunner(ssh);
        var command = runner.Prepare(server, new ScriptEntry { Name = "t", Body = "echo \"HELLO $SSHM_SERVER_NAME\"; echo $0" });
        var run = client.RunCommand(command);
        Assert.Contains("HELLO e2e", run.Result);
        var file = run.Result.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("/tmp/sshm-"));
        Assert.Contains("gone", client.RunCommand($"test -e {file} || echo gone").Result);
    }

    /// <summary>
    /// In-app script run: parameters as env vars, output streamed, results collected, temp files removed.
    /// With SSHM_E2E_SUDO=user:password also runs as a sudo user (password fed on stdin).
    /// </summary>
    [Fact]
    public async Task Script_Runs_In_App_With_Params_And_Results()
    {
        if (Target() is not { } t) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var root = new ServerEntry { Name = "e2e", Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password };
        vault.Update(d => d.Servers.Add(root));
        var ssh = new SshClientFactory(vault, new KnownHostsService(tmp.File("known_hosts"))) { ConfirmHostKey = _ => true };
        var runner = new Core.Scripts.ScriptRunner(ssh);
        var script = new ScriptEntry
        {
            Name = "demo",
            UseSudo = true,
            Body = """
                #!/usr/bin/env bash
                # @param GREETING text default=hi
                set -euo pipefail
                echo "start $GREETING on $SSHM_SERVER_NAME as $(id -un)"
                for i in 1 2 3; do echo "step $i"; sleep 0.3; done
                echo "to stderr" >&2
                printf 'URL=vless://id@host:443?x=1#%s\n' "$SSHM_SERVER_NAME" >> "$SSHM_RESULT"
                echo "WHO=$(id -un)" >> "$SSHM_RESULT"
                echo "SELF=$0" >> "$SSHM_RESULT"
                exit 3
                """,
        };

        async Task<Core.Scripts.ScriptRunResult> Run(ServerEntry s, Dictionary<string, string> values, List<string> chunks) =>
            await runner.RunAsync(s, script, values, c => { lock (chunks) chunks.Add(c); }, CancellationToken.None);

        var chunks = new List<string>();
        var r = await Run(root, new() { ["GREETING"] = "it's me" }, chunks);
        var shown = string.Concat(chunks);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("start it's me on e2e as root", shown);
        Assert.Contains("step 3", shown);
        Assert.Contains("to stderr", shown);
        Assert.DoesNotContain(Core.Scripts.ScriptRunner.ResultMarker, shown);
        Assert.DoesNotContain("URL=", shown);
        Assert.Equal("vless://id@host:443?x=1#e2e", r.Results["URL"]);
        Assert.Equal("root", r.Results["WHO"]);
        using (var client = ssh.Connect(root))
        {
            var self = r.Results["SELF"];
            var stem = self[..^3];
            Assert.Contains("gone", client.RunCommand($"test -e {self} -o -e {stem}.env -o -e {stem}.result || echo gone").Result);
        }

        // cancelling stops the run
        var slow = script.Clone();
        slow.Body = "echo begin; sleep 30; echo never";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var cancelled = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(root, slow, new Dictionary<string, string>(), c => { lock (cancelled) cancelled.Add(c); }, cts.Token));
        Assert.Contains("begin", string.Concat(cancelled));

        // compose script against a stub "docker" (only on targets without a real one)
        using (var c = ssh.Connect(root))
        {
            if (c.RunCommand("command -v docker").ExitStatus != 0)
            {
                c.RunCommand("printf '#!/bin/sh\\necho \"docker $*\" >> /tmp/sshm-fake-docker.log\\n' > /usr/local/bin/docker && chmod +x /usr/local/bin/docker");
                try
                {
                    var compose = new ScriptEntry
                    {
                        Name = "Demo compose",
                        Kind = ScriptKind.Compose,
                        Body = "# @project sshm-e2e-demo\n# @param APP_PORT number default=8080\n# @param SECRET_X secret\n" +
                               "services:\n  app:\n    image: nginx:alpine\n    ports: [\"${APP_PORT}:80\"]\n",
                    };
                    var cr = await runner.RunAsync(root, compose, new Dictionary<string, string> { ["APP_PORT"] = "9090", ["SECRET_X"] = "it's $ecret" },
                        _ => { }, CancellationToken.None);
                    Assert.True(cr.ExitCode == 0, cr.Output);
                    Assert.Contains("image: nginx:alpine", c.RunCommand("cat /opt/sshm-e2e-demo/docker-compose.yml").Result);
                    var env = c.RunCommand("cat /opt/sshm-e2e-demo/.env; stat -c %a /opt/sshm-e2e-demo/.env").Result;
                    Assert.Contains("APP_PORT='9090'", env);
                    Assert.Contains("SECRET_X=\"it's $$ecret\"", env);
                    Assert.EndsWith("600", env.Trim());
                    var calls = c.RunCommand("cat /tmp/sshm-fake-docker.log").Result;
                    Assert.Contains("docker compose pull", calls);
                    Assert.Contains("docker compose up -d --remove-orphans", calls);
                }
                finally
                {
                    c.RunCommand("rm -rf /usr/local/bin/docker /tmp/sshm-fake-docker.log /opt/sshm-e2e-demo");
                }
            }
        }

        if (Environment.GetEnvironmentVariable("SSHM_E2E_SUDO") is { Length: > 0 } sudo)
        {
            var p = sudo.Split(':', 2);
            var user = new ServerEntry { Name = "e2e-sudo", Host = t.Host, Port = t.Port, Username = p[0], Password = p[1] };
            vault.Update(d => d.Servers.Add(user));
            var userChunks = new List<string>();
            var ur = await Run(user, new() { ["GREETING"] = "x" }, userChunks);
            Assert.Equal(3, ur.ExitCode);
            Assert.Equal("root", ur.Results["WHO"]);
            Assert.DoesNotContain(p[1], string.Concat(userChunks));
        }
    }

    [Fact]
    public async Task Password_Then_KeySetup_Then_Agent_And_AskPass_Sessions()
    {
        if (Target() is not { } t) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var server = new ServerEntry { Name = "e2e", Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password };
        vault.Update(d => d.Servers.Add(server));

        var known = new KnownHostsService(tmp.File("known_hosts"));
        var prompts = 0;
        var ssh = new SshClientFactory(vault, known) { ConfirmHostKey = _ => { prompts++; return true; } };
        var setup = new KeySetupService(vault, ssh);

        // 1. password login via SSH.NET, host key gets stored
        Assert.Contains("Linux", setup.Test(server));
        Assert.Equal(1, prompts);

        // 2. password -> key
        var logs = new List<string>();
        var key = setup.SetupKeyAuth(server, null, logs.Add);
        var updated = vault.Data.Servers.Single();
        Assert.Equal(AuthMode.Key, updated.Auth);
        Assert.Equal(key.Id, updated.KeyId);
        Assert.Contains("Linux", setup.Test(updated)); // SSH.NET key login
        Assert.Equal(1, prompts); // known host now

        // running setup again must not duplicate the authorized_keys line
        setup.SetupKeyAuth(updated, key.Id, logs.Add);

        // 3. real ssh.exe through our agent, using our known_hosts (BatchMode: no prompts allowed)
        var agentPipe = "sshm-e2e-" + Guid.NewGuid().ToString("N");
        using var agent = new SshAgentServer(vault);
        Assert.True(agent.StartOn(agentPipe));
        var settings = new SettingsService();
        var launcher = new SessionLauncher(vault, settings, agent, known);

        var spec = launcher.BuildSpec(vault.Data.Servers.Single(), pauseOnError: false);
        var r = OpenSsh.Run("ssh", [.. spec.Args.Take(spec.Args.Count - 1), "-o", "BatchMode=yes", spec.Args[^1],
            "echo KEY_OK; grep -c ssh-ed25519 ~/.ssh/authorized_keys"], spec.Env);
        Assert.True(r.Code == 0, r.Err);
        Assert.Contains("KEY_OK", r.Out);
        Assert.EndsWith("1", r.Out.Trim());

        // 4. password session: ssh asks sshm.exe (SSH_ASKPASS), which asks us over the control pipe
        var pwServer = new ServerEntry { Name = "e2e-pw", Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password };
        vault.Update(d => d.Servers.Add(pwServer));
        using var control = new ControlServer(req => Task.FromResult(req.Op == "askpass" && launcher.AnswerAskPass(req.Token!) is { } pw
            ? new ControlResponse { Ok = true, Value = pw }
            : ControlResponse.Fail("no")));
        Assert.True(control.Start(), "control pipe busy — is SSH Manager running?");
        Assert.True(File.Exists(AppPaths.HelperExe), AppPaths.HelperExe);

        var pwSpec = launcher.BuildSpec(pwServer, pauseOnError: false);
        var r2 = await Task.Run(() => OpenSsh.Run("ssh", [.. pwSpec.Args, "echo PW_OK"], pwSpec.Env));
        Assert.True(r2.Code == 0, r2.Err);
        Assert.Contains("PW_OK", r2.Out);
    }

    /// <summary>Built-in terminal: the shell integration loads unseen, reports cwd and "edit", suggestions list real files.
    /// SSHM_E2E_ZSH=user:password also checks a zsh login.</summary>
    [Fact]
    public async Task Terminal_Shell_Integration_And_Suggestions()
    {
        if (Target() is not { } t) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var known = new KnownHostsService(tmp.File("known_hosts"));
        var ssh = new SshClientFactory(vault, known) { ConfirmHostKey = _ => true };
        var users = new List<(string User, string Password)> { (t.User, t.Password) };
        if (Environment.GetEnvironmentVariable("SSHM_E2E_ZSH") is { Length: > 0 } z && z.Split(':', 2) is [var zu, var zp]) users.Add((zu, zp));

        foreach (var (user, password) in users)
        {
            var server = new ServerEntry { Name = "e2e-" + user, Host = t.Host, Port = t.Port, Username = user, Password = password };
            var output = new System.Text.StringBuilder();
            using var session = new TerminalSession(ssh, server);
            session.Output += s => { lock (output) output.Append(s); };
            string Out() { lock (output) return output.ToString(); }
            async Task WaitFor(string text)
            {
                for (var i = 0; i < 100 && !Out().Contains(text); i++) await Task.Delay(100);
                Assert.True(Out().Contains(text), $"no '{text}' in: {Out()}");
            }

            await Task.Run(() => session.Open(100, 30));
            await WaitFor("\u001b]7337;B\u0007");
            Assert.True(session.Integrated, Out());
            Assert.DoesNotContain("sshm/shell-", Out()); // the source line is not shown

            session.Send("cd /etc\n");
            await WaitFor("\u001b]7337;P;/etc\u0007");
            session.Send("edit hosts\n");
            await WaitFor("\u001b]7337;E;/etc/hosts\u0007");
            session.Send("false\n");
            await WaitFor("\u001b]7337;D;1\u0007");

            var assist = new Core.Terminal.TerminalAssist(server, session.Run) { Cwd = "/etc" };
            assist.Start();
            var ls = session.Run("ls -1ApL -- /etc", TimeSpan.FromSeconds(5));
            Assert.True(ls.Output.Contains("hosts"), ls.Combined);
            var r = await assist.CompleteAsync("cat ho", 6, false, default);
            Assert.Contains(r.Items, i => i.Label == "hosts" && i.Insert == "sts ");
            r = await assist.CompleteAsync("ls /usr/sh", 10, false, default);
            Assert.Contains(r.Items, i => i.Label == "share/");
            for (var i = 0; i < 50 && assist.Home == null; i++) await Task.Delay(100);
            r = await assist.CompleteAsync("whoam", 5, false, default);
            Assert.Contains(r.Items, i => i.Label == "whoami");
        }
    }

    /// <summary>File manager and editor over SFTP: listing, folders, a transfer with a conflict, a CRLF file saved as
    /// it was, and (SSHM_E2E_SUDO=user:password, a sudo-capable user) saving a root-owned file through sudo.</summary>
    [Fact]
    public void Files_Transfers_And_Editor()
    {
        if (Target() is not { } t) return;
        using var tmp = new TempDir();
        var vault = new VaultService(tmp.File("vault.dat"));
        vault.Create("password123");
        var known = new KnownHostsService(tmp.File("known_hosts"));
        var ssh = new SshClientFactory(vault, known) { ConfirmHostKey = _ => true };
        var server = new ServerEntry { Name = "e2e", Host = t.Host, Port = t.Port, Username = t.User, Password = t.Password };

        using var remote = new Core.Files.SftpFileSystem(ssh.ConnectSftp(server));
        var root = remote.Combine(remote.Home, "sshm-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        remote.CreateDirectory(root);
        try
        {
            // local tree → server
            var local = new Core.Files.LocalFileSystem();
            var src = Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
            File.WriteAllText(Path.Combine(src.FullName, "a.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(src.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(src.FullName, "sub", "big.bin"), new byte[3 * 1024 * 1024 + 17]);
            var job = new Core.Files.TransferJob("up");
            Core.Files.Transfers.Run(job, local, [local.Stat(src.FullName)!], remote, root, false, (_, _) => Core.Files.ConflictChoice.Cancel);
            Assert.Equal(Core.Files.TransferState.Done, job.State);
            Assert.Equal(2, job.DoneFiles);
            Assert.Equal(3 * 1024 * 1024 + 22, job.DoneBytes);
            var listed = remote.List(remote.Combine(root, "src"));
            Assert.Contains(listed, f => f.Name == "sub" && f.IsDirectory);
            Assert.Contains(listed, f => f.Name == "a.txt" && f.Size == 5 && f.Owner == t.User);

            // again: skip everything
            job = new Core.Files.TransferJob("up2");
            Core.Files.Transfers.Run(job, local, [local.Stat(src.FullName)!], remote, root, false, (_, _) => Core.Files.ConflictChoice.SkipAll);
            Assert.Equal(2, job.Skipped);

            // server → local, rename, delete
            var down = Directory.CreateDirectory(Path.Combine(tmp.Path, "down"));
            job = new Core.Files.TransferJob("down");
            Core.Files.Transfers.Run(job, remote, [remote.Stat(remote.Combine(root, "src"))!], local, down.FullName, false, (_, _) => Core.Files.ConflictChoice.Cancel);
            Assert.Equal(Core.Files.TransferState.Done, job.State);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(down.FullName, "src", "a.txt")));
            Assert.Equal(3 * 1024 * 1024 + 17, new FileInfo(Path.Combine(down.FullName, "src", "sub", "big.bin")).Length);
            remote.Rename(remote.Combine(root, "src/a.txt"), remote.Combine(root, "src/b.txt"));
            Assert.NotNull(remote.Stat(remote.Combine(root, "src/b.txt")));

            // editor: CRLF and a missing final newline stay as they were
            var path = remote.Combine(root, "crlf.conf");
            using (var w = remote.Create(path)) w.Write("a=1\r\nb=2"u8);
            using (var file = new Core.Files.RemoteTextFile(ssh, server, path))
            {
                Assert.Equal("a=1\nb=2", file.Load());
                Assert.True(file.Format.Crlf);
                Assert.False(file.ChangedOnServer());
                file.Save("a=1\nb=3\nc=4");
            }
            using (var r = remote.OpenRead(path))
            using (var reader = new StreamReader(r)) Assert.Equal("a=1\r\nb=3\r\nc=4", reader.ReadToEnd());

            remote.Delete(remote.Stat(remote.Combine(root, "src"))!);
            Assert.Null(remote.Stat(remote.Combine(root, "src")));
        }
        finally
        {
            remote.Delete(remote.Stat(root)!);
        }

        if (Environment.GetEnvironmentVariable("SSHM_E2E_SUDO") is { Length: > 0 } su && su.Split(':', 2) is [var user, var password])
        {
            // /etc/sshm-test.conf: root-owned 644 with CRLF lines, prepared in the container
            var bob = new ServerEntry { Name = "e2e-sudo", Host = t.Host, Port = t.Port, Username = user, Password = password };
            using var file = new Core.Files.RemoteTextFile(ssh, bob, "/etc/sshm-test.conf");
            Assert.Equal("line1\nline2\n", file.Load());
            Assert.False(file.UsesSudo);
            Assert.Throws<UnauthorizedAccessException>(() => file.Save("line1\nchanged\n"));
            file.Save("line1\nchanged\n", sudo: true);
            Assert.True(file.UsesSudo);
            using var check = new Core.Files.SftpFileSystem(ssh.ConnectSftp(bob));
            var st = check.Stat("/etc/sshm-test.conf")!;
            Assert.Equal("root", st.Owner);
            Assert.Equal("rw-r--r--", st.Permissions);
            using var r = check.OpenRead("/etc/sshm-test.conf");
            using var reader = new StreamReader(r);
            Assert.Equal("line1\r\nchanged\r\n", reader.ReadToEnd());
        }
    }
}
