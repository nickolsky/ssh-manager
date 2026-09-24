using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SshManager.Core.Files;
using SshManager.Core.Models;
using SshManager.Services;
using SshManager.ViewModels;

namespace SshManager.Views;

/// <summary>
/// Two-panel file manager tab: this PC on the left, the server over SFTP on the right. Transfers run on their own
/// SFTP connection, so browsing stays responsive while a big upload is going.
/// </summary>
public partial class FileManagerView : UserControl, IDisposable
{
    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly FilePanelModel _local;
    private readonly FilePanelModel _remote;
    private readonly ObservableCollection<TransferRow> _transfers = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string? _startDir;
    private FilePanelView _active;
    private bool _disposed;

    public FileManagerView(AppHost host, ServerEntry server, string? remoteDir)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        _startDir = remoteDir;
        _local = new FilePanelModel(L.Get("Files.ThisPc"), false, () => new LocalFileSystem());
        _remote = new FilePanelModel(server.Name, true, () => new SftpFileSystem(host.Ssh.ConnectSftp(server)));
        LeftPanel.Attach(_local);
        RightPanel.Attach(_remote);
        _active = RightPanel;
        TransferList.ItemsSource = _transfers;
        _timer.Tick += (_, _) => RefreshTransfers();

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.CopyRequested += StartTransfer;
            panel.FilesDropped += Upload;
            panel.EditRequested += item => EditRequested?.Invoke(item.Path);
            panel.NewFileCreated += path =>
            {
                if (panel.Model.IsRemote) EditRequested?.Invoke(path);
            };
            panel.TerminalRequested += path => TerminalRequested?.Invoke(path);
            panel.SwitchRequested += () => Other(panel).FocusGrid();
            panel.Activated += SetActive;
        }
        _remote.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FilePanelModel.Connected) or nameof(FilePanelModel.HasError)) StateChanged?.Invoke();
        };
        PreviewKeyDown += OnKey;
        Loaded += OnLoaded;
    }

    /// <summary>Open a remote file in the editor.</summary>
    public event Action<string>? EditRequested;
    /// <summary>A terminal in this remote folder.</summary>
    public event Action<string>? TerminalRequested;
    public event Action? CloseRequested;
    /// <summary>Connected / failed changed (for the tab's dot).</summary>
    public event Action? StateChanged;

    public ServerEntry Server => _server;
    public bool IsConnected => _remote.Connected && !_remote.HasError;
    public bool HasError => _remote.HasError;
    public bool HasRunningTransfers => _transfers.Any(t => t.IsRunning);
    public string RemotePath => _remote.Path;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var s = _host.SettingsStore.Settings;
        var localDir = s.FilesLocalDir is { Length: > 0 } d && System.IO.Directory.Exists(d) ? d : null;
        var remoteDir = _startDir ?? s.FilesRemoteDirs.GetValueOrDefault(_server.Id.ToString());
        var local = _local.OpenAsync(localDir);
        await _remote.OpenAsync(remoteDir);
        // a remembered folder that is gone: the home folder
        if (_remote.HasError && _remote.Connected && remoteDir != null && _startDir == null) await _remote.OpenAsync(null);
        await local;
        RightPanel.FocusGrid();
        SetActive(RightPanel);
    }

    /// <summary>Shows a folder of the server (the tab is already open).</summary>
    public async void Navigate(string remoteDir)
    {
        await _remote.OpenAsync(remoteDir);
        RightPanel.FocusGrid();
    }

    public new void Focus() => _active.FocusGrid();

    private FilePanelView Other(FilePanelView p) => p == LeftPanel ? RightPanel : LeftPanel;

    private void SetActive(FilePanelView panel)
    {
        _active = panel;
        LeftPanel.SetActive(panel == LeftPanel);
        RightPanel.SetActive(panel == RightPanel);
        CopyText.Text = L.Get(panel.Model.IsRemote ? "Files.Download" : "Files.Upload");
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.W)
        {
            CloseRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e) => _active.Copy(false);
    private void OnMove(object sender, RoutedEventArgs e) => _active.Copy(true);
    private void OnNewFolder(object sender, RoutedEventArgs e) => _active.NewFolder();
    private void OnEdit(object sender, RoutedEventArgs e) => _active.Edit();
    private void OnDelete(object sender, RoutedEventArgs e) => _active.Delete();
    private void OnTerminal(object sender, RoutedEventArgs e) => TerminalRequested?.Invoke(_remote.Path);

    private void OnHiddenChanged(object sender, RoutedEventArgs e)
    {
        _local.ShowHidden = _remote.ShowHidden = HiddenBox.IsChecked == true;
    }

    // ---------- transfers ----------

    private void Upload(IReadOnlyList<string> paths, string targetDir)
    {
        var local = new LocalFileSystem();
        var items = paths.Select(local.Stat).OfType<FileItem>().ToList();
        if (items.Count > 0) StartTransfer(LeftPanel, items, false, targetDir);
    }

    private void StartTransfer(FilePanelView source, IReadOnlyList<FileItem> items, bool move, string? targetDir)
    {
        var from = source.Model;
        var to = Other(source).Model;
        var target = targetDir ?? to.Path;
        if (from.Fs == null || to.Fs == null || target.Length == 0 || items.Count == 0) return;
        var what = items.Count == 1 ? items[0].Name : L.F("Files.ItemsCount", items.Count);
        var verb = L.Get(move ? "Files.Moving" : from.IsRemote ? "Files.Downloading" : "Files.Uploading");
        var job = new TransferJob($"{verb}: {what} → {target}");
        var row = new TransferRow(job);
        _transfers.Insert(0, row);
        TransfersBox.Visibility = Visibility.Visible;
        _timer.Start();
        var owner = Window.GetWindow(this);
        var first = items[0].Name;

        Task.Run(() =>
        {
            SftpFileSystem? remote = null;
            try
            {
                remote = new SftpFileSystem(_host.Ssh.ConnectSftp(_server));
                var local = new LocalFileSystem();
                Transfers.Run(job, from.IsRemote ? remote : local, items, to.IsRemote ? remote : local, target, move,
                    (src, existing) => Dispatcher.Invoke(() => ConflictWindow.Ask(owner, src, existing)));
            }
            catch (Exception ex)
            {
                job.Fail(ex.Message);
            }
            finally
            {
                remote?.Dispose();
            }
        }).ContinueWith(_ => Dispatcher.BeginInvoke(async () =>
        {
            if (_disposed) return;
            row.Refresh();
            if (to.Path == target) await to.RefreshAsync(first);
            if (move) await from.RefreshAsync();
        }));
    }

    private void RefreshTransfers()
    {
        foreach (var t in _transfers) t.Refresh();
        if (!HasRunningTransfers) _timer.Stop();
    }

    private void OnClearDone(object sender, RoutedEventArgs e)
    {
        foreach (var t in _transfers.Where(t => !t.IsRunning).ToList()) _transfers.Remove(t);
        if (_transfers.Count == 0) TransfersBox.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (var t in _transfers) t.Job.Cancel();
        var s = _host.SettingsStore.Settings;
        if (_local.Path.Length > 0) s.FilesLocalDir = _local.Path;
        if (_remote.Connected && _remote.Path.Length > 0) s.FilesRemoteDirs[_server.Id.ToString()] = _remote.Path;
        _host.SettingsStore.Save();
        _local.Close();
        _remote.Close();
    }
}
