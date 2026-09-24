using SshManager.Core.Terminal;

namespace SshManager.Tests;

public class ShellWordsTests
{
    [Fact]
    public void Splits_The_Current_Command()
    {
        var words = ShellWords.Parse("cat x | docker logs -f ngi", out var cur);
        Assert.Equal(["docker", "logs", "-f"], words.Select(w => w.Value));
        Assert.Equal("ngi", cur!.Value);

        words = ShellWords.Parse("ls 'My Doc", out cur);
        Assert.Equal("My Doc", cur!.Value);
        Assert.Equal('\'', cur.Quote);
        Assert.Equal("'My Doc", cur.Raw);

        words = ShellWords.Parse(@"cd My\ Do", out cur);
        Assert.Equal("My Do", cur!.Value);
        Assert.Equal(@"My\ Do", cur.Raw);

        ShellWords.Parse("docker ", out cur);
        Assert.Equal("", cur!.Value);

        // redirection target, 2>&1 is not a command separator
        words = ShellWords.Parse("make 2>&1 > /var/lo", out cur);
        Assert.Equal(["make"], words.Select(w => w.Value));
        Assert.True(cur!.Redirect);
        Assert.Equal("/var/lo", cur.Value);

        ShellWords.Parse("echo hi # com", out cur);
        Assert.Null(cur);
    }
}

public class CompletionEngineTests
{
    private static readonly Dictionary<string, List<string>> Fs = new()
    {
        ["/root"] = ["docker-compose.yml", "My Files/", ".bashrc", "notes.txt"],
        ["/etc"] = ["nginx/", "nanorc", "hosts"],
        ["/etc/nginx"] = ["nginx.conf", "sites-enabled/"],
    };

    private static CompletionSource Source(params string[] history) => new()
    {
        History = history,
        Commands = ["docker", "docker-compose", "dockerd", "ls", "lsblk", "systemctl", "journalctl", "nano"],
        Containers = [new Candidate("nginx-proxy", "running"), new Candidate("uptime-kuma", "running")],
        Services = [new Candidate("nginx"), new Candidate("ssh"), new Candidate("xray")],
        Cwd = "/root",
        Home = "/root",
        ListDirectory = (dir, _) => Task.FromResult<IReadOnlyList<string>?>(Fs.TryGetValue(dir, out var l) ? l : null),
    };

    private static async Task<CompletionResult> Complete(string line, CompletionSource? src = null, bool explicitRequest = false) =>
        await CompletionEngine.CompleteAsync(line, line.Length, src ?? Source(), explicitRequest);

    [Fact]
    public async Task Commands_Subcommands_And_Flags()
    {
        var r = await Complete("dock");
        Assert.Equal("docker", r.Items[0].Label);
        Assert.Equal("er ", r.Items[0].Insert);
        Assert.Equal(0, r.Items[0].Delete);

        r = await Complete("docker lo");
        Assert.Contains(r.Items, i => i.Label == "logs" && i.Kind == "sub" && i.Insert == "gs ");

        r = await Complete("docker logs --t");
        Assert.Contains(r.Items, i => i.Label == "-n, --tail" && i.Insert == "ail ");

        // after "docker " the subcommands come without typing
        r = await Complete("docker ");
        Assert.Contains(r.Items, i => i.Label == "ps");
        // docker compose = docker-compose spec
        r = await Complete("docker compose up --remove-o");
        Assert.Contains(r.Items, i => i.Label == "--remove-orphans");
    }

    [Fact]
    public async Task Containers_Services_And_Flag_Values()
    {
        var r = await Complete("docker logs -f upt");
        Assert.Equal("uptime-kuma", Assert.Single(r.Items).Label);
        Assert.Equal("ime-kuma ", r.Items[0].Insert);

        r = await Complete("sudo systemctl restart ng");
        Assert.Equal("nginx", Assert.Single(r.Items).Label);

        r = await Complete("journalctl -u x");
        Assert.Equal("xray", Assert.Single(r.Items).Label);

        r = await Complete("journalctl --unit=ss");
        Assert.Equal("h ", Assert.Single(r.Items).Insert);
    }

    [Fact]
    public async Task Files_Dirs_And_Escaping()
    {
        var r = await Complete("nano /etc/ng");
        var it = Assert.Single(r.Items);
        Assert.Equal("nginx/", it.Label);
        Assert.Equal("inx/", it.Insert); // a directory: no space, continue typing

        r = await Complete("cat /etc/nginx/");
        Assert.Contains(r.Items, i => i.Label == "nginx.conf" && i.Insert == "nginx.conf ");

        r = await Complete("cd My");
        it = Assert.Single(r.Items);
        Assert.Equal(@"\ Files/", it.Insert);

        r = await Complete("cat 'My");
        Assert.Equal(" Files/", Assert.Single(r.Items).Insert);

        // hidden files only for a leading dot, cd lists directories only
        r = await Complete("cat .b");
        Assert.Contains(r.Items, i => i.Label == ".bashrc");
        r = await Complete("cd ", explicitRequest: true);
        Assert.Equal(["My Files/"], r.Items.Select(i => i.Label));

        r = await Complete("cat ~/no");
        Assert.Equal("tes.txt ", Assert.Single(r.Items).Insert);

        // relative to the shell's directory, .. resolved
        r = await Complete("ls ../etc/ho");
        Assert.Equal("sts ", Assert.Single(r.Items).Insert);
    }

    [Fact]
    public async Task History_Ghost_And_Items()
    {
        var src = Source("docker compose logs -f", "docker ps -a", "ls -la");
        var r = await Complete("docker c", src);
        Assert.Equal("ompose logs -f", r.Ghost);
        Assert.Equal("docker compose logs -f", r.Items[0].Label);
        Assert.Equal("history", r.Items[0].Kind);

        // Ctrl+Space on an empty line: recent commands
        r = await Complete("", src, explicitRequest: true);
        Assert.Equal(3, r.Items.Count);
        Assert.Null(r.Ghost);

        // no ghost when the cursor is not at the end
        r = await CompletionEngine.CompleteAsync("docker c x", 8, src, false);
        Assert.Null(r.Ghost);
    }

    [Fact]
    public async Task Nothing_For_An_Exact_Word()
    {
        var src = Source();
        var r = await Complete("nano", src with { Commands = ["nano"] });
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Resolves_Paths()
    {
        var src = Source();
        Assert.Equal("/root/a", CompletionEngine.Resolve("a/", src));
        Assert.Equal("/etc", CompletionEngine.Resolve("/etc/nginx/../", src));
        Assert.Equal("/root/x", CompletionEngine.Resolve("~/x/", src));
        Assert.Null(CompletionEngine.Resolve("~bob/", src));
        Assert.Null(CompletionEngine.Resolve("$HOME/", src));
    }

    [Fact]
    public void Specs_Load()
    {
        var docker = CommandSpecs.Get("docker")!;
        Assert.NotNull(docker.Subs!["compose"].Subs!["up"]);
        Assert.Equal("service", CommandSpecs.Get("journalctl")!.FindFlag("-u")!.Arg);
        Assert.Equal("none", CommandSpecs.Get("docker")!.Subs!["logs"].FindFlag("--tail")!.Arg);
        Assert.NotNull(CommandSpecs.Get("apt-get")!.Subs!["install"]);
        var f = CommandSpecs.ParseFlag("-u,--unit=service|Unit");
        Assert.Equal(["-u", "--unit"], f.Names);
        Assert.Equal("service", f.Arg);
        Assert.Equal("Unit", f.Description);
    }
}

public class ShellIntegrationTests
{
    [Fact]
    public void Prompt_Detection()
    {
        Assert.True(ShellIntegration.LooksLikePrompt("Welcome\r\n\u001b[?2004h\u001b[01;32mroot@vps\u001b[00m:~# "));
        Assert.True(ShellIntegration.LooksLikePrompt("bob@host:~$ "));
        Assert.True(ShellIntegration.LooksLikePrompt("❯ "));
        Assert.False(ShellIntegration.LooksLikePrompt("You are required to change your password.\r\nCurrent password: "));
        Assert.False(ShellIntegration.LooksLikePrompt("Last login: today\r\n"));
        Assert.True(ShellIntegration.LooksLikePrompt("[root@centos ~]# "));
        Assert.True(ShellIntegration.LooksLikePrompt("➜  ~ "));
        Assert.True(ShellIntegration.LooksLikePrompt("\u001b[0m\u001b[27m\u001b[24m\u001b[Jzed@host ~ % \u001b[K\u001b[?1h\u001b=\u001b[?2004h"));
        Assert.False(ShellIntegration.LooksLikePrompt("(q)  Quit and do nothing.\r\n\r\n--- Type one of the keys in parentheses --- "));
        Assert.False(ShellIntegration.LooksLikePrompt(""));
    }

    [Fact]
    public void Splice_Drops_The_First_Prompt_And_The_Source_Line()
    {
        var before = "Welcome to Ubuntu\r\n\r\nroot@vps:~# ";
        var after = " . '/root/.cache/sshm/shell-1.sh'\r\n" + ShellIntegration.LoadedMark + "\u001b]7337;A\u0007root@vps:~# ";
        var shown = ShellIntegration.Splice(before, after);
        Assert.Equal("Welcome to Ubuntu\r\n\r\n\u001b]7337;A\u0007root@vps:~# ", shown);
        Assert.DoesNotContain("sshm", shown);
    }

    [Fact]
    public void Setup_Script_Is_Plain_Sh()
    {
        var cmd = ShellIntegration.SetupCommand();
        Assert.Contains("__SSHM_EOF__\n", cmd);
        Assert.Contains("shell-" + ShellIntegration.Hash + ".sh", cmd);
        Assert.DoesNotContain("\r", cmd);
        Assert.Equal(("bash", "/root/.cache/sshm/shell-x.sh"), ShellIntegration.ParseSetup("bash\n/root/.cache/sshm/shell-x.sh\n"));
        Assert.Equal((null, null), ShellIntegration.ParseSetup("sh: 1: cannot create\n"));
    }
}
