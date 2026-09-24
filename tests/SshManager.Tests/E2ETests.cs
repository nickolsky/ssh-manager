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
        var setup = new KeySetupService(vault, known) { ConfirmHostKey = _ => { prompts++; return true; } };

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
}
