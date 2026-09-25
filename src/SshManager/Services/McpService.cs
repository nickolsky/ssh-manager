using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Threading;
using SshManager.Core;
using SshManager.Core.Mcp;
using SshManager.Core.Models;
using SshManager.Views;

namespace SshManager.Services;

/// <summary>
/// The app side of MCP: the tools run on the app's services; sessions come through the control pipe
/// (sshm.exe mcp, stdio) or through HTTP on 127.0.0.1 (when turned on in the settings).
/// </summary>
public sealed class McpService : IDisposable
{
    private readonly AppHost _host;
    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new();
    private readonly McpHttpServer _http;

    public McpService(AppHost host, Dispatcher ui)
    {
        _host = host;
        _ui = ui;
        Log = new AgentLog(null, () =>
        {
            var s = Settings;
            return (Math.Max(1, s.LogMaxMb) * 1024L * 1024, TimeSpan.FromDays(Math.Max(1, s.LogRetentionDays)));
        });
        Server = new McpServer(new McpHost
        {
            Vault = host.Vault,
            Ssh = host.Ssh,
            Inventory = host.Inventory,
            Metrics = host.Metrics,
            Scripts = host.Scripts,
            Health = host.Health,
            Uptime = host.Uptime,
            Log = Log,
            Settings = () => Settings,
            EnsureUnlocked = () => host.EnsureUnlockedAsync(fromAgent: true),
            Confirm = ConfirmAsync,
            Reboot = id => host.RebootAsync(id, interactive: false),
            Changed = id => host.Health.CheckNow(id),
        });
        _http = new McpHttpServer((body, session) => Server.HandleAsync(body, session), Token);
    }

    public McpServer Server { get; }
    public AgentLog Log { get; }

    private McpSettings Settings => _host.SettingsStore.Settings.Mcp;

    /// <summary>The HTTP endpoint while it runs, else null.</summary>
    public string? HttpUrl => _http.IsRunning ? _http.Url : null;

    /// <summary>Why HTTP could not start (port taken…), else null.</summary>
    public string? HttpError { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>A message from sshm.exe mcp (stdio): the session is the sshm process.</summary>
    public Task<string?> HandleAsync(string sessionId, string payload)
    {
        var session = _sessions.GetOrAdd(sessionId, id => new McpSession(id));
        foreach (var (id, s) in _sessions)
            if (DateTime.UtcNow - s.LastUsed > TimeSpan.FromDays(1)) _sessions.TryRemove(id, out _);
        return Server.HandleAsync(payload, session);
    }

    /// <summary>Shows <see cref="AgentConfirmWindow"/> on the UI thread; false when declined or not answered in time.</summary>
    private Task<bool> ConfirmAsync(string title, string text) =>
        _ui.InvokeAsync(() =>
        {
            var w = new AgentConfirmWindow(title, text);
            w.ShowDialog();
            return w.Allowed;
        }).Task;

    /// <summary>The token HTTP clients must send (made the first time it is needed).</summary>
    public string Token()
    {
        if (string.IsNullOrEmpty(Settings.HttpToken))
        {
            Settings.HttpToken = NewToken();
            _host.SettingsStore.Save();
        }
        return Settings.HttpToken!;
    }

    public void RegenerateToken()
    {
        Settings.HttpToken = NewToken();
        _host.SettingsStore.Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public int HttpPort => Settings.HttpPort is > 0 and < 65536 ? Settings.HttpPort : McpSettings.DefaultPort;

    /// <summary>Starts or stops the HTTP endpoint to match the settings.</summary>
    public void ApplySettings()
    {
        var want = Settings.Enabled && Settings.HttpEnabled;
        if (want == _http.IsRunning && (!want || _http.Port == HttpPort)) return;
        _http.Stop();
        HttpError = null;
        if (want)
        {
            try
            {
                Token();
                _http.Start(HttpPort);
            }
            catch (HttpListenerException ex)
            {
                HttpError = L.F("Mcp.HttpFailed", HttpPort, ex.Message);
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- connection help for the settings page ----------

    /// <summary>The sshm.exe next to the app (what Claude Code / Codex start).</summary>
    public static string HelperPath => AppPaths.HelperExe;

    public static string ClaudeCommand => $"claude mcp add --scope user sshmanager -- \"{HelperPath}\" mcp";

    public static string CodexConfig => $"[mcp_servers.sshmanager]\ncommand = '{HelperPath}'\nargs = [\"mcp\"]\ntool_timeout_sec = 900";

    public string ClaudeHttpCommand() =>
        $"claude mcp add --scope user --transport http sshmanager {"http://127.0.0.1:" + HttpPort + "/mcp"} --header \"Authorization: Bearer {Token()}\"";

    public void Dispose() => _http.Dispose();
}
