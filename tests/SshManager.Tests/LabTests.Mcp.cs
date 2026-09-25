using System.Text.Json.Nodes;
using SshManager.Core.Inventory;
using SshManager.Core.Mcp;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Scripts;

namespace SshManager.Tests;

/// <summary>The MCP tools against the lab servers, over real SSH, the way an agent calls them.</summary>
public partial class LabTests
{
    private sealed class Agent(McpServer server, McpSession session)
    {
        private int _id;

        public async Task<(bool Error, string Text)> Call(string tool, JsonObject args)
        {
            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = ++_id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var r = (JsonObject)JsonNode.Parse((await server.HandleAsync(msg.ToJsonString(), session))!)!;
            var result = r["result"]!;
            return ((bool)result["isError"]!, (string)result["content"]![0]!["text"]!);
        }

        public async Task<string> Ok(string tool, JsonObject args)
        {
            var (error, text) = await Call(tool, args);
            Assert.False(error, $"{tool}: {text}");
            return text;
        }
    }

    [Fact]
    public async Task Mcp_Tools_Work_Over_Ssh_Within_The_Access_Level()
    {
        if (!Enabled("mcp")) return;
        await OnEachServer(async lab =>
        {
            Sh(lab, "true"); // the host key becomes known here; MCP never asks to trust one
            using var local = new TempDir();
            var allowed = Directory.CreateDirectory(Path.Combine(local.Path, "share")).FullName;
            var name = lab.Server.Name;
            var id = lab.Server.Id;
            void Access(McpAccess a) => lab.Vault.Update(d =>
            {
                var s = d.Servers.First(x => x.Id == id);
                s.McpAccess = a;
                s.McpFolders = [allowed];
            });
            Access(McpAccess.Full);
            lab.Vault.Update(d => BuiltinScripts.Sync(d));

            var confirmations = new List<string>();
            Guid? rebooted = null;
            var agentLog = new AgentLog(local.File("agent-logs"), () => (10L * 1024 * 1024, TimeSpan.FromDays(30)));
            var mcp = new McpServer(new McpHost
            {
                Vault = lab.Vault, Ssh = lab.Ssh, Inventory = new ServerInventoryService(lab.Vault, lab.Ssh), Metrics = new MetricsCollector(lab.Ssh),
                Scripts = lab.Runner, Log = agentLog, Settings = () => new McpSettings { Enabled = true },
                Confirm = (_, text) =>
                {
                    lock (confirmations) confirmations.Add(text);
                    return Task.FromResult(true);
                },
                Reboot = sid =>
                {
                    rebooted = sid;
                    return Task.CompletedTask;
                },
            });
            var agent = new Agent(mcp, new McpSession("lab") { Client = "lab-agent" });
            var S = (JsonObject extra) => { extra["server"] = name; return extra; };

            // read only
            var status = await agent.Ok("server_status", S([]));
            Assert.Contains("\"cpu_percent\"", status);
            Assert.Contains("\"memory_percent\"", status);
            await agent.Ok("list_containers", S(new() { ["refresh"] = true })); // a list, or "Docker is not installed"
            var after = await agent.Ok("server_status", S([]));
            Assert.Contains("\"inventory_updated\": \"20", after); // the refresh reached the server's facts
            Assert.Contains("\"scheduled_jobs\"", after);
            Assert.StartsWith("[", (await agent.Ok("list_cron_jobs", S([]))).Trim());
            Assert.Contains("\"listening\"", await agent.Ok("list_ports", S([])));
            await agent.Ok("service_logs", S(new() { ["unit"] = "ssh", ["lines"] = 5 }));
            Assert.True((await agent.Call("container_logs", S(new() { ["container"] = "no-such-container" }))).Error);

            // full: commands and files
            var run = await agent.Ok("run_command", S(new() { ["command"] = "echo mcp-hi; id -un; echo oops >&2" }));
            Assert.Contains("exit code: 0", run);
            Assert.Contains("mcp-hi", run);
            Assert.Contains("root", run);
            Assert.Contains("oops", run);
            await agent.Ok("run_command", S(new() { ["command"] = "rm -rf /tmp/sshm-mcp; mkdir -p /tmp/sshm-mcp" }));
            await agent.Ok("write_file", S(new() { ["path"] = "/tmp/sshm-mcp/a.txt", ["content"] = "привет из MCP\nline 2\n" }));
            Assert.Equal("привет из MCP\nline 2\n", await agent.Ok("read_file", S(new() { ["path"] = "/tmp/sshm-mcp/a.txt" })));
            Assert.Contains("\"a.txt\"", await agent.Ok("list_directory", S(new() { ["path"] = "/tmp/sshm-mcp" })));

            await agent.Ok("download", S(new() { ["remote_path"] = "/tmp/sshm-mcp/a.txt", ["local_dir"] = Path.Combine(allowed, "in") }));
            Assert.Equal("привет из MCP\nline 2\n", File.ReadAllText(Path.Combine(allowed, "in", "a.txt")));
            var outside = await agent.Call("download", S(new() { ["remote_path"] = "/tmp/sshm-mcp/a.txt", ["local_dir"] = local.Path }));
            Assert.True(outside.Error);
            Assert.False(File.Exists(Path.Combine(local.Path, "a.txt")));

            Directory.CreateDirectory(Path.Combine(allowed, "up", "sub"));
            File.WriteAllText(Path.Combine(allowed, "up", "sub", "b.txt"), "uploaded");
            await agent.Ok("upload", S(new() { ["local_path"] = Path.Combine(allowed, "up"), ["remote_dir"] = "/tmp/sshm-mcp/deep/er" }));
            Assert.Equal("uploaded", Sh(lab, "cat /tmp/sshm-mcp/deep/er/up/sub/b.txt"));

            Assert.True((await agent.Call("delete_path", S(new() { ["path"] = "/etc" }))).Error); // protected
            await agent.Ok("delete_path", S(new() { ["path"] = "/tmp/sshm-mcp" }));
            Assert.Contains("gone", Sh(lab, "test -e /tmp/sshm-mcp || echo gone"));
            Assert.Contains(confirmations, c => c.Contains("/tmp/sshm-mcp"));

            // menu actions: an install script as a job
            var scripts = await agent.Ok("list_install_scripts", S([]));
            Assert.Contains("Caddy", scripts);
            var started = JsonNode.Parse(await agent.Ok("run_install_script", S(new()
            {
                ["script"] = "static-site-docker", ["params"] = new JsonObject { ["SITE_PORT"] = "8086", ["SITE_TITLE"] = "via mcp", ["SITE_OVERWRITE"] = "1" },
            })))!;
            var job = (string)started["job_id"]!;
            JsonNode state;
            var until = DateTime.UtcNow.AddMinutes(8);
            do
            {
                await Task.Delay(3000);
                state = JsonNode.Parse(await agent.Ok("get_job", S(new() { ["job_id"] = job })))!;
            } while ((string)state["state"]! == "running" && DateTime.UtcNow < until);
            Assert.True((string)state["state"]! == "done", state.ToJsonString());
            Assert.EndsWith(":8086", (string)state["results"]!["SITE_URL"]!);
            Assert.Contains("via mcp", Sh(lab, "curl -s http://127.0.0.1:8086/"));
            Assert.Contains(lab.Vault.Read(d => d.Servers.First(x => x.Id == id).ScriptRuns), r => r.ScriptName.Contains("Caddy") && r.ExitCode == 0);
            Assert.Contains("SITE_URL", await agent.Ok("script_results", S([])));

            // levels: read only refuses writes; the reboot goes through the app's callback after a confirmation
            Access(McpAccess.ReadOnly);
            var denied = await agent.Call("run_command", S(new() { ["command"] = "reboot" }));
            Assert.True(denied.Error);
            Assert.True((await agent.Call("run_install_script", S(new() { ["script"] = "static-site-docker" }))).Error);
            Access(McpAccess.Reboot);
            await agent.Ok("reboot_server", S(new() { ["reason"] = "lab test" }));
            Assert.Equal(id, rebooted);
            Assert.Contains(confirmations, c => c.Contains("lab test"));
            Access(McpAccess.Off);
            Assert.True((await agent.Call("server_status", S([]))).Error);

            var lines = agentLog.Tail(id, 200);
            Assert.Contains(lines, l => l.Contains("run_command") && l.Contains(" ok ") && l.Contains("mcp-hi"));
            Assert.Contains(lines, l => l.Contains("run_command") && l.Contains("DENIED"));
            Assert.Contains(lines, l => l.Contains("write_file") && !l.Contains("привет из MCP"));
            Assert.Contains(lines, l => l.Contains("run_install_script") && l.Contains("finished"));
            log.WriteLine($"{name}: {lines.Count} log lines\n" + string.Join("\n", lines.TakeLast(8)));
            Sh(lab, "cd /opt/static-site && docker compose down >/dev/null 2>&1; true", check: false);
        });
    }
}
