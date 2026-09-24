using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Scripts;

public sealed record ScriptRunResult(int ExitCode, Dictionary<string, string> Results, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs install scripts. The script and its parameters are uploaded over SFTP to /tmp (never on a command line)
/// and removed when it ends. Two ways to run:
/// in the app (output streamed back, results collected from $SSHM_RESULT) or in a terminal tab (interactive).
/// </summary>
public sealed partial class ScriptRunner(SshClientFactory ssh, SessionLauncher launcher)
{
    /// <summary>Printed after the script's own output; what follows is the content of $SSHM_RESULT.</summary>
    internal const string ResultMarker = "@@sshm:result:7f3c";

    private sealed record Upload(string Script, string Env, string Result, IReadOnlyList<string> Extra)
    {
        /// <summary>rm -f command for everything uploaded.</summary>
        public string Remove => "rm -f " + string.Join(' ', new[] { Script, Env, Result }.Concat(Extra).Select(RemoteShell.Quote));
    }

    // ---------- in the app ----------

    /// <summary>
    /// Runs the script and streams its output (stdout and stderr merged) to <paramref name="onOutput"/>.
    /// Cancelling sends SIGTERM to the remote shell. Call off the UI thread.
    /// </summary>
    public async Task<ScriptRunResult> RunAsync(ServerEntry server, ScriptEntry script, IReadOnlyDictionary<string, string> values,
        Action<string> onOutput, CancellationToken ct)
    {
        using var client = ssh.Connect(server);
        var files = UploadFiles(server, script, values);
        var sudoPassword = script.UseSudo && !RemoteShell.IsRoot(server) && !string.IsNullOrEmpty(server.Password) ? server.Password : null;
        var command = InAppCommand(server, files, script.UseSudo, sudoPassword != null);

        using var cmd = client.CreateCommand(command, Encoding.UTF8);
        cmd.CommandTimeout = Timeout.InfiniteTimeSpan;
        var run = cmd.ExecuteAsync(ct);
        if (sudoPassword != null)
        {
            using var input = cmd.CreateInputStream();
            input.Write(Encoding.UTF8.GetBytes(sudoPassword + "\n"));
        }

        var all = new StringBuilder();
        var reading = Task.Run(() => Pump(cmd.OutputStream, all, onOutput), CancellationToken.None);
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // the remote shell got SIGTERM; still collect what was printed
        }
        await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

        string text;
        lock (all) text = all.ToString();
        var (output, results) = Split(text);
        if (!string.IsNullOrEmpty(cmd.Error)) onOutput(CleanOutput(cmd.Error));
        ct.ThrowIfCancellationRequested();
        return new ScriptRunResult(cmd.ExitStatus ?? -1, results, CleanOutput(output));
    }

    /// <summary>
    /// Copies the command output to <paramref name="all"/> and shows it, except the result block after the marker.
    /// A chunk ending in what could be the start of the marker is held back until the next one arrives.
    /// </summary>
    private static async Task Pump(Stream stream, StringBuilder all, Action<string> onOutput)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var pending = "";
        var inResults = false;
        int n;
        while ((n = await stream.ReadAsync(bytes)) > 0)
        {
            var text = new string(chars, 0, decoder.GetChars(bytes, 0, n, chars, 0));
            lock (all) all.Append(text);
            if (inResults) continue;
            pending += text;
            var marker = pending.IndexOf(ResultMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                if (marker > 0) onOutput(CleanOutput(pending[..marker]));
                inResults = true;
                continue;
            }
            var keep = PartialMarkerLength(pending);
            if (pending.Length > keep)
            {
                onOutput(CleanOutput(pending[..^keep]));
                pending = pending[^keep..];
            }
        }
        if (!inResults && pending.Length > 0) onOutput(CleanOutput(pending));
    }

    /// <summary>Script output and the KEY=value results printed after the marker.</summary>
    internal static (string Output, Dictionary<string, string> Results) Split(string all)
    {
        var i = all.LastIndexOf(ResultMarker, StringComparison.Ordinal);
        if (i < 0) return (all, []);
        return (all[..i], ScriptManifest.ParseResults(all[(i + ResultMarker.Length)..]));
    }

    /// <summary>How many trailing characters could be the beginning of the marker (kept until more arrives).</summary>
    private static int PartialMarkerLength(string s)
    {
        for (var k = Math.Min(ResultMarker.Length - 1, s.Length); k > 0; k--)
            if (ResultMarker.StartsWith(s[^k..], StringComparison.Ordinal)) return k;
        return 0;
    }

    /// <summary>Removes terminal colour / cursor sequences; for "10%\r20%" progress redraws keeps the last state.</summary>
    public static string CleanOutput(string text)
    {
        text = AnsiEscape().Replace(text, "").Replace("\r\n", "\n");
        if (!text.Contains('\r')) return text;
        return string.Join("\n", text.Split('\n').Select(line => line.Split('\r').LastOrDefault(p => p.Length > 0) ?? ""));
    }

    private static string InAppCommand(ServerEntry server, Upload f, bool useSudo, bool sudoWithPassword)
    {
        var inner = $"exec </dev/null; set -a; . {RemoteShell.Quote(f.Env)}; set +a; " +
                    $"B=$(command -v bash || echo sh); \"$B\" {RemoteShell.Quote(f.Script)} 2>&1";
        var run = !useSudo || RemoteShell.IsRoot(server)
            ? "sh -c " + RemoteShell.Quote(inner)
            : (sudoWithPassword ? "sudo -S -p '' sh -c " : "sudo -n sh -c ") + RemoteShell.Quote(inner);
        return $"trap {RemoteShell.Quote(f.Remove)} EXIT; {run} 2>&1; rc=$?; echo; echo '{ResultMarker}'; cat {RemoteShell.Quote(f.Result)} 2>/dev/null; exit $rc";
    }

    // ---------- in a terminal ----------

    /// <summary>Uploads the script (slow, call off the UI thread) and returns the remote command to run it in a terminal.</summary>
    public string Prepare(ServerEntry server, ScriptEntry script, IReadOnlyDictionary<string, string>? values = null)
    {
        var f = UploadFiles(server, script, values ?? new Dictionary<string, string>());
        var inner = $"set -a; . {RemoteShell.Quote(f.Env)}; set +a; B=$(command -v bash || echo sh); \"$B\" {RemoteShell.Quote(f.Script)}";
        var elevate = script.UseSudo && !RemoteShell.IsRoot(server) ? "sudo " : "";
        var rm = f.Remove;
        // results cannot be collected from a terminal; show them instead
        var showResults = $"if [ -s {RemoteShell.Quote(f.Result)} ]; then echo; echo '== results =='; cat {RemoteShell.Quote(f.Result)}; fi";
        return $"trap {RemoteShell.Quote(rm)} EXIT; {elevate}sh -c {RemoteShell.Quote(inner)}; rc=$?; {showResults}; echo; echo \"[exit $rc]\"; exit $rc";
    }

    public void Launch(ServerEntry server, string remoteCommand, string title) =>
        launcher.Launch(server, remoteCommand, title);

    // ---------- shared ----------

    private Upload UploadFiles(ServerEntry server, ScriptEntry script, IReadOnlyDictionary<string, string> values)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var stem = $"/tmp/sshm-{id}";
        var body = script.Body.Replace("\r\n", "\n");
        if (!body.EndsWith('\n')) body += "\n";
        var extra = new List<(string Path, string Text)>();
        var system = new Dictionary<string, string>();
        if (script.Kind == ScriptKind.Compose)
        {
            // the compose file and its .env travel next to a generated deploy script
            extra.Add(($"{stem}.yml", body));
            extra.Add(($"{stem}.dotenv", DotEnv(values)));
            system["SSHM_COMPOSE_FILE"] = $"{stem}.yml";
            system["SSHM_DOTENV"] = $"{stem}.dotenv";
            body = ComposeDeploy(ScriptManifest.ProjectFor(script, ScriptManifest.Parse(script.Body)));
        }
        var f = new Upload($"{stem}.sh", $"{stem}.env", $"{stem}.result", extra.Select(e => e.Path).ToList());
        using var sftp = ssh.ConnectSftp(server);
        Write(sftp, f.Script, body, 700); // SSH.NET reads the digits as octal
        Write(sftp, f.Env, EnvFile(server, values, f.Result, system), 600);
        Write(sftp, f.Result, "", 600); // root (sudo) can append to it too
        foreach (var (path, text) in extra) Write(sftp, path, text, 600);
        return f;
    }

    /// <summary>
    /// Deploys a compose project: installs Docker when missing, puts docker-compose.yml and .env into /opt/&lt;project&gt;,
    /// then pulls and starts it. Running again updates images and applies changed parameters.
    /// </summary>
    internal static string ComposeDeploy(string project) => """
        #!/usr/bin/env bash
        set -Eeuo pipefail
        DIR="/opt/__PROJECT__"
        have_compose(){ command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; }
        if ! have_compose; then
          echo "== Docker is not installed: installing it (get.docker.com) =="
          tmp="$(mktemp)"
          if command -v curl >/dev/null 2>&1; then curl -fsSL https://get.docker.com -o "$tmp"; else wget -qO "$tmp" https://get.docker.com; fi
          sh "$tmp"
          rm -f "$tmp"
          systemctl enable --now docker >/dev/null 2>&1 || service docker start >/dev/null 2>&1 || true
          have_compose || { echo "ERROR: docker compose is still not available" >&2; exit 1; }
        fi
        echo "== Project folder: $DIR =="
        mkdir -p "$DIR"
        install -m 644 "$SSHM_COMPOSE_FILE" "$DIR/docker-compose.yml"
        install -m 600 "$SSHM_DOTENV" "$DIR/.env"
        cd "$DIR"
        docker compose config --quiet
        echo "== docker compose pull =="
        docker compose pull
        echo "== docker compose up -d =="
        docker compose up -d --remove-orphans
        echo
        docker compose ps
        """.Replace("\r\n", "\n").Replace("__PROJECT__", project) + "\n"; // the source file may have CRLF

    /// <summary>Parameters as a compose .env file (single quotes = literal; double quotes when the value has one).</summary>
    internal static string DotEnv(IReadOnlyDictionary<string, string> values)
    {
        var sb = new StringBuilder("# written by SSH Manager\n");
        foreach (var (name, raw) in values)
        {
            if (!ScriptManifest.EnvName().IsMatch(name)) continue;
            var v = raw.Replace("\r", "").Replace("\n", " ");
            sb.Append(name).Append('=')
                .Append(v.Contains('\'') ? "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "$$") + "\"" : "'" + v + "'")
                .Append('\n');
        }
        return sb.ToString();
    }

    private static void Write(Renci.SshNet.SftpClient sftp, string path, string text, short mode)
    {
        using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        sftp.UploadFile(ms, path);
        sftp.ChangePermissions(path, mode);
    }

    /// <summary>Sourced with "set -a": every line becomes an exported variable.</summary>
    /// <param name="system">SSHM_* variables set by the app itself (script parameters cannot override them).</param>
    internal static string EnvFile(ServerEntry server, IReadOnlyDictionary<string, string> values, string resultFile,
        IReadOnlyDictionary<string, string>? system = null)
    {
        var sb = new StringBuilder();
        void Add(string name, string value) => sb.Append(name).Append('=').Append(RemoteShell.Quote(value.Replace("\r", "").Replace("\n", " "))).Append('\n');
        Add("SSHM_SERVER_NAME", server.Name);
        Add("SSHM_HOST", server.Host);
        Add("SSHM_RESULT", resultFile);
        Add("DEBIAN_FRONTEND", "noninteractive");
        foreach (var (name, value) in system ?? new Dictionary<string, string>()) Add(name, value);
        foreach (var (name, value) in values)
            if (ScriptManifest.EnvName().IsMatch(name) && !name.StartsWith("SSHM_", StringComparison.Ordinal)) Add(name, value);
        return sb.ToString();
    }

    /// <summary>Command (with sudo for non-root users) for container / service quick actions.</summary>
    public static string Elevated(ServerEntry server, string command) =>
        RemoteShell.IsRoot(server) ? command : "sudo " + command;

    public static string DockerLogs(ServerEntry s, string container) =>
        Elevated(s, $"docker logs -f --tail 200 {RemoteShell.Quote(container)}");

    public static string ServiceStatus(ServerEntry s, string unit) =>
        Elevated(s, $"systemctl status --no-pager -l {RemoteShell.Quote(unit)}");

    public static string ServiceRestart(ServerEntry s, string unit) =>
        Elevated(s, $"systemctl restart {RemoteShell.Quote(unit)}") + $" && systemctl status --no-pager {RemoteShell.Quote(unit)}";

    public static string ServiceLogs(ServerEntry s, string unit) =>
        Elevated(s, $"journalctl -u {RemoteShell.Quote(unit)} -n 200 -f --no-pager");

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiEscape();
}
