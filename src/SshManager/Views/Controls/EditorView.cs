using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SshManager.Core.Files;
using SshManager.Core.Models;
using SshManager.Services;

namespace SshManager.Views.Controls;

/// <summary>
/// A remote text file in a tab: CodeMirror (Assets\editor) in WebView2 over <see cref="RemoteTextFile"/>.
/// Ctrl+S saves; a file changed on the server meanwhile, or one the login user may not write, asks first.
/// </summary>
public sealed class EditorView : DockPanel, IDisposable
{
    private const string HostName = "sshm-editor.local";

    private readonly WebView2 _web = new();
    private readonly RemoteTextFile _file;
    private readonly TextBlock _pathText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
    private readonly Button _save = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
    private readonly Button _reload = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
    private readonly ToggleButton _wrap = new() { Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 12, 0), Focusable = false };
    private bool _ready;
    private bool _loaded;
    private bool _saving;
    private bool _disposed;
    private string _status = "";

    public EditorView(AppHost host, ServerEntry server, string path)
    {
        Server = server;
        _file = new RemoteTextFile(host.Ssh, server, path);
        Margin = new Thickness(0, 8, 0, 0);

        _save.Content = L.Get("TextEd.Save");
        _save.ToolTip = "Ctrl+S";
        _save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        _save.Click += (_, _) => Post(new { t = "requestSave" });
        _reload.Content = L.Get("TextEd.Reload");
        _reload.Click += (_, _) => Reload();
        _wrap.Content = L.Get("TextEd.Wrap");
        _wrap.ToolTip = "Alt+Z";
        _wrap.Click += (_, _) => Post(new { t = "wrap", on = _wrap.IsChecked == true });
        _pathText.Text = $"{server.Name}:{path}";
        _pathText.ToolTip = _pathText.Text;
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        SetDock(bar, Dock.Top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _save, _reload, _wrap } };
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(_statusText, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(_statusText);
        bar.Children.Add(_pathText);
        Children.Add(bar);
        var frame = new Border { Child = _web, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        frame.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Children.Add(frame);
        _web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        UpdateButtons();
        Loaded += OnLoaded;
    }

    public ServerEntry Server { get; }
    public string Path => _file.Path;
    public string FileName => _file.Name;
    public bool IsDirty { get; private set; }
    public bool HasError { get; private set; }

    /// <summary>Dirty, saved, failed: for the tab header.</summary>
    public event Action? StateChanged;
    /// <summary>Ctrl+W / Ctrl+Tab from inside the editor.</summary>
    public event Action<string>? KeyCommand;

    public new void Focus()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    private Window? Owner => Window.GetWindow(this);

    /// <summary>Unsaved changes: save, discard or stay.</summary>
    public bool ConfirmClose()
    {
        if (!IsDirty) return true;
        var r = MessageBox.Show(Owner, L.F("TextEd.CloseConfirm", FileName), L.Get("TextEd.Title"), MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (r == MessageBoxResult.No) return true;
        if (r == MessageBoxResult.Yes) Post(new { t = "requestSave", close = true });
        return false;
    }

    /// <summary>Closes the tab once a save requested by <see cref="ConfirmClose"/> went through.</summary>
    public event Action? SavedAndClose;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _web.EnsureCoreWebView2Async(await TerminalView.Environment());
        }
        catch (Exception ex)
        {
            Children.Remove(_web.Parent as UIElement);
            Children.Add(new TextBlock { Text = L.F("Term.WebViewMissing", ex.Message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) });
            return;
        }
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = true;
        s.AreDevToolsEnabled = Debugger.IsAttached;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(HostName, System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "editor"),
            CoreWebView2HostResourceAccessKind.Deny);
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.NavigationStarting += (_, a) =>
        {
            if (!a.Uri.StartsWith($"https://{HostName}/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
        };
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://{HostName}/editor.html");
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
        int I(string name) => m.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
        bool B(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        switch (S("t"))
        {
            case "ready":
                _ready = true;
                SendTheme();
                Reload();
                break;
            case "dirty":
                IsDirty = B("on");
                UpdateButtons();
                StateChanged?.Invoke();
                break;
            case "save":
                Save(S("text"), B("close"));
                break;
            case "status":
                _status = L.F("TextEd.Status", I("line"), I("col")) + (I("sel") > 0 ? " · " + L.F("TextEd.Selected", I("sel")) : "") +
                          $" · {(_file.Format.Crlf ? "CRLF" : "LF")} · {_file.Format.Encoding}" + (S("lang") is { Length: > 0 } lang ? " · " + lang : "") +
                          (_file.UsesSudo ? " · sudo" : "");
                _statusText.Text = _status;
                break;
            case "wrap":
                _wrap.IsChecked = B("on");
                break;
            case "key":
                KeyCommand?.Invoke(S("k"));
                break;
        }
    }

    private async void Reload()
    {
        if (!_ready) return;
        if (IsDirty && MessageBox.Show(Owner, L.F("TextEd.ReloadConfirm", FileName), L.Get("TextEd.Title"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Post(new { t = "banner", text = L.F("TextEd.Loading", FileName) });
        try
        {
            var text = await Task.Run(_file.Load);
            if (_disposed) return;
            _loaded = true;
            HasError = false;
            IsDirty = false;
            Post(new { t = "load", text, name = _file.Name, path = _file.Path });
            if (_file.IsNew) _status = L.Get("TextEd.NewFile");
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            HasError = true;
            Post(new { t = "banner", text = L.F("TextEd.LoadFailed", ex.Message) });
        }
        UpdateButtons();
        StateChanged?.Invoke();
    }

    private async void Save(string text, bool close)
    {
        if (!_loaded || _saving) return;
        _saving = true;
        UpdateButtons();
        try
        {
            if (await Task.Run(_file.ChangedOnServer) &&
                MessageBox.Show(Owner, L.F("TextEd.ChangedOnServer", FileName), L.Get("TextEd.Title"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                await Task.Run(() => _file.Save(text));
            }
            catch (UnauthorizedAccessException) when (_file is { UsesSudo: false, CanSudo: true })
            {
                if (MessageBox.Show(Owner, L.F("TextEd.SaveWithSudo", FileName), L.Get("TextEd.Title"), MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                    return;
                await Task.Run(() => _file.Save(text, sudo: true));
            }
            if (_disposed) return;
            Post(new { t = "saved" });
            IsDirty = false;
            HasError = false;
            _statusText.Text = L.Get("TextEd.Saved") + " · " + _status;
            StateChanged?.Invoke();
            if (close) SavedAndClose?.Invoke();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            MessageBox.Show(Owner, L.F("TextEd.SaveFailed", ex.Message), L.Get("TextEd.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saving = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        _save.IsEnabled = _loaded && !_saving;
        _reload.IsEnabled = _ready && !_saving;
    }

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
        _file.Dispose();
        _web.Dispose();
    }
}
