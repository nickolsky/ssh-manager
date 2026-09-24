using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SshManager.Core.Ssh;

namespace SshManager.Views.Controls;

/// <summary>
/// One built-in terminal: xterm.js in WebView2 (Assets\terminal) connected to a <see cref="TerminalSession"/>.
/// Output is batched every few milliseconds so a flood (cat of a big file) does not stall the UI thread.
/// </summary>
public sealed class TerminalView : Border, IDisposable
{
    private const string HostName = "sshm-terminal.local";
    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebView2 _web = new();
    private readonly Func<TerminalSession> _createSession;
    private readonly string? _command;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _flush;
    private TerminalSession? _session;
    private bool _ready;
    private bool _connecting;
    private bool _disposed;
    private int _cols = 120, _rows = 30;

    /// <param name="createSession">New session for this tab (also used to reconnect).</param>
    /// <param name="command">Typed into the shell after connecting (quick actions).</param>
    public TerminalView(Func<TerminalSession> createSession, string? command)
    {
        _createSession = createSession;
        _command = command;
        Child = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        _flush = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        _flush.Tick += (_, _) => Flush();
        Loaded += OnLoaded;
    }

    /// <summary>Connected, connecting, closed (for the tab's dot).</summary>
    public event Action<TerminalState>? StateChanged;
    /// <summary>Title set by the remote shell (OSC 0/2).</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+Shift+W, Ctrl+Shift+T from inside the terminal.</summary>
    public event Action<string>? KeyCommand;

    public TerminalState State { get; private set; } = TerminalState.Connecting;

    public new void Focus()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    private static Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SshManager", "WebView2"));

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _web.EnsureCoreWebView2Async(await Environment());
        }
        catch (Exception ex)
        {
            Child = new TextBlock { Text = L.F("Term.WebViewMissing", ex.Message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
            SetState(TerminalState.Closed);
            return;
        }
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = Debugger.IsAttached;
        s.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+R reload, Ctrl+F find, Ctrl+P print
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"),
            CoreWebView2HostResourceAccessKind.Deny);
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.NavigationStarting += (_, a) =>
        {
            if (!a.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
        };
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{HostName}/terminal.html");
    }

    // ---------- page → app ----------

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try
        {
            m = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        string S(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
        switch (S("t"))
        {
            case "ready":
                _ready = true;
                (_cols, _rows) = (Math.Max(10, I("cols")), Math.Max(2, I("rows")));
                SendTheme();
                Connect();
                break;
            case "in":
                _session?.Send(S("d"));
                break;
            case "size":
                (_cols, _rows) = (Math.Max(10, I("cols")), Math.Max(2, I("rows")));
                _session?.Resize(_cols, _rows);
                break;
            case "paste":
                var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                if (text.Length > 0) Post(new { t = "paste", d = text.Replace("\r\n", "\n") });
                break;
            case "copy":
                if (S("d") is { Length: > 0 } copied) Clipboard.SetText(copied);
                break;
            case "title":
                TitleChanged?.Invoke(S("d"));
                break;
            case "key":
                KeyCommand?.Invoke(S("k"));
                break;
            case "link":
                if (Uri.TryCreate(S("url"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                break;
            case "reconnect":
                if (State == TerminalState.Closed) Connect();
                break;
        }
    }

    // ---------- session ----------

    private async void Connect()
    {
        if (_connecting || _disposed) return;
        _connecting = true;
        SetState(TerminalState.Connecting);
        _session?.Dispose();
        var session = _createSession();
        session.Output += OnOutput;
        session.Closed += OnClosed;
        _session = session;
        Post(new { t = "banner", text = L.F("Term.Connecting", session.Server.Name, session.Server.Display) });
        try
        {
            var (cols, rows) = (_cols, _rows);
            await Task.Run(() => session.Open(cols, rows, _command));
            if (_disposed) return;
            Post(new { t = "opened" });
            SetState(TerminalState.Connected);
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Post(new { t = "banner", text = "" });
            Post(new { t = "closed", text = L.F("Term.ConnectFailed", ex.Message) });
            SetState(TerminalState.Closed);
        }
        finally
        {
            _connecting = false;
        }
    }

    private void OnOutput(string text)
    {
        lock (_pending) _pending.Append(text);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_flush.IsEnabled) _flush.Start();
        });
    }

    private void Flush()
    {
        string text;
        lock (_pending)
        {
            if (_pending.Length == 0)
            {
                _flush.Stop();
                return;
            }
            text = _pending.ToString();
            _pending.Clear();
        }
        Post(new { t = "out", d = text });
    }

    private void OnClosed(string? error) => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        Flush();
        Post(new { t = "closed", text = error == null ? L.Get("Term.Ended") : L.F("Term.Lost", error) });
        SetState(TerminalState.Closed, error == null);
    });

    private void SetState(TerminalState state, bool endedByUser = false)
    {
        State = state;
        EndedNormally = endedByUser;
        StateChanged?.Invoke(state);
    }

    /// <summary>The last close was the shell exiting (exit / logout), not a lost connection.</summary>
    public bool EndedNormally { get; private set; }

    // ---------- theme ----------

    /// <summary>Follows the app theme: dark or light palette on the app's background colour.</summary>
    public void SendTheme()
    {
        var bg = (TryFindResource("SolidBackgroundFillColorQuarternaryBrush") as SolidColorBrush)?.Color ?? Colors.Black;
        var dark = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B < 128;
        Post(new { t = "theme", dark, bg = $"#{bg.R:x2}{bg.G:x2}{bg.B:x2}" });
    }

    private void Post(object message)
    {
        if (!_ready || _disposed || _web.CoreWebView2 == null) return;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flush.Stop();
        _session?.Dispose();
        _session = null;
        _web.Dispose();
    }
}

public enum TerminalState
{
    Connecting,
    Connected,
    Closed,
}
