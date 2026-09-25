using System.Diagnostics;
using System.Text.Json.Nodes;
using SshManager.Core.Inventory;
using SshManager.Core.Mcp;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Tests;

/// <summary>An MCP server over a temp vault, without SSH: protocol, access levels, the agent log.</summary>
internal sealed class McpFixture : IDisposable
{
    public readonly TempDir Tmp = new();
    public readonly VaultService Vault;
    public readonly McpSettings Settings = new() { Enabled = true };
    public readonly AgentLog Log;
    public readonly McpServer Server;
    public readonly List<string> Confirmations = [];
    public bool ConfirmAnswer = true;

    public McpFixture(params (string Name, McpAccess Access)[] servers)
    {
        Vault = new VaultService(Tmp.File("vault.dat"));
        Vault.Create("password123");
        Vault.Update(d =>
        {
            foreach (var (name, access) in servers)
                d.Servers.Add(new ServerEntry { Name = name, Host = name + ".example", McpAccess = access, JumpHost = null });
        });
        var ssh = new SshClientFactory(Vault, new KnownHostsService(Tmp.File("known_hosts")));
        Log = new AgentLog(Tmp.File("agent-logs"), () => (10L * 1024 * 1024, TimeSpan.FromDays(30)));
        Server = new McpServer(new McpHost
        {
            Vault = Vault, Ssh = ssh, Inventory = new ServerInventoryService(Vault, ssh), Metrics = new MetricsCollector(ssh),
            Scripts = new ScriptRunner(ssh), Log = Log, Settings = () => Settings,
            Confirm = (_, text) =>
            {
                Confirmations.Add(text);
                return Task.FromResult(ConfirmAnswer);
            },
        });
    }

    public async Task<JsonObject> Rpc(string method, JsonObject? p = null, McpSession? session = null, int id = 1)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p != null) msg["params"] = p;
        var response = await Server.HandleAsync(msg.ToJsonString(), session ?? Session);
        return (JsonObject)JsonNode.Parse(response!)!;
    }

    public readonly McpSession Session = new("test") { Client = "test-agent" };

    /// <summary>tools/call → (isError, text).</summary>
    public async Task<(bool Error, string Text)> Call(string tool, JsonObject? args = null)
    {
        var r = await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] });
        var result = r["result"]!;
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    public void Dispose() => Tmp.Dispose();
}

public class McpProtocolTests
{
    [Fact]
    public async Task Initialize_Negotiates_The_Version_And_Remembers_The_Client()
    {
        using var f = new McpFixture();
        var session = new McpSession("s1");
        var r = await f.Rpc("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "claude-code", ["version"] = "2.0" },
        }, session);
        Assert.Equal("2025-06-18", (string?)r["result"]!["protocolVersion"]);
        Assert.Equal("sshmanager", (string?)r["result"]!["serverInfo"]!["name"]);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Equal("claude-code", session.Client);

        var future = await f.Rpc("initialize", new JsonObject { ["protocolVersion"] = "2099-01-01" }, new McpSession("s2"));
        Assert.Equal(McpServer.Versions[0], (string?)future["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task JsonRpc_Errors_And_Notifications()
    {
        using var f = new McpFixture();
        Assert.Contains("-32700", await f.Server.HandleAsync("{not json", f.Session));
        Assert.Contains("-32600", await f.Server.HandleAsync("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]", f.Session));
        var unknown = await f.Rpc("resources/list");
        Assert.Equal(-32601, (int)unknown["error"]!["code"]!);
        Assert.Null(await f.Server.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", f.Session));
        Assert.Null(await f.Server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"result\":{}}", f.Session)); // a response to us
        var ping = await f.Rpc("ping", id: 7);
        Assert.Equal(7, (int)ping["id"]!);
        Assert.NotNull(ping["result"]);
        var badTool = await f.Rpc("tools/call", new JsonObject { ["name"] = "format_c" });
        Assert.Equal(-32602, (int)badTool["error"]!["code"]!);
    }

    private static async Task<List<string>> ToolNames(McpFixture f)
    {
        var r = await f.Rpc("tools/list");
        return r["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
    }

    [Fact]
    public async Task Tools_Listed_Up_To_The_Highest_Level_Any_Server_Allows()
    {
        using (var ro = new McpFixture(("a", McpAccess.ReadOnly), ("b", McpAccess.Off)))
        {
            var names = await ToolNames(ro);
            Assert.Contains("list_servers", names);
            Assert.Contains("list_cron_jobs", names);
            Assert.DoesNotContain("reboot_server", names);
            Assert.DoesNotContain("run_command", names);
            var tool = (await ro.Rpc("tools/list"))["result"]!["tools"]!.AsArray().First(t => (string)t!["name"]! == "container_logs")!;
            Assert.Equal("object", (string?)tool["inputSchema"]!["type"]);
            Assert.Contains("server", tool["inputSchema"]!["required"]!.AsArray().Select(x => (string)x!));
            Assert.True((bool)tool["annotations"]!["readOnlyHint"]!);
        }
        using (var full = new McpFixture(("a", McpAccess.ReadOnly), ("b", McpAccess.Full)))
        {
            var names = await ToolNames(full);
            Assert.Contains("run_command", names);
            Assert.Contains("upload", names);
            Assert.Contains("reboot_server", names);
        }
        using (var off = new McpFixture(("a", McpAccess.Full)))
        {
            off.Settings.Enabled = false;
            Assert.Equal(["list_servers"], await ToolNames(off));
        }
    }

    [Fact]
    public async Task Servers_Off_Are_Invisible_And_Levels_Are_Enforced_And_Logged()
    {
        using var f = new McpFixture(("web", McpAccess.ReadOnly), ("secret-db", McpAccess.Off), ("box", McpAccess.Full));
        var (err, text) = await f.Call("list_servers");
        Assert.False(err);
        Assert.Contains("\"web\"", text);
        Assert.Contains("\"read_only\"", text);
        Assert.Contains("\"box\"", text);
        Assert.DoesNotContain("secret-db", text);

        var hidden = await f.Call("server_status", new JsonObject { ["server"] = "secret-db" });
        Assert.True(hidden.Error);
        Assert.DoesNotContain("off", hidden.Text.ToLowerInvariant().Replace("offline", "")); // does not reveal the server

        var denied = await f.Call("run_command", new JsonObject { ["server"] = "web", ["command"] = "rm -rf /" });
        Assert.True(denied.Error);
        Assert.Contains("read_only", denied.Text);
        var rebootDenied = await f.Call("reboot_server", new JsonObject { ["server"] = "web.example" }); // by host too
        Assert.True(rebootDenied.Error);
        Assert.Empty(f.Confirmations); // refused before asking anyone

        var webId = f.Vault.Read(d => d.Servers.First(s => s.Name == "web").Id);
        var log = f.Log.Tail(webId, 10);
        Assert.Equal(2, log.Count);
        Assert.All(log, l => Assert.Contains("DENIED", l));
        Assert.Contains("run_command", log[0]);
        Assert.Contains("test-agent", log[0]);
        Assert.Contains("rm -rf /", log[0]); // the command is in the log
        Assert.Contains(f.Log.Tail(null, 10), l => l.Contains("list_servers") && l.Contains(" ok "));

        f.Settings.Enabled = false;
        var off = await f.Call("server_status", new JsonObject { ["server"] = "web" });
        Assert.True(off.Error);
    }

    [Fact]
    public async Task Write_File_Content_Is_Not_Logged_And_Local_Folders_Are_Checked()
    {
        using var f = new McpFixture(("box", McpAccess.Full));
        var outside = Path.Combine(Path.GetTempPath(), "sshm-not-allowed");
        var r = await f.Call("upload", new JsonObject { ["server"] = "box", ["local_path"] = outside, ["remote_dir"] = "/tmp" });
        Assert.True(r.Error);
        var boxId = f.Vault.Read(d => d.Servers[0].Id);
        Assert.Contains(f.Log.Tail(boxId, 5), l => l.Contains("upload") && l.Contains("DENIED"));

        // write_file fails (no SSH here) but its content must not reach the log
        await f.Call("write_file", new JsonObject { ["server"] = "box", ["path"] = "/tmp/x", ["content"] = "TOP-SECRET-CONTENT" });
        Assert.DoesNotContain(f.Log.Tail(boxId, 5), l => l.Contains("TOP-SECRET-CONTENT"));
        Assert.Contains(f.Log.Tail(boxId, 5), l => l.Contains("write_file") && l.Contains("chars>"));
    }
}

/// <summary>The real sshm.exe mcp against a control pipe of its own instance (not the running app's).</summary>
public class McpStdioBridgeTests
{
    private static Process StartBridge(string instance, string dataDir)
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "sshm.exe"), "mcp")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardInputEncoding = new System.Text.UTF8Encoding(false),
            CreateNoWindow = true,
        };
        psi.Environment["SSHMANAGER_INSTANCE"] = instance;
        psi.Environment["SSHMANAGER_DATA"] = dataDir;
        return Process.Start(psi)!;
    }

    private static async Task<List<JsonObject>> Exchange(Process p, IEnumerable<string> messages, int expect)
    {
        foreach (var m in messages) await p.StandardInput.WriteLineAsync(m);
        var replies = new List<JsonObject>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (replies.Count < expect)
        {
            var line = await p.StandardOutput.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            replies.Add((JsonObject)JsonNode.Parse(line!)!);
        }
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(0, p.ExitCode);
        return replies;
    }

    [Fact]
    public async Task Messages_Pass_Through_The_Control_Pipe_In_Utf8()
    {
        using var f = new McpFixture(("веб-сервер", McpAccess.ReadOnly));
        var instance = "mcpt" + Guid.NewGuid().ToString("N")[..8];
        var sessions = new System.Collections.Concurrent.ConcurrentDictionary<string, McpSession>();
        using var control = new SshManager.Core.Pipes.ControlServer(async req => req.Op == "mcp"
            ? new SshManager.Core.Pipes.ControlResponse
            {
                Ok = true, Value = await f.Server.HandleAsync(req.Payload!, sessions.GetOrAdd(req.Session!, id => new McpSession(id))),
            }
            : SshManager.Core.Pipes.ControlResponse.Fail("unexpected " + req.Op));
        Assert.True(control.StartForInstance(instance));

        using var p = StartBridge(instance, f.Tmp.Path);
        var replies = await Exchange(p,
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"bridge-test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_servers","arguments":{}}}""",
        ], expect: 3);

        var byId = replies.ToDictionary(r => (int)r["id"]!);
        Assert.Equal("2025-06-18", (string?)byId[1]["result"]!["protocolVersion"]);
        Assert.Contains(byId[2]["result"]!["tools"]!.AsArray(), t => (string?)t!["name"] == "server_status");
        Assert.Contains("веб-сервер", (string?)byId[3]["result"]!["content"]![0]!["text"]);
        Assert.Equal("bridge-test", Assert.Single(sessions).Value.Client); // one session for the whole process
    }

    [Fact]
    public async Task Without_The_App_Requests_Get_A_JsonRpc_Error()
    {
        using var tmp = new TempDir();
        using var p = StartBridge("mcpt" + Guid.NewGuid().ToString("N")[..8], tmp.Path);
        var replies = await Exchange(p,
        [
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"a","method":"tools/list"}""",
        ], expect: 1);
        var r = Assert.Single(replies);
        Assert.Equal("a", (string?)r["id"]);
        Assert.Equal(-32000, (int)r["error"]!["code"]!);
        Assert.Contains("SSH Manager", (string?)r["error"]!["message"]);
    }
}

public class McpHttpTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Token_Origin_Sessions_And_Methods()
    {
        using var f = new McpFixture(("web", McpAccess.ReadOnly));
        using var http = new McpHttpServer((body, s) => f.Server.HandleAsync(body, s), () => "s3cret-token");
        var port = FreePort();
        http.Start(port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        HttpRequestMessage Post(string json, string? token = "s3cret-token", string? session = null, string? origin = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            m.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (token != null) m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (session != null) m.Headers.Add("Mcp-Session-Id", session);
            if (origin != null) m.Headers.Add("Origin", origin);
            return m;
        }

        const string init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"http-test"}}}""";
        Assert.Equal(401, (int)(await client.SendAsync(Post(init, token: null))).StatusCode);
        Assert.Equal(401, (int)(await client.SendAsync(Post(init, token: "wrong"))).StatusCode);
        Assert.Equal(403, (int)(await client.SendAsync(Post(init, origin: "https://evil.example"))).StatusCode);
        Assert.Equal(404, (int)(await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/other"))).StatusCode);

        var r = await client.SendAsync(Post(init, origin: $"http://127.0.0.1:{port}"));
        Assert.Equal(200, (int)r.StatusCode);
        var session = r.Headers.GetValues("Mcp-Session-Id").Single();
        Assert.Contains("\"protocolVersion\":\"2025-06-18\"", await r.Content.ReadAsStringAsync());

        Assert.Equal(202, (int)(await client.SendAsync(Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", session: session))).StatusCode);
        var list = await client.SendAsync(Post("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", session: session));
        Assert.Equal(200, (int)list.StatusCode);
        Assert.Contains("server_status", await list.Content.ReadAsStringAsync());
        Assert.Equal(404, (int)(await client.SendAsync(Post("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", session: "nope"))).StatusCode);

        var get = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        get.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret-token");
        Assert.Equal(405, (int)(await client.SendAsync(get)).StatusCode);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret-token");
        delete.Headers.Add("Mcp-Session-Id", session);
        Assert.Equal(200, (int)(await client.SendAsync(delete)).StatusCode);
        Assert.Equal(404, (int)(await client.SendAsync(Post("""{"jsonrpc":"2.0","id":4,"method":"ping"}""", session: session))).StatusCode);

        // a foreign Host header (DNS rebinding: evil.example resolving to 127.0.0.1)
        var rebound = Post(init);
        rebound.Headers.Host = $"evil.example:{port}";
        Assert.Equal(403, (int)(await client.SendAsync(rebound)).StatusCode);
    }
}

public class McpPolicyTests
{
    [Fact]
    public void Levels_Include_The_Lower_Ones()
    {
        Assert.True(McpPolicy.Allows(McpAccess.Full, McpAccess.ReadOnly));
        Assert.True(McpPolicy.Allows(McpAccess.Reboot, McpAccess.Reboot));
        Assert.False(McpPolicy.Allows(McpAccess.LimitedWrite, McpAccess.Full));
        Assert.False(McpPolicy.Allows(McpAccess.Off, McpAccess.Off));
    }

    [Fact]
    public void Local_Paths_Must_Stay_Inside_The_Allowed_Folders()
    {
        using var tmp = new TempDir();
        var allowed = Directory.CreateDirectory(Path.Combine(tmp.Path, "Share")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(tmp.Path, "Share2")).FullName;
        var folders = new[] { allowed };

        Assert.Equal(Path.Combine(allowed, "a", "b.txt"), McpPolicy.LocalPath(Path.Combine(allowed, "a", "b.txt"), folders, out _));
        Assert.NotNull(McpPolicy.LocalPath(allowed, folders, out _));
        Assert.NotNull(McpPolicy.LocalPath(allowed.ToUpperInvariant() + "\\x", folders, out _)); // case does not matter
        Assert.Null(McpPolicy.LocalPath(Path.Combine(allowed, "..", "Share2", "x"), folders, out var e1));
        Assert.Contains("Share2", e1);
        Assert.Null(McpPolicy.LocalPath(sibling, folders, out _)); // a prefix of the name is not inside
        Assert.Null(McpPolicy.LocalPath("relative\\path", folders, out _));
        Assert.Null(McpPolicy.LocalPath(Path.Combine(allowed, "f.txt:stream"), folders, out _));
        Assert.Null(McpPolicy.LocalPath(@"\\?\" + allowed, folders, out _));
        Assert.Null(McpPolicy.LocalPath(allowed, [], out _));

        // a junction inside the allowed folder pointing outside is not "inside"
        var junction = Path.Combine(allowed, "escape");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{sibling}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (Directory.Exists(junction))
        {
            Assert.Null(McpPolicy.LocalPath(Path.Combine(junction, "file.txt"), folders, out _));
            Assert.NotNull(McpPolicy.LocalPath(Path.Combine(junction, "file.txt"), [allowed, sibling], out _));
            Directory.Delete(junction);
        }
    }
}

public class AgentLogTests
{
    private static AgentLogEntry E(DateTime utc, Guid? id, string tool, string result = "fine") =>
        new(utc, id, id == null ? "" : "srv", "test", tool, "{}", AgentOutcome.Ok, result, TimeSpan.FromMilliseconds(1200));

    [Fact]
    public void Lines_Rotate_By_Size_And_Day_And_Old_Files_Go()
    {
        using var tmp = new TempDir();
        var now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var limits = (MaxBytes: 256L * 1024, Retention: TimeSpan.FromDays(3));
        var log = new AgentLog(tmp.Path, () => limits, () => now);
        var id = Guid.NewGuid();
        var appended = 0;
        log.Appended += _ => appended++;

        var big = new string('x', 900);
        for (var i = 0; i < 200; i++) log.Write(E(now, id, "run_command", big + i)); // ~200 KB: passes a quarter of 256 KB (min 64 KB)
        Assert.Equal(200, appended);
        var files = Directory.GetFiles(tmp.Path, id.ToString("N") + "*.log");
        Assert.True(files.Length >= 2, "rotated by size");
        Assert.True(files.Sum(f => new FileInfo(f).Length) <= limits.MaxBytes, "total within the limit");
        var tail = log.Tail(id, 5);
        Assert.Equal(5, tail.Count);
        Assert.EndsWith("x199", tail[^1]);
        Assert.Contains("run_command", tail[0]);
        Assert.Contains("1.2s", tail[0]);

        // next day: a new file; after the retention period the old ones are gone
        now = now.AddDays(1);
        log.Write(E(now, id, "server_status"));
        Assert.Single(log.Tail(id, 1), l => l.Contains("server_status"));
        now = now.AddDays(5);
        log.Write(E(now, id, "list_ports"));
        log.PruneAll();
        var left = Directory.GetFiles(tmp.Path, id.ToString("N") + "*.log");
        Assert.All(left, f => Assert.True(new FileInfo(f).Name == id.ToString("N") + ".log" || f.Contains("20260907"), f));

        // calls without a server go to general.log; multi-line results stay on one line
        log.Write(E(now, null, "list_servers", "a\nb"));
        var general = Assert.Single(log.Tail(null, 10));
        Assert.Contains("a ⏎ b", general);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "general.log")));
    }

    [Fact]
    public void Long_Results_Are_Cut()
    {
        using var tmp = new TempDir();
        var log = new AgentLog(tmp.Path, () => (1024L * 1024, TimeSpan.FromDays(1)));
        log.Write(E(DateTime.UtcNow, null, "run_command", new string('y', 50_000)));
        var line = Assert.Single(log.Tail(null, 1));
        Assert.True(line.Length < AgentLog.MaxResultChars + 600);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Log_Lines_Read_Back_As_Entries()
    {
        var utc = new DateTime(2026, 9, 25, 6, 58, 37, DateTimeKind.Utc);
        var e = new AgentLogEntry(utc, Guid.NewGuid(), "orange", "codex-probe", "run_command", "{\"command\":\"df -h\\nuptime\"}",
            AgentOutcome.Failed, "exit 2: line 1\nline 2", TimeSpan.FromSeconds(9.24));
        var back = AgentLogEntry.TryParse(e.Format());
        Assert.NotNull(back);
        Assert.Equal(utc, back.Utc);
        Assert.Equal(("orange", "codex-probe", "run_command", AgentOutcome.Failed), (back.ServerName, back.Client, back.Tool, back.Outcome));
        Assert.Equal("exit 2: line 1\nline 2", back.Result);
        Assert.Equal(e.Args, back.Args);
        Assert.Equal(9.2, back.Duration.TotalSeconds, 3);

        var bare = AgentLogEntry.TryParse(new AgentLogEntry(utc, null, "", "c", "list_servers", "", AgentOutcome.Denied, "no", TimeSpan.Zero).Format());
        Assert.Equal(("", "", "no", AgentOutcome.Denied), (bare!.ServerName, bare.Args, bare.Result, bare.Outcome));
        Assert.Null(AgentLogEntry.TryParse("hand-written note"));
    }

    [Fact]
    public void Console_Shows_Commands_And_Output_Like_A_Terminal()
    {
        var console = new AgentConsole();
        var utc = DateTime.UtcNow;
        var run = console.Render(new AgentLogEntry(utc, null, "orange", "codex", "run_command", "{\"command\":\"ls --color\",\"sudo\":true}",
            AgentOutcome.Ok, "exit 1: \x1b[34mdir\x1b[0m\n\x1b[2Jgone\x1b]0;title\x07\rdone", TimeSpan.FromSeconds(1)));
        Assert.Contains("──── ", run); // the day
        Assert.Contains("codex@orange", run);
        Assert.Contains("# \x1b[1mls --color", run); // sudo: root's prompt
        Assert.Contains("\x1b[34mdir\x1b[0m\r\ngonedone", run); // colours stay, clear screen, title and \r go
        Assert.DoesNotContain("\x1b[2J", run);
        Assert.Contains("[exit 1]", run);

        var tool = console.Render(new AgentLogEntry(utc, null, "orange", "codex", "service_logs", "{\"unit\":\"ssh\",\"lines\":5,\"grep\":\"a b\"}",
            AgentOutcome.Ok, "{\"a\":1}", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("────", tool); // same day
        Assert.Contains("service_logs", tool);
        Assert.Contains("\x1b[90m--unit\x1b[0m \x1b[33mssh", tool); // arguments as options
        Assert.Contains("'a b'", tool);
        Assert.Contains("{\r\n  \"a\": 1\r\n}", tool); // JSON indented

        // a command cut at the log's length limit still shows as a command
        var cut = new AgentLogEntry(utc, null, "orange", "codex", "run_command", "{\"command\":\"" + new string('x', 3000) + "\"}",
            AgentOutcome.Ok, "exit 0: ok", TimeSpan.Zero);
        var shown = console.Render(AgentLogEntry.TryParse(cut.Format())!);
        Assert.Contains("$ \x1b[1mxxxx", shown);
        Assert.DoesNotContain("[exit", shown);

        var denied = console.Render(new AgentLogEntry(utc, null, "orange", "codex", "run_command", "{\"command\":\"reboot\"}",
            AgentOutcome.Denied, "read only", TimeSpan.Zero));
        Assert.Contains("\x1b[31mread only", denied);
        Assert.Contains("[denied]", denied);
    }
}
