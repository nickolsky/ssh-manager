using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshManager.Core.Models;

namespace SshManager.Core.Ssh;

/// <summary>
/// An interactive shell for the built-in terminal: SSH.NET with a remote pty (xterm-256color).
/// Output arrives on a background thread as decoded text; input and resize go straight to the channel.
/// </summary>
public sealed class TerminalSession(SshClientFactory ssh, ServerEntry server, int keepAliveSeconds = 30) : IDisposable
{
    private readonly object _sync = new();
    private SshClient? _client;
    private ShellStream? _stream;
    private string? _error;
    private bool _disposed;

    public ServerEntry Server { get; } = server;

    /// <summary>Text from the server (UTF-8 decoded, may end in the middle of an escape sequence).</summary>
    public event Action<string>? Output;

    /// <summary>The shell ended: null when the remote side closed it (exit), otherwise the error.</summary>
    public event Action<string?>? Closed;

    public bool IsOpen => _stream != null;

    /// <summary>Connects and starts the shell. Slow (network, maybe a host key prompt): call off the UI thread.</summary>
    /// <param name="command">Typed into the shell once it starts (quick actions, scripts in a terminal).</param>
    public void Open(int cols, int rows, string? command = null)
    {
        var client = ssh.Connect(Server);
        if (keepAliveSeconds > 0) client.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds);
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
        new Thread(() => Pump(stream)) { IsBackground = true, Name = "sshm-terminal " + Server.Name }.Start();
        if (!string.IsNullOrWhiteSpace(command)) Send(command.TrimEnd() + "\n");
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
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            _error ??= ex is ObjectDisposedException ? null : ex.Message;
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
