using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SshManager.Core.Mcp;
using SshManager.Core.Models;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>
/// What AI agents did, as it happens (like tail -f): one server or all of them, the newest at the bottom — as a console
/// session (every call a prompt with its command and output, in colour) or as the log's own lines.
/// The files themselves are in data\agent-logs (rotated by size and age, see the settings).
/// </summary>
public partial class AgentLogWindow : Window
{
    private const int InitialLines = 500;
    private const int MaxChars = 2_000_000;

    private sealed record ServerChoice(Guid? Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The view chosen last (for the next window in this run of the app).</summary>
    private static bool _linesMode;

    private readonly AppHost _host;
    private readonly List<string> _paused = [];
    private readonly AgentConsole _console = new();
    private int _shown;

    public AgentLogWindow(AppHost host, Guid? serverId)
    {
        InitializeComponent();
        _host = host;
        if (_linesMode) LinesMode.IsChecked = true;
        ShowView();
        Icon = IconFactory.CreateImage(true);
        var choices = new List<ServerChoice> { new(null, L.Get("AgentLog.All")) };
        if (host.Vault.TryRead(d => d.Servers.OrderBy(s => s.Name).Select(s => (s.Id, s.Name, s.McpAccess)).ToList(), out var servers))
            choices.AddRange(servers.Select(s => new ServerChoice(s.Id, s.McpAccess == McpAccess.Off ? $"{s.Name}  ({L.Get("Mcp.LevelOff")})" : s.Name)));
        ServerBox.ItemsSource = choices;
        Select(serverId);
        host.Mcp.Log.Appended += OnAppended;
        Closed += (_, _) =>
        {
            host.Mcp.Log.Appended -= OnAppended;
            LogConsole.Dispose();
        };
        PauseButton.Unchecked += (_, _) => Resume();
    }

    private Guid? Selected => (ServerBox.SelectedItem as ServerChoice)?.Id;

    /// <summary>Shows one server's log (null = all).</summary>
    public void Select(Guid? serverId)
    {
        var list = (List<ServerChoice>)ServerBox.ItemsSource;
        ServerBox.SelectedItem = list.FirstOrDefault(c => c.Id == serverId) ?? list[0];
    }

    private void OnServerChanged(object sender, SelectionChangedEventArgs e) => Reload();

    private void OnViewChanged(object sender, RoutedEventArgs e)
    {
        if (ConsoleHost == null || Lines == null) return; // while the XAML loads
        _linesMode = LinesMode.IsChecked == true;
        ShowView();
    }

    private void ShowView()
    {
        ConsoleHost.Visibility = _linesMode ? Visibility.Collapsed : Visibility.Visible;
        Lines.Visibility = _linesMode ? Visibility.Visible : Visibility.Collapsed;
        if (_linesMode) Lines.ScrollToEnd();
    }

    private void Reload()
    {
        var log = _host.Mcp.Log;
        var lines = Selected is { } id ? log.Tail(id, InitialLines) : log.TailAll(InitialLines);
        _paused.Clear();
        Lines.Text = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
        Lines.ScrollToEnd();
        _console.Clear();
        LogConsole.Reset(Render(lines));
        _shown = lines.Count;
        ShowCount();
    }

    private void ShowCount() =>
        Status.Text = _shown == 0 ? L.Get("AgentLog.Empty") : L.F("AgentLog.Shown", _shown, _host.Mcp.Log.Folder);

    /// <summary>The console text for log lines; a line that is not an entry (edited by hand) shows as it is.</summary>
    private string Render(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(AgentLogEntry.TryParse(line) is { } entry ? _console.Render(entry) : line + "\r\n");
        return sb.ToString();
    }

    private void OnAppended(AgentLogEntry e) => Dispatcher.BeginInvoke(() =>
    {
        if (Selected is { } id && e.ServerId != id) return;
        var line = e.Format(); // what the file has: cut and on one line, so both views show the same as after a reload
        if (PauseButton.IsChecked == true)
        {
            _paused.Add(line);
            Status.Text = L.F("AgentLog.PausedCount", _paused.Count);
            return;
        }
        Append([line]);
    });

    private void Resume()
    {
        if (_paused.Count > 0) Append(_paused);
        _paused.Clear();
        ShowCount();
    }

    private void Append(IReadOnlyList<string> lines)
    {
        var atEnd = Lines.VerticalOffset + Lines.ViewportHeight >= Lines.ExtentHeight - 4;
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l).Append('\n');
        Lines.AppendText(sb.ToString());
        if (Lines.Text.Length > MaxChars) Lines.Text = Lines.Text[^(MaxChars / 2)..];
        if (atEnd) Lines.ScrollToEnd();
        LogConsole.Append(Render(lines)); // the terminal keeps its own scrollback limit
        _shown += lines.Count;
        ShowCount();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = _host.Mcp.Log.Folder;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
    }
}
