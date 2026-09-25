using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SshManager.Core.Mcp;

/// <summary>
/// MCP Streamable HTTP on 127.0.0.1 only, without SSE: POST one JSON-RPC message, get application/json back
/// (202 for notifications). Needs "Authorization: Bearer &lt;token&gt;"; requests with a foreign Host or a web page's
/// Origin are refused (DNS rebinding). initialize starts a session (Mcp-Session-Id), DELETE ends it.
/// </summary>
public sealed class McpHttpServer(Func<string, McpSession, Task<string?>> handle, Func<string> token) : IDisposable
{
    private const int MaxBody = 16 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new();
    private HttpListener? _http;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _http is { IsListening: true };
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <exception cref="HttpListenerException">The port is taken.</exception>
    public void Start(int port)
    {
        Stop();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _http = http;
        Port = port;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(http, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            _http?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _http = null;
    }

    private async Task AcceptLoop(HttpListener http, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && http.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await http.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var status = Check(req);
            if (status != 200)
            {
                if (status == 401) res.AddHeader("WWW-Authenticate", "Bearer");
                await Reply(res, status, $"{{\"error\":\"{(status == 401 ? "unauthorized" : status == 404 ? "not found" : "forbidden")}\"}}");
                return;
            }
            var sessionId = req.Headers["Mcp-Session-Id"];
            if (req.HttpMethod == "DELETE")
            {
                if (sessionId != null) _sessions.TryRemove(sessionId, out _);
                await Reply(res, 200, "");
                return;
            }
            if (req.HttpMethod != "POST")
            {
                res.AddHeader("Allow", "POST, DELETE");
                await Reply(res, 405, "");
                return;
            }
            if (req.ContentLength64 > MaxBody)
            {
                await Reply(res, 413, "");
                return;
            }
            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();

            McpSession? session;
            if (sessionId == null)
            {
                // initialize starts a session; anything else without a session id gets a throwaway one
                var isInit = TryMethod(body) == "initialize";
                var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                session = new McpSession(id);
                if (isInit)
                {
                    _sessions[id] = session;
                    res.AddHeader("Mcp-Session-Id", id);
                }
            }
            else if (!_sessions.TryGetValue(sessionId, out session))
            {
                await Reply(res, 404, "{\"error\":\"unknown session\"}"); // the client starts a new one
                return;
            }
            foreach (var (id, s) in _sessions)
                if (DateTime.UtcNow - s.LastUsed > TimeSpan.FromDays(1)) _sessions.TryRemove(id, out _);
            var answer = await handle(body, session);
            if (answer == null) await Reply(res, 202, "");
            else await Reply(res, 200, answer);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            // the client went away
        }
    }

    private static string? TryMethod(string body)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["method"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>200, or the status that refuses the request.</summary>
    private int Check(HttpListenerRequest req)
    {
        if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") return 404;
        var host = req.Headers["Host"] ?? "";
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") return 403;
        var origin = req.Headers["Origin"];
        if (origin != null && origin != $"http://127.0.0.1:{Port}" && origin != $"http://localhost:{Port}") return 403;
        var auth = Encoding.UTF8.GetBytes(req.Headers["Authorization"] ?? "");
        var expected = Encoding.UTF8.GetBytes("Bearer " + token());
        return CryptographicOperations.FixedTimeEquals(auth, expected) ? 200 : 401;
    }

    private static async Task Reply(HttpListenerResponse res, int status, string body)
    {
        res.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(body);
        if (bytes.Length > 0) res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose() => Stop();
}
