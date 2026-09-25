using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SshManager.Views.Controls;

/// <summary>
/// Read-only terminal (xterm.js in WebView2, Assets\terminal\log.html) that shows ANSI text written by the app:
/// the agent log as a console session. Text written before the page is ready is kept and sent once it is.
/// </summary>
public sealed class LogConsoleView : Border, IDisposable
{
    private const string HostName = "sshm-log.local";

    private readonly WebView2 _web = new();
    private readonly StringBuilder _early = new();
    private bool _ready;
    private bool _disposed;

    public LogConsoleView()
    {
        Child = _web;
        _web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Loaded += OnLoaded;
    }

    /// <summary>Replaces everything shown and scrolls to the end.</summary>
    public void Reset(string ansi)
    {
        _early.Clear().Append(ansi);
        if (_ready) Post(new { t = "reset", d = ansi });
    }

    /// <summary>Adds text at the end (the view follows it when it was at the end).</summary>
    public void Append(string ansi)
    {
        if (_ready) Post(new { t = "out", d = ansi });
        else _early.Append(ansi);
    }

    public new void Focus() => _web.Focus();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _web.EnsureCoreWebView2Async(await TerminalView.Environment());
        }
        catch (Exception ex)
        {
            Child = new TextBlock { Text = L.F("Term.WebViewMissing", ex.Message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
            return;
        }
        if (_disposed) return;
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = Debugger.IsAttached;
        s.AreBrowserAcceleratorKeysEnabled = false;
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
        core.Navigate($"https://{HostName}/log.html");
    }

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
        switch (S("t"))
        {
            case "ready":
                _ready = true;
                SendTheme();
                Post(new { t = "labels", copy = L.Get("Term.MenuCopy"), selectAll = L.Get("Term.MenuSelectAll") });
                Post(new { t = "reset", d = _early.ToString() });
                _early.Clear();
                break;
            case "copy":
                if (S("d") is { Length: > 0 } copied)
                {
                    try
                    {
                        Clipboard.SetText(copied);
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        // the clipboard is busy in another program
                    }
                }
                break;
            case "link":
                if (Uri.TryCreate(S("url"), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                break;
        }
    }

    /// <summary>Dark or light palette on the app's background colour.</summary>
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
        _web.Dispose();
    }
}
