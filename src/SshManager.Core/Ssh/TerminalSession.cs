using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshManager.Core.Models;
using SshManager.Core.Terminal;

namespace SshManager.Core.Ssh;

/// <summary>
/// An interactive shell for the built-in terminal: SSH.NET with a remote pty (xterm-256color).
/// Output arrives on a background thread as decoded text; input and resize go straight to the channel.
/// With <see cref="ShellIntegration"/> the shell reports its prompt and directory; loading it is hidden: output
/// is held until the first prompt, the source line is typed there and everything up to the loaded mark is cut.
/// </summary>
public sealed class TerminalSession(SshClientFactory ssh, ServerEntry server, int keepAliveSeconds = 30, bool integrate = true)
    : IDisposable
{
    private static readonly TimeSpan PromptQuiet = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(3);

    private enum Phase { Direct, WaitPrompt, WaitLoaded }

    private readonly object _sync = new();
    private readonly object _out = new();
    private SshClient? _client;
    private ShellStream? _stream;
    private string? _error;
    private bool _disposed;

    private Phase _phase = Phase.Direct;
    private readonly StringBuilder _before = new();
    private readonly StringBuilder _after = new();
    private DateTime _started, _lastOutput, _typed;
    private string? _sourceLine;
    private string? _command;
    private Timer? _timer;

    public ServerEntry Server { get; } = server;

    /// <summary>Text from the server (UTF-8 decoded, may end in the middle of an escape sequence).</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended: null when the remote side closed it (exit), otherwise the error.</summary>
    public event Action<string?>? Closed;

    public bool IsOpen => _stream != null;

    /// <summary>The login shell (bash, zsh, …) when known.</summary>
    public string? Shell { get; private set; }

    /// <summary>The integration script loaded: prompt marks, cwd and "edit" are available.</summary>
    public bool Integrated { get; private set; }

    /// <summary>Connects and starts the shell. Slow (network, maybe a host key prompt): call off the UI thread.</summary>
    /// <param name="command">Typed into the shell once it starts (quick actions, scripts in a terminal).</param>
    public void Open(int cols, int rows, string? command = null)
    {
        var client = ssh.Connect(Server);
        if (keepAliveSeconds > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds);
        string? file = null;
        if (integrate)
        {
            try
            {
                var r = RemoteShell.Run(client, Server, ShellIntegration.SetupCommand(), elevated: false, TimeSpan.FromSeconds(10));
                (Shell, file) = r.Ok ? ShellIntegration.ParseSetup(r.Output) : (null, null);
            }
            catch (Exception ex) when (ex is SshException or TimeoutException or IOException)
            {
                // no integration, a plain terminal
            }
        }
        var modes = new Dictionary<TerminalModes, uint>
        {
            [TerminalModes.ECHO] = 1,
            [TerminalModes.TTY_OP_ISPEED] = 115200,
            [TerminalModes.TTY_OP_OSPEED] = 115200,
        };
        var stream = client.CreateShellStream("xterm-256color", (uint)Math.Max(10, cols), (uint)Math.Max(2, rows), 0, 0, 1 << 16, modes);
        stream.ErrorOccurred += (_, e) => _error ??= e.Exception.Message;
        lock (_sync)
        {
            if (_disposed)
            {
                stream.Dispose();
                client.Dispose();
                throw new ObjectDisposedException(nameof(TerminalSession));
            }
            _client = client;
            _stream = stream;
            _error = null;
        }
        _command = string.IsNullOrWhiteSpace(command) ? null : command.TrimEnd() + "\n";
        if (file != null && ShellIntegration.Supports(Shell))
        {
            _sourceLine = ShellIntegration.SourceLine(file);
            _started = _lastOutput = DateTime.UtcNow;
            _phase = Phase.WaitPrompt;
            _timer = new Timer(_ => Tick(), null, 100, 100);
        }
        new Thread(() => Pump(stream)) { IsBackground = true, Name = "sshm-terminal " + Server.Name }.Start();
        if (_phase == Phase.Direct) SendCommand();
    }

    /// <summary>Runs a helper command over the same connection (suggestions: history, directory listings).</summary>
    public ShellResult Run(string script, TimeSpan timeout)
    {
        var client = _client ?? throw new InvalidOperationException(L.Get("Term.ConnectionLost"));
        return RemoteShell.Run(client, Server, script, elevated: false, timeout);
    }

    private void SendCommand()
    {
        var c = _command;
        _command = null;
        if (c != null) Send(c);
    }

    private void Emit(string text)
    {
        lock (_out)
        {
            switch (_phase)
            {
                case Phase.WaitPrompt:
                    _before.Append(text);
                    _lastOutput = DateTime.UtcNow;
                    return;
                case Phase.WaitLoaded:
                    _after.Append(text);
                    var after = _after.ToString();
                    if (!after.Contains(ShellIntegration.LoadedMark, StringComparison.Ordinal)) return;
                    Integrated = true;
                    Finish(ShellIntegration.Splice(_before.ToString(), after));
                    return;
                default:
                    Output?.Invoke(text);
                    return;
            }
        }
    }

    /// <summary>Timeouts of the hidden start: type the source line at a quiet prompt, give up when it never loads.</summary>
    private void Tick()
    {
        lock (_out)
        {
            var now = DateTime.UtcNow;
            if (_phase == Phase.WaitPrompt)
            {
                if (_before.Length > 0 && now - _lastOutput >= PromptQuiet && ShellIntegration.LooksLikePrompt(_before.ToString()))
                {
                    _phase = Phase.WaitLoaded;
                    _typed = now;
                    Send(_sourceLine!);
                }
                else if (now - _started >= PromptWait) Finish(_before.ToString());
            }
            else if (_phase == Phase.WaitLoaded && now - _typed >= LoadWait)
            {
                Finish(_before.ToString() + _after);
            }
        }
    }

    private void Finish(string text)
    {
        _phase = Phase.Direct;
        _timer?.Dispose();
        _timer = null;
        _before.Clear();
        _after.Clear();
        if (text.Length > 0) Output?.Invoke(text);
        SendCommand();
    }

    private void Pump(ShellStream stream)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[16384];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int n;
            while ((n = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                var c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) Emit(new string(chars, 0, c));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            _error ??= ex is ObjectDisposedException ? null : ex.Message;
        }
        lock (_out)
        {
            // the shell exited during the hidden start: show what it printed
            if (_phase != Phase.Direct) Finish(_before.ToString() + _after);
        }
        string? error;
        bool wasDisposed;
        lock (_sync)
        {
            wasDisposed = _disposed;
            // the channel closed while the connection is alive = the shell exited normally
            error = _error ?? (_client is { IsConnected: false } ? L.Get("Term.ConnectionLost") : null);
            CloseLocked();
        }
        if (!wasDisposed) Closed?.Invoke(error);
    }

    public void Send(string text)
    {
        var stream = _stream;
        if (stream == null || text.Length == 0) return;
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            _error ??= ex.Message;
        }
    }

    public void Resize(int cols, int rows)
    {
        try
        {
            _stream?.ChangeWindowSize((uint)Math.Max(10, cols), (uint)Math.Max(2, rows), 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException)
        {
        }
    }

    private void CloseLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CloseLocked();
        }
    }
}
