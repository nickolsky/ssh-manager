using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Core.Mcp;

/// <summary>
/// What the MCP tools need from the app: the real services there, the same Core services with stubs in tests.
/// UI parts (unlocking, confirmations, the reboot with its notice) come as callbacks.
/// </summary>
public sealed class McpHost
{
    public required VaultService Vault { get; init; }
    public required SshClientFactory Ssh { get; init; }
    public required ServerInventoryService Inventory { get; init; }
    public required MetricsCollector Metrics { get; init; }
    public required ScriptRunner Scripts { get; init; }
    public required AgentLog Log { get; init; }
    public required Func<McpSettings> Settings { get; init; }
    public HealthMonitor? Health { get; init; }
    public UptimeLog? Uptime { get; init; }
    /// <summary>Shows the unlock window when the vault is locked; false = still locked.</summary>
    public Func<Task<bool>> EnsureUnlocked { get; init; } = () => Task.FromResult(true);
    /// <summary>Asks the user in the app (title, text); false = refused or no answer in time.</summary>
    public Func<string, string, Task<bool>> Confirm { get; init; } = (_, _) => Task.FromResult(true);
    /// <summary>Reboots a server (the app also waits for it and tells the user when it is back).</summary>
    public Func<Guid, Task>? Reboot { get; init; }
    /// <summary>After a change on a server (inventory refreshed, script run): e.g. re-check its health.</summary>
    public Action<Guid>? Changed { get; init; }
}

/// <summary>One MCP client connection: an sshm.exe process (stdio) or an HTTP session.</summary>
public sealed class McpSession(string id)
{
    public string Id { get; } = id;
    /// <summary>clientInfo.name from initialize ("claude-code", "codex-mcp-client"…), shown in the agent log.</summary>
    public string Client { get; set; } = "agent";
    public string? ProtocolVersion { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}

/// <summary>A JSON-RPC error (unknown method, bad parameters).</summary>
public sealed class McpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// The MCP server (JSON-RPC 2.0 over any transport): initialize, ping, tools/list, tools/call. Tools run here, in the
/// app, with the vault's credentials; every call is checked against the server's access level and logged.
/// </summary>
public sealed partial class McpServer
{
    /// <summary>Protocol versions understood, newest first.</summary>
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    /// <summary>Tool output and log lines: readable Cyrillic and &lt;&gt; instead of \uXXXX (it is text for an agent, not HTML).</summary>
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static readonly JsonSerializerOptions JsonLine = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly McpHost _host;
    private readonly IReadOnlyList<McpTool> _tools;

    public McpServer(McpHost host)
    {
        _host = host;
        _tools = BuildTools();
    }

    public McpHost Host => _host;
    internal IReadOnlyList<McpTool> Tools => _tools;

    /// <summary>Handles one JSON-RPC message; the response, or null for notifications and client responses.</summary>
    public async Task<string?> HandleAsync(string message, McpSession session, CancellationToken ct = default)
    {
        session.LastUsed = DateTime.UtcNow;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }
        if (node is JsonArray) return Error(null, -32600, "Batches are not supported");
        if (node is not JsonObject msg) return Error(null, -32600, "Invalid request");
        var id = msg["id"]?.DeepClone();
        var isNotification = !msg.ContainsKey("id");
        if (msg["method"]?.GetValueKind() != JsonValueKind.String) return null; // a response to us, or junk without an id
        var method = msg["method"]!.GetValue<string>();
        var parameters = msg["params"] as JsonObject;
        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters, session),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "tools/call" => await CallAsync(parameters, session, ct),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new McpException(-32601, "Method not found: " + method),
            };
            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() }.ToJsonString();
        }
        catch (McpException e)
        {
            return isNotification ? null : Error(id, e.Code, e.Message);
        }
    }

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString();

    private static JsonObject Initialize(JsonObject? p, McpSession session)
    {
        var wanted = p?["protocolVersion"]?.GetValueKind() == JsonValueKind.String ? p["protocolVersion"]!.GetValue<string>() : null;
        session.ProtocolVersion = wanted != null && Versions.Contains(wanted) ? wanted : Versions[0];
        if (p?["clientInfo"]?["name"]?.GetValueKind() == JsonValueKind.String) session.Client = p["clientInfo"]!["name"]!.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = session.ProtocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "sshmanager", ["title"] = "SSH Manager", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            },
            ["instructions"] =
                "Manages the SSH servers saved in SSH Manager on this PC. Call list_servers first: it shows the servers " +
                "available to you and what each allows (read_only < reboot < limited_write < full). Tools take the server by " +
                "name or host. Reboots and deletions may wait for the user to confirm in SSH Manager. Everything you do is logged.",
        };
    }

    /// <summary>Tools up to the highest level any server allows (all of them while the vault is locked: a call unlocks it).</summary>
    private JsonObject ToolsList()
    {
        var top = McpAccess.Full;
        if (_host.Vault.TryRead(d => d.Servers.Select(s => s.McpAccess).DefaultIfEmpty(McpAccess.Off).Max(), out var max)) top = max;
        if (!_host.Settings().Enabled) top = McpAccess.Off;
        var list = new JsonArray();
        foreach (var t in _tools.Where(t => t.Level == McpAccess.Off || McpPolicy.Allows(top, t.Level)))
            list.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["description"] = t.Description + (t.Level > McpAccess.ReadOnly ? $" Needs the server's access level {LevelName(t.Level)} or higher." : ""),
                ["inputSchema"] = t.Schema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = t.ReadOnly, ["destructiveHint"] = t.Destructive, ["openWorldHint"] = false,
                },
            });
        return new JsonObject { ["tools"] = list };
    }

    public static string LevelName(McpAccess a) => a switch
    {
        McpAccess.ReadOnly => "read_only",
        McpAccess.Reboot => "reboot",
        McpAccess.LimitedWrite => "limited_write",
        McpAccess.Full => "full",
        _ => "off",
    };

    private async Task<JsonObject> CallAsync(JsonObject? p, McpSession session, CancellationToken ct)
    {
        var name = p?["name"]?.GetValueKind() == JsonValueKind.String ? p["name"]!.GetValue<string>() : "";
        var tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new McpException(-32602, "Unknown tool: " + name);
        var args = p?["arguments"] as JsonObject ?? [];
        var sw = Stopwatch.StartNew();
        var call = new ToolCall(this, session, args, ct);

        ToolResult Deny(string why, ServerEntry? s = null)
        {
            Log(call, s, tool, AgentOutcome.Denied, why, sw.Elapsed);
            return ToolResult.Fail(why);
        }

        ServerEntry? target = null;
        var why = "";
        if (!_host.Settings().Enabled) return Deny(L.Get("Mcp.Disabled")).ToJson();
        if (!await _host.EnsureUnlocked()) return Deny(L.Get("Mcp.Locked")).ToJson();
        if (tool.PerServer && (target = FindServer(call.Str("server"), out why)) == null) return Deny(why).ToJson();
        if (target != null && !McpPolicy.Allows(target.McpAccess, tool.Level))
            return Deny(L.F("Mcp.LevelTooLow", target.Name, LevelName(target.McpAccess), tool.Name, LevelName(tool.Level)), target).ToJson();

        call.Server = target;
        ToolResult result;
        try
        {
            result = await tool.Run(call);
        }
        catch (McpDeniedException d)
        {
            return Deny(d.Message, target).ToJson();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = ToolResult.Fail(ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message);
        }
        Log(call, target, tool, result.IsError ? AgentOutcome.Failed : AgentOutcome.Ok, result.LogText ?? result.Text, sw.Elapsed);
        return result.ToJson();
    }

    private void Log(ToolCall call, ServerEntry? server, McpTool tool, AgentOutcome outcome, string text, TimeSpan duration) =>
        _host.Log.Write(new AgentLogEntry(DateTime.UtcNow, server?.Id, server?.Name ?? "", call.Session.Client, tool.Name,
            call.ArgsForLog(tool), outcome, text, duration));

    /// <summary>
    /// A server available to agents, by name or host (exact first, then a unique part of the name). Servers with
    /// access "off" are treated as missing, so an agent cannot even learn they exist.
    /// </summary>
    internal ServerEntry? FindServer(string name, out string error)
    {
        error = "";
        var servers = _host.Vault.Read(d => d.Servers.Where(s => s.McpAccess != McpAccess.Off).Select(s => s.Clone()).ToList());
        if (string.IsNullOrWhiteSpace(name))
        {
            error = L.Get("Mcp.NeedServer");
            return null;
        }
        var exact = servers.Where(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase) ||
                                       string.Equals(s.Host, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = exact.Count > 1 ? exact : servers.Where(s => s.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        error = partial.Count == 0 ? L.F("Mcp.ServerNotFound", name) : L.F("Mcp.ServerAmbiguous", name, string.Join(", ", partial.Select(s => s.Name)));
        return null;
    }
}

/// <summary>A refusal found while running a tool (a local folder outside the list, a declined confirmation).</summary>
public sealed class McpDeniedException(string message) : Exception(message);

internal sealed record McpTool(
    string Name, string Title, string Description, McpAccess Level, JsonObject Schema, Func<ToolCall, Task<ToolResult>> Run,
    bool ReadOnly = true, bool Destructive = false, bool PerServer = true, string[]? HiddenArgs = null);

internal sealed record ToolResult(string Text, bool IsError = false, string? LogText = null)
{
    public static ToolResult Fail(string text) => new(text, IsError: true);

    /// <summary>Pretty JSON as text: every MCP client shows it, agents read it well.</summary>
    public static ToolResult Data(object value, string? log = null) => new(JsonSerializer.Serialize(value, McpServer.Json), LogText: log);

    public JsonObject ToJson() => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = Text }),
        ["isError"] = IsError,
    };
}

/// <summary>The arguments of one call with typed getters, and the resolved server.</summary>
internal sealed class ToolCall(McpServer server, McpSession session, JsonObject args, CancellationToken ct)
{
    public McpServer Mcp { get; } = server;
    public McpHost Host => Mcp.Host;
    public McpSession Session { get; } = session;
    public JsonObject Args { get; } = args;
    public CancellationToken Ct { get; } = ct;
    public ServerEntry? Server { get; set; }
    public ServerEntry S => Server ?? throw new InvalidOperationException("no server");

    public string Str(string name, string? fallback = null)
    {
        var v = Args[name];
        return v?.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => fallback ?? "",
        };
    }

    public string Required(string name) =>
        Str(name) is { Length: > 0 } v ? v : throw new McpException(-32602, $"Missing argument: {name}");

    public int Int(string name, int fallback, int min, int max)
    {
        var v = Args[name];
        var n = v?.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.String when double.TryParse(v.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => fallback,
        };
        return (int)Math.Clamp(n, min, max);
    }

    public bool Bool(string name, bool fallback = false) => Args[name]?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => Args[name]!.GetValue<string>() is "true" or "1" or "yes",
        _ => fallback,
    };

    /// <summary>The arguments for the log: file contents and other bulky or secret ones replaced by their size.</summary>
    public string ArgsForLog(McpTool tool)
    {
        var copy = (JsonObject)Args.DeepClone();
        foreach (var hidden in tool.HiddenArgs ?? [])
            if (copy[hidden] is { } v) copy[hidden] = $"<{v.ToJsonString().Length} chars>";
        copy.Remove("server");
        return copy.Count == 0 ? "" : copy.ToJsonString(McpServer.JsonLine);
    }
}
