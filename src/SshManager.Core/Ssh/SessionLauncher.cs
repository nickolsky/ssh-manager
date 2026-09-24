using System.Collections.Concurrent;
using System.Diagnostics;
using SshManager.Core.Agent;
using SshManager.Core.Crypto;
using SshManager.Core.Models;
using SshManager.Core.Pipes;
using SshManager.Core.Storage;

namespace SshManager.Core.Ssh;

/// <summary>
/// Builds ssh.exe invocations and opens them in Windows Terminal (or a console window).
/// Secrets never appear on command lines: the terminal runs "sshm.exe tab &lt;token&gt;", which fetches
/// the real command from this process, and passwords are handed to ssh via SSH_ASKPASS on request.
/// </summary>
public sealed class SessionLauncher(VaultService vault, SettingsService settings, SshAgentServer agent, KnownHostsService knownHosts)
{
    private static readonly TimeSpan LaunchTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AskPassTtl = TimeSpan.FromMinutes(10);
    private const int MaxPasswordAnswers = 2;

    private readonly ConcurrentDictionary<string, (LaunchSpec Spec, DateTime Expires)> _launches = new();
    private readonly ConcurrentDictionary<string, AskPassTicket> _askPass = new();

    private sealed class AskPassTicket
    {
        public Guid ServerId;
        public DateTime Expires;
        public int Answers;
    }

    public string SshPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(settings.Settings.SshPath)) return settings.Settings.SshPath!;
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            return File.Exists(system) ? system : "ssh.exe";
        }
    }

    public static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "wt.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public LaunchSpec BuildSpec(ServerEntry server, bool pauseOnError)
    {
        var s = settings.Settings;
        var args = new List<string>
        {
            "-p", server.Port.ToString(),
            "-o", "UserKnownHostsFile=" + AppPaths.ForSsh(knownHosts.FilePath),
        };
        if (s.ServerAliveInterval > 0) args.AddRange(["-o", $"ServerAliveInterval={s.ServerAliveInterval}"]);
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) args.AddRange(["-J", server.JumpHost.Trim()]);

        var env = new Dictionary<string, string>();
        if (agent.PipePath != null) env["SSH_AUTH_SOCK"] = agent.PipePath;

        if (server.Auth == AuthMode.Key)
        {
            var key = server.KeyId is { } id ? vault.Data.Keys.FirstOrDefault(k => k.Id == id) : null;
            if (key == null) throw new InvalidOperationException($"У сервера «{server.Name}» не выбран ключ");
            args.AddRange(["-o", "IdentitiesOnly=yes", "-i", AppPaths.ForSsh(KeyService.EnsurePublicKeyFile(key))]);
        }
        else
        {
            if (string.IsNullOrEmpty(server.Password))
                throw new InvalidOperationException($"У сервера «{server.Name}» не сохранён пароль");
            var token = NewToken();
            _askPass[token] = new AskPassTicket { ServerId = server.Id, Expires = DateTime.UtcNow + AskPassTtl };
            env["SSH_ASKPASS"] = AppPaths.HelperExe;
            env["SSH_ASKPASS_REQUIRE"] = "force";
            env["SSHM_ASKPASS_TOKEN"] = token;
            args.AddRange([
                "-o", "PubkeyAuthentication=no",
                "-o", "PreferredAuthentications=keyboard-interactive,password",
                "-o", "NumberOfPasswordPrompts=1",
            ]);
        }

        args.AddRange(SplitArgs(server.ExtraArgs));
        args.AddRange(["-l", server.Username, server.Host]);

        return new LaunchSpec
        {
            SshPath = SshPath,
            Args = args,
            Env = env,
            Title = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name,
            PauseOnError = pauseOnError,
        };
    }

    /// <summary>Opens a session in a new terminal tab/window.</summary>
    public void Launch(ServerEntry server)
    {
        var mode = settings.Settings.Terminal;
        var wt = mode == TerminalMode.ConsoleWindow ? null : FindWindowsTerminal();
        var spec = BuildSpec(server, pauseOnError: wt == null);
        var token = NewToken();
        _launches[token] = (spec, DateTime.UtcNow + LaunchTtl);
        Cleanup();

        ProcessStartInfo psi;
        if (wt != null)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(mode == TerminalMode.WindowsTerminalWindow ? "new" : "0");
            psi.ArgumentList.Add("new-tab");
            if (!string.IsNullOrWhiteSpace(settings.Settings.WindowsTerminalProfile))
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(settings.Settings.WindowsTerminalProfile!);
            }
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(spec.Title.Replace(';', ','));
            psi.ArgumentList.Add("--suppressApplicationTitle");
            psi.ArgumentList.Add(AppPaths.HelperExe);
        }
        else
        {
            // GUI parent without a console: the console helper gets its own new console window.
            psi = new ProcessStartInfo(AppPaths.HelperExe) { UseShellExecute = false };
        }
        psi.ArgumentList.Add("tab");
        psi.ArgumentList.Add(token);
        Process.Start(psi)?.Dispose();

        vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
            if (s != null) s.LastConnected = DateTime.Now;
        });
    }

    public LaunchSpec? TakeLaunch(string token)
    {
        Cleanup();
        return _launches.TryRemove(token, out var e) && e.Expires > DateTime.UtcNow ? e.Spec : null;
    }

    /// <summary>Returns the stored password for an askpass request, or null if the ticket is invalid/used up.</summary>
    public string? AnswerAskPass(string token)
    {
        if (!_askPass.TryGetValue(token, out var t) || t.Expires < DateTime.UtcNow) return null;
        if (Interlocked.Increment(ref t.Answers) > MaxPasswordAnswers) return null;
        return vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == t.ServerId)?.Password, out var pw) ? pw : null;
    }

    /// <summary>Plain command for copy/paste (no secrets).</summary>
    public string CommandLine(ServerEntry server)
    {
        var parts = new List<string> { "ssh" };
        if (server.Port != 22) parts.AddRange(["-p", server.Port.ToString()]);
        if (!string.IsNullOrWhiteSpace(server.JumpHost)) parts.AddRange(["-J", server.JumpHost.Trim()]);
        if (!string.IsNullOrWhiteSpace(server.ExtraArgs)) parts.Add(server.ExtraArgs.Trim());
        parts.Add($"{server.Username}@{server.Host}");
        return string.Join(' ', parts);
    }

    private void Cleanup()
    {
        var now = DateTime.UtcNow;
        foreach (var (k, v) in _launches)
            if (v.Expires < now) _launches.TryRemove(k, out _);
        foreach (var (k, v) in _askPass)
            if (v.Expires < now) _askPass.TryRemove(k, out _);
    }

    private static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static IEnumerable<string> SplitArgs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var cur = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in s)
        {
            if (quote != null)
            {
                if (c == quote) quote = null;
                else cur.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) yield return cur.ToString();
                cur.Clear();
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }
}
