using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;

namespace SshManager.Core.Mcp;

/// <summary>The tools: read-only ones, reboot and the app's menu actions (limited write). Full access is in McpToolsFull.cs.</summary>
public sealed partial class McpServer
{
    internal const int MaxOutput = 100_000;

    private sealed record Prop(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

    private static readonly Prop ServerProp = new("server", "string", "Server name or host as in SSH Manager (see list_servers).", true);

    private static JsonObject Schema(params Prop[] props)
    {
        var properties = new JsonObject();
        foreach (var p in props)
        {
            var o = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null) o["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
            if (p.Type == "object") o["additionalProperties"] = new JsonObject { ["type"] = "string" };
            properties[p.Name] = o;
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var required = props.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray();
        if (required.Length > 0) schema["required"] = new JsonArray(required);
        return schema;
    }

    private List<McpTool> BuildTools() =>
    [
        // ---------- read only ----------
        new("list_servers", "List servers",
            "The SSH servers available to you, with their access level, OS, status and (for full access) the local folders you may copy files to and from.",
            McpAccess.Off, Schema(), ListServers, PerServer: false),
        new("server_status", "Server status",
            "Availability, latency, CPU / memory / disk usage (read now), uptime percentages, OS and a summary of containers, services and scheduled jobs.",
            McpAccess.ReadOnly, Schema(ServerProp), ServerStatus),
        new("list_containers", "List Docker containers",
            "Docker containers with state, image, ports, restart policy and compose project.",
            McpAccess.ReadOnly, Schema(ServerProp, new Prop("refresh", "boolean", "Read them from the server now instead of the last inventory.")), ListContainers),
        new("list_services", "List services",
            "Well-known systemd services (web servers, databases, VPNs, docker, ssh…) with state and autostart.",
            McpAccess.ReadOnly, Schema(ServerProp, new Prop("refresh", "boolean", "Read them from the server now.")), ListServices),
        new("list_cron_jobs", "List scheduled jobs",
            "Every user's crontab, /etc/crontab, /etc/cron.d, cron.hourly…monthly scripts and systemd timers (read now).",
            McpAccess.ReadOnly, Schema(ServerProp), ListCronJobs),
        new("list_ports", "List ports",
            "TCP ports the server listens on (with the process) and the ports SSH Manager monitors, with their state.",
            McpAccess.ReadOnly, Schema(ServerProp), ListPorts),
        new("container_logs", "Container log",
            "The last lines of a container's log (docker logs --tail).",
            McpAccess.ReadOnly, Schema(ServerProp, new Prop("container", "string", "Container name or id.", true),
                new Prop("lines", "integer", "How many lines (default 100, at most 2000)."),
                new Prop("since", "string", "Only newer entries, e.g. \"30m\", \"2h\", \"2026-09-25T10:00:00\".")), ContainerLogs),
        new("service_logs", "Service log",
            "The last lines of a systemd unit's journal (journalctl -u … -n).",
            McpAccess.ReadOnly, Schema(ServerProp, new Prop("unit", "string", "Unit name, e.g. nginx or nginx.service.", true),
                new Prop("lines", "integer", "How many lines (default 100, at most 2000).")), ServiceLogs),

        // ---------- reboot ----------
        new("reboot_server", "Reboot server",
            "Reboots the server. The user may have to confirm it in SSH Manager first. Returns once the reboot started; check server_status after a minute or two.",
            McpAccess.Reboot, Schema(ServerProp, new Prop("reason", "string", "Why, shown to the user in the confirmation.")), RebootServer,
            ReadOnly: false, Destructive: true),

        // ---------- limited write: the app's menus ----------
        new("container_action", "Container action",
            "start, stop, restart, remove (asks the user) a container, or set its restart policy (set_restart with restart_policy).",
            McpAccess.LimitedWrite, Schema(ServerProp, new Prop("container", "string", "Container name or id.", true),
                new Prop("action", "string", "What to do.", true, ["start", "stop", "restart", "set_restart", "remove"]),
                new Prop("restart_policy", "string", "For set_restart.", Enum: ContainerCommands.RestartPolicies),
                new Prop("remove_volumes", "boolean", "For remove: also its anonymous volumes."),
                new Prop("remove_image", "boolean", "For remove: also its image.")), ContainerAction,
            ReadOnly: false, Destructive: true),
        new("service_action", "Service action",
            "start, stop, restart, enable (autostart on) or disable a systemd service. Stopping or disabling ssh asks the user.",
            McpAccess.LimitedWrite, Schema(ServerProp, new Prop("unit", "string", "Unit name, e.g. nginx.", true),
                new Prop("action", "string", "What to do.", true, ["start", "stop", "restart", "enable", "disable"])), ServiceAction,
            ReadOnly: false, Destructive: true),
        new("refresh_info", "Refresh server info",
            "Reads the OS, containers, services, scheduled jobs and ports from the server again.",
            McpAccess.LimitedWrite, Schema(ServerProp), RefreshInfo, ReadOnly: false),
        new("list_install_scripts", "List install scripts",
            "The install scripts saved in SSH Manager that fit the server's OS, with their parameters (VPNs, web servers, cloud storage, Docker…).",
            McpAccess.LimitedWrite, Schema(ServerProp), ListInstallScripts),
        new("run_install_script", "Run install script",
            "Starts an install script with parameters and returns a job id at once (scripts take minutes): follow it with get_job. " +
            "Its results (links, addresses) are saved to the server in SSH Manager.",
            McpAccess.LimitedWrite, Schema(ServerProp, new Prop("script", "string", "Script name as in list_install_scripts.", true),
                new Prop("params", "object", "Parameter values by name (strings; booleans as \"1\" / \"0\"); missing ones get the defaults or the values of the last run.")),
            RunInstallScript, ReadOnly: false, Destructive: true),
        new("get_job", "Get job",
            "State, exit code, output and results of a job started by run_install_script.",
            McpAccess.LimitedWrite, Schema(ServerProp, new Prop("job_id", "string", "The id run_install_script returned.", true),
                new Prop("lines", "integer", "How many last output lines (default 100, at most 2000).")), GetJob),

        .. FullTools(),
    ];

    // ---------- helpers ----------

    /// <summary>SSH.NET cannot go through a jump host or use extra ssh options.</summary>
    internal static void CheckDirect(ServerEntry s)
    {
        if (!ServerInventoryService.Supported(s) || !string.IsNullOrWhiteSpace(s.ExtraArgs)) throw new InvalidOperationException(L.Get("Mcp.NoJump"));
    }

    internal Task<ShellResult> Exec(ToolCall c, string command, bool elevated, TimeSpan timeout)
    {
        var s = c.S;
        return Task.Run(() =>
        {
            CheckDirect(s);
            using var client = _host.Ssh.Connect(s, interactive: false);
            return RemoteShell.Run(client, s, command, elevated, timeout);
        }, c.Ct);
    }

    internal static string Cut(string text, int max = MaxOutput) =>
        text.Length <= max ? text : text[..max] + $"\n… ({text.Length - max} more characters)";

    /// <summary>The server as the vault has it now (facts may have been refreshed since the call started).</summary>
    private ServerEntry Fresh(ServerEntry s) =>
        _host.Vault.Read(d => d.Servers.FirstOrDefault(x => x.Id == s.Id)?.Clone()) ?? s;

    private async Task<ServerEntry> Refreshed(ToolCall c, bool force)
    {
        var s = c.S;
        if (force || s.Facts?.InventoryUpdated == null)
        {
            CheckDirect(s);
            await _host.Inventory.RefreshAsync(s.Id);
            s = Fresh(s);
            if (s.Facts?.InventoryError is { } e && force) throw new InvalidOperationException(e);
        }
        return s;
    }

    private async Task<bool> Confirm(ToolCall c, string text)
    {
        if (!_host.Settings().ConfirmDangerous) return true;
        return await _host.Confirm(L.Get("Mcp.ConfirmTitle"), L.F("Mcp.ConfirmBy", c.Session.Client) + "\n\n" + text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex ContainerName();

    [GeneratedRegex(@"^[A-Za-z0-9@_.:\\-]{1,200}$")]
    private static partial Regex UnitName();

    [GeneratedRegex(@"^[0-9A-Za-z:.+\- T]{1,40}$")]
    private static partial Regex SinceValue();

    private static readonly Regex Secretish = new("PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE|KEY", RegexOptions.IgnoreCase);

    // ---------- read only ----------

    private Task<ToolResult> ListServers(ToolCall c)
    {
        var servers = _host.Vault.Read(d => d.Servers.Where(s => s.McpAccess != McpAccess.Off).OrderBy(s => s.Group).ThenBy(s => s.Name)
            .Select(s => s.Clone()).ToList());
        var list = servers.Select(s =>
        {
            var h = _host.Health?.Get(s.Id);
            return new
            {
                name = s.Name,
                host = s.Display,
                group = s.Group.Length > 0 ? s.Group : null,
                access = LevelName(s.McpAccess),
                os = s.Facts?.OsLabel,
                status = h?.State.ToString().ToLowerInvariant(),
                latency_ms = h?.LatencyMs,
                local_folders = s.McpAccess == McpAccess.Full ? s.McpFolders : null,
                note = !ServerInventoryService.Supported(s) || !string.IsNullOrWhiteSpace(s.ExtraArgs) ? L.Get("Mcp.NoJump") : null,
            };
        }).ToList();
        return Task.FromResult(list.Count == 0 ? new ToolResult(L.Get("Mcp.NoServers")) : ToolResult.Data(list, $"{list.Count} servers"));
    }

    private async Task<ToolResult> ServerStatus(ToolCall c)
    {
        var s = c.S;
        var h = _host.Health?.Get(s.Id);
        Monitoring.ServerMetrics? m = null;
        if (ServerInventoryService.Supported(s) && string.IsNullOrWhiteSpace(s.ExtraArgs))
        {
            await _host.Metrics.CollectAsync(s);
            m = _host.Metrics.Get(s.Id);
        }
        var up = _host.Uptime?.Stats(s.Id, DateTime.UtcNow);
        var f = s.Facts;
        return ToolResult.Data(new
        {
            name = s.Name,
            host = s.Display,
            access = LevelName(s.McpAccess),
            os = f?.OsLabel,
            kernel = f?.Kernel,
            health = h == null ? null : new { state = h.State.ToString().ToLowerInvariant(), latency_ms = h.LatencyMs, @checked = h.Checked, error = h.Error, ssh_down_but_port_open = h.SshDown },
            metrics = m == null ? null : new
            {
                cpu_percent = Round(m.CpuPercent), load1 = m.Load1, cores = m.Cores,
                memory_percent = Round(m.MemPercent), memory_total_mb = m.MemTotalKb / 1024,
                disk_percent = Round(m.DiskPercent), disk_total_gb = m.DiskTotalKb is { } d ? Math.Round(d / 1024.0 / 1024, 1) : (double?)null,
                uptime = m.Uptime?.ToString(@"d\.hh\:mm"), error = m.Error,
            },
            availability_percent = up == null ? null : new { day = Round(up.Day), week = Round(up.Week), month = Round(up.Month) },
            containers = f == null ? null : new { running = f.Containers.Count(x => x.IsRunning), total = f.Containers.Count, docker = f.DockerAvailable },
            services = f == null ? null : new { running = f.Services.Count(x => x.IsRunning), total = f.Services.Count },
            scheduled_jobs = f?.CronJobs.Count,
            inventory_updated = f?.InventoryUpdated,
            inventory_error = f?.InventoryError,
        }, h?.State.ToString());
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private async Task<ToolResult> ListContainers(ToolCall c)
    {
        var s = await Refreshed(c, c.Bool("refresh"));
        var f = s.Facts;
        if (f == null) return ToolResult.Fail(L.Get("Mcp.NoInventory"));
        if (!f.DockerAvailable) return new ToolResult(L.Get("Mcp.NoDocker"));
        return ToolResult.Data(f.Containers.Select(x => new
        {
            name = x.Name, id = x.Id.Length > 12 ? x.Id[..12] : x.Id, image = x.Image, state = x.State, status = x.Status, ports = x.Ports,
            restart_policy = x.RestartPolicy, compose_project = x.ComposeProject, compose_dir = x.ComposeDir, compose_service = x.ComposeService,
        }).ToList(), $"{f.Containers.Count} containers");
    }

    private async Task<ToolResult> ListServices(ToolCall c)
    {
        var s = await Refreshed(c, c.Bool("refresh"));
        if (s.Facts == null) return ToolResult.Fail(L.Get("Mcp.NoInventory"));
        return ToolResult.Data(s.Facts.Services.Select(x => new
        {
            unit = x.Unit, title = x.Title, active = x.Active, sub = x.Sub, enabled = x.Enabled,
        }).ToList(), $"{s.Facts.Services.Count} services");
    }

    private async Task<ToolResult> ListCronJobs(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var jobs = await Task.Run(() => CronCollector.Collect(_host.Ssh, s), c.Ct);
        _host.Vault.UpdateFacts(s.Id, f => f.CronJobs = jobs);
        return ToolResult.Data(jobs.Select(j => new
        {
            kind = j.Kind.ToString().ToLowerInvariant(), source = j.Source, user = j.User.Length > 0 ? j.User : null, schedule = j.Schedule,
            command = j.Command, next = j.Next, last = j.Last, description = j.Description, active = j.Active,
        }).ToList(), $"{jobs.Count} jobs");
    }

    private Task<ToolResult> ListPorts(ToolCall c)
    {
        var s = c.S;
        var health = _host.Health?.GetPorts(s.Id);
        var listening = (s.Facts?.ListeningPorts ?? []).Select(p => new
        {
            port = p.Port, addresses = p.Addresses, process = p.Process, local_only = p.LocalOnly,
            monitored = s.MonitoredPorts.FirstOrDefault(m => m.Port == p.Port)?.Label,
            state = health != null && health.TryGetValue(p.Port, out var ph) ? ph.State.ToString().ToLowerInvariant() : null,
        }).ToList();
        var monitoredOnly = s.MonitoredPorts.Where(m => listening.All(l => l.port != m.Port)).Select(m => new
        {
            port = m.Port, name = m.Name,
            state = health != null && health.TryGetValue(m.Port, out var ph) ? ph.State.ToString().ToLowerInvariant() : null,
        }).ToList();
        return Task.FromResult(ToolResult.Data(new { listening, monitored_not_listening = monitoredOnly }, $"{listening.Count} ports"));
    }

    private async Task<ToolResult> ContainerLogs(ToolCall c)
    {
        var name = c.Required("container");
        if (!ContainerName().IsMatch(name)) return ToolResult.Fail(L.F("Mcp.BadName", name));
        var lines = c.Int("lines", 100, 1, 2000);
        var since = c.Str("since");
        if (since.Length > 0 && !SinceValue().IsMatch(since)) return ToolResult.Fail(L.F("Mcp.BadName", since));
        var cmd = $"docker logs --tail {lines}" + (since.Length > 0 ? " --since " + RemoteShell.Quote(since) : "") + " " + RemoteShell.Quote(name) + " 2>&1";
        var r = await Exec(c, cmd, elevated: true, TimeSpan.FromMinutes(1));
        return r.Ok ? new ToolResult(Cut(r.Output), LogText: $"{r.Output.Split('\n').Length} lines") : ToolResult.Fail(Cut(r.Combined));
    }

    private async Task<ToolResult> ServiceLogs(ToolCall c)
    {
        var unit = c.Required("unit");
        if (!UnitName().IsMatch(unit)) return ToolResult.Fail(L.F("Mcp.BadName", unit));
        var lines = c.Int("lines", 100, 1, 2000);
        var r = await Exec(c, $"journalctl -u {RemoteShell.Quote(unit)} -n {lines} --no-pager 2>&1", elevated: true, TimeSpan.FromMinutes(1));
        return r.Ok ? new ToolResult(Cut(r.Output), LogText: $"{r.Output.Split('\n').Length} lines") : ToolResult.Fail(Cut(r.Combined));
    }

    // ---------- reboot ----------

    private async Task<ToolResult> RebootServer(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var reason = c.Str("reason");
        if (!await Confirm(c, L.F("Mcp.ConfirmReboot", s.Name, s.Display) + (reason.Length > 0 ? "\n" + L.F("Mcp.Reason", reason) : "")))
            throw new McpDeniedException(L.Get("Mcp.UserDeclined"));
        if (_host.Reboot != null) await _host.Reboot(s.Id);
        else await Task.Run(() => ServerPower.Reboot(_host.Ssh, s, interactive: false), c.Ct);
        return new ToolResult(L.F("Mcp.Rebooting", s.Name));
    }

    // ---------- limited write ----------

    private async Task<ToolResult> ContainerAction(ToolCall c)
    {
        var name = c.Required("container");
        var action = c.Required("action");
        var s = await Refreshed(c, false);
        var container = FindContainer(s, name) ?? FindContainer(s = await Refreshed(c, true), name);
        if (container == null) return ToolResult.Fail(L.F("Mcp.NoContainer", name, s.Name));
        string command;
        switch (action)
        {
            case "start": command = ContainerCommands.Start(container); break;
            case "stop": command = ContainerCommands.Stop(container); break;
            case "restart": command = ContainerCommands.Restart(container); break;
            case "set_restart":
                var policy = c.Required("restart_policy");
                if (!ContainerCommands.RestartPolicies.Contains(policy)) return ToolResult.Fail(L.F("Mcp.BadName", policy));
                command = ContainerCommands.SetRestart(container, policy);
                break;
            case "remove":
                var removal = new ContainerRemoval(WholeProject: false, Volumes: c.Bool("remove_volumes"), Image: c.Bool("remove_image"), ProjectFolder: false);
                if (!await Confirm(c, L.F("Mcp.ConfirmRemoveContainer", container.Name, s.Name) +
                                      (removal.Volumes ? "\n" + L.Get("Mcp.AndVolumes") : "") + (removal.Image ? "\n" + L.Get("Mcp.AndImage") : "")))
                    throw new McpDeniedException(L.Get("Mcp.UserDeclined"));
                command = ContainerCommands.Remove(container, removal);
                break;
            default:
                return ToolResult.Fail(L.F("Mcp.BadAction", action));
        }
        var r = await Exec(c, command, elevated: true, TimeSpan.FromMinutes(5));
        _ = _host.Inventory.RefreshAsync(s.Id);
        _host.Changed?.Invoke(s.Id);
        return r.Ok ? new ToolResult(L.F("Mcp.Done", action, container.Name) + (r.Output.Trim() is { Length: > 0 } o ? "\n" + Cut(o, 4000) : ""))
            : ToolResult.Fail(Cut(r.Combined));
    }

    private static ContainerInfo? FindContainer(ServerEntry s, string name) =>
        s.Facts?.Containers.FirstOrDefault(x => x.Name == name) ??
        s.Facts?.Containers.FirstOrDefault(x => name.Length >= 4 && x.Id.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    private async Task<ToolResult> ServiceAction(ToolCall c)
    {
        var unit = c.Required("unit");
        if (unit.EndsWith(".service", StringComparison.Ordinal)) unit = unit[..^".service".Length];
        if (!UnitName().IsMatch(unit)) return ToolResult.Fail(L.F("Mcp.BadName", unit));
        var action = c.Required("action");
        var info = new ServiceInfo { Unit = unit };
        if (action is "stop" or "disable" && ServiceCommands.IsSsh(info) &&
            !await Confirm(c, L.F("Mcp.ConfirmStopSsh", unit, c.S.Name)))
            throw new McpDeniedException(L.Get("Mcp.UserDeclined"));
        var command = action switch
        {
            "start" => ServiceCommands.Start(info),
            "stop" => ServiceCommands.Stop(info),
            "restart" => ServiceCommands.Restart(info),
            "enable" => ServiceCommands.Enable(info),
            "disable" => ServiceCommands.Disable(info),
            _ => null,
        };
        if (command == null) return ToolResult.Fail(L.F("Mcp.BadAction", action));
        var r = await Exec(c, command + $"; systemctl is-active {RemoteShell.Quote(unit + ".service")} 2>&1", elevated: true, TimeSpan.FromMinutes(5));
        _ = _host.Inventory.RefreshAsync(c.S.Id);
        _host.Changed?.Invoke(c.S.Id);
        return r.Ok || r.Output.Trim() is "active" or "inactive"
            ? new ToolResult(L.F("Mcp.Done", action, unit) + "\nsystemctl is-active: " + r.Output.Trim().Split('\n').Last())
            : ToolResult.Fail(Cut(r.Combined));
    }

    private async Task<ToolResult> RefreshInfo(ToolCall c)
    {
        var s = await Refreshed(c, true);
        _host.Changed?.Invoke(s.Id);
        var f = s.Facts!;
        return ToolResult.Data(new
        {
            os = f.OsLabel, kernel = f.Kernel, containers = f.Containers.Count, services = f.Services.Count, scheduled_jobs = f.CronJobs.Count,
            listening_ports = f.ListeningPorts.Count, updated = f.InventoryUpdated,
        });
    }

    private IEnumerable<ScriptEntry> ScriptsFor(ServerEntry s) =>
        _host.Vault.Read(d => d.Scripts.Select(x => x.Clone()).ToList()).Where(x => x.Matches(s.Facts)).OrderBy(x => x.Name);

    private Task<ToolResult> ListInstallScripts(ToolCall c)
    {
        var list = ScriptsFor(c.S).Select(x =>
        {
            var m = ScriptManifest.Parse(x.Body);
            return new
            {
                name = x.Name,
                group = m.Group,
                os = x.OsFilter.Length > 0 ? x.OsFilter : null,
                kind = x.Kind == ScriptKind.Compose ? "docker compose" : "bash",
                description = m.Description,
                @params = m.Params.Select(p => new
                {
                    name = p.Name, type = p.Type.ToString().ToLowerInvariant(), label = p.Label, @default = p.Default,
                    options = p.Options.Count > 0 ? p.Options : null, required = p.Required, hint = p.Hint,
                    only_when = p.WhenName != null ? $"{p.WhenName}={p.WhenValue}" : null,
                }).ToList(),
                results = m.Results.Select(r => r.Name).ToList(),
            };
        }).ToList();
        return Task.FromResult(ToolResult.Data(list, $"{list.Count} scripts"));
    }

    // ---------- jobs (install scripts) ----------

    private sealed class Job
    {
        public required string Id { get; init; }
        public required Guid ServerId { get; init; }
        public required string Script { get; init; }
        public DateTime Started { get; } = DateTime.UtcNow;
        public DateTime? Finished { get; set; }
        public int? ExitCode { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, string> Results { get; set; } = [];
        public readonly StringBuilder Output = new();
        public string State => Finished == null ? "running" : ExitCode == 0 ? "done" : "failed";
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    private Task<ToolResult> RunInstallScript(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var name = c.Required("script");
        var scripts = ScriptsFor(s).ToList();
        var script = scripts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)) ??
                     scripts.FirstOrDefault(x => string.Equals(x.BuiltinId, name, StringComparison.OrdinalIgnoreCase));
        if (script == null) return Task.FromResult(ToolResult.Fail(L.F("Mcp.NoScript", name, s.Name)));
        var manifest = ScriptManifest.Parse(script.Body);
        var previous = s.ScriptRuns.LastOrDefault(r => r.ScriptId == script.Id)?.Params;
        var values = manifest.InitialValues(previous);
        if (c.Args["params"] is JsonObject given)
            foreach (var (k, v) in given)
            {
                if (manifest.Params.All(p => p.Name != k)) return Task.FromResult(ToolResult.Fail(L.F("Mcp.UnknownParam", k, script.Name)));
                values[k] = v?.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v?.ToJsonString() ?? "";
            }
        var visible = manifest.Params.Where(p => p.IsVisible(values)).ToDictionary(p => p.Name, p => values[p.Name]);
        if (manifest.Validate(visible) is { } problem) return Task.FromResult(ToolResult.Fail(problem));

        foreach (var old in _jobs.Values.Where(j => j.Finished < DateTime.UtcNow.AddHours(-12)).ToList()) _jobs.TryRemove(old.Id, out _);
        var job = new Job { Id = Guid.NewGuid().ToString("N")[..12], ServerId = s.Id, Script = script.Name };
        _jobs[job.Id] = job;
        var client = c.Session.Client;
        _ = Task.Run(async () =>
        {
            var run = new ScriptRun { ScriptId = script.Id, ScriptName = script.Name, Started = DateTime.Now, Params = ScriptRunRecorder.HistoryParams(manifest, visible) };
            try
            {
                var r = await _host.Scripts.RunAsync(s, script, visible, o => { lock (job.Output) if (job.Output.Length < 2_000_000) job.Output.Append(o); }, CancellationToken.None);
                run.ExitCode = job.ExitCode = r.ExitCode;
                run.Results = r.Results;
            }
            catch (Exception ex)
            {
                run.Error = job.Error = ex.Message;
                job.ExitCode = -1;
            }
            run.Finished = DateTime.Now;
            string output;
            lock (job.Output) output = job.Output.ToString();
            run.OutputTail = string.Join('\n', output.TrimEnd().Split('\n').TakeLast(60));
            try
            {
                ScriptRunRecorder.Record(_host.Vault, s, script, manifest, run, visible);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                job.Error ??= ex.Message;
            }
            job.Results = run.Results;
            job.Finished = DateTime.UtcNow;
            _host.Log.Write(new AgentLogEntry(DateTime.UtcNow, s.Id, s.Name, client, "run_install_script",
                $"{{\"job_id\":\"{job.Id}\",\"finished\":true}}", job.ExitCode == 0 ? AgentOutcome.Ok : AgentOutcome.Failed,
                $"{script.Name}: exit {job.ExitCode}" + (job.Error != null ? " " + job.Error : ""), job.Finished.Value - job.Started));
            if (job.ExitCode == 0) _ = _host.Inventory.RefreshAsync(s.Id);
            _host.Changed?.Invoke(s.Id);
        });
        return Task.FromResult(ToolResult.Data(new { job_id = job.Id, script = script.Name, state = "running", hint = "Call get_job with this job_id." },
            $"job {job.Id}: {script.Name}"));
    }

    private Task<ToolResult> GetJob(ToolCall c)
    {
        var id = c.Required("job_id");
        if (!_jobs.TryGetValue(id, out var job) || job.ServerId != c.S.Id) return Task.FromResult(ToolResult.Fail(L.F("Mcp.NoJob", id)));
        string output;
        lock (job.Output) output = job.Output.ToString();
        var lines = c.Int("lines", 100, 1, 2000);
        var tail = string.Join('\n', ScriptRunner.CleanOutput(output).TrimEnd().Split('\n').TakeLast(lines));
        // values that look like credentials are shown only to agents with full access (they are in SSH Manager anyway)
        var full = c.S.McpAccess == McpAccess.Full;
        var results = job.Results.ToDictionary(r => r.Key, r => full || !Secretish.IsMatch(r.Key) ? r.Value : L.Get("Mcp.SecretHidden"));
        return Task.FromResult(ToolResult.Data(new
        {
            job_id = job.Id, script = job.Script, state = job.State, exit_code = job.ExitCode, error = job.Error,
            seconds = (int)((job.Finished ?? DateTime.UtcNow) - job.Started).TotalSeconds,
            results = job.Finished != null ? results : null,
            output_tail = tail,
        }, $"{job.Script}: {job.State}"));
    }
}
