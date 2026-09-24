using System.Collections.ObjectModel;
using SshManager.Core.Files;
using SshManager.Mvvm;

namespace SshManager.ViewModels;

/// <summary>One row of a file panel; ".." (up) is the first row of every folder but the top.</summary>
public sealed class FileRow(FileItem item, bool isParent = false)
{
    public FileItem Item { get; } = item;
    public bool IsParent { get; } = isParent;
    public bool IsDirectory => IsParent || Item.IsDirectory;
    public string Name => IsParent ? ".." : Item.Name;
    public string Icon => IsParent ? "" : Item.IsLink ? (Item.IsDirectory ? "" : "") : Item.IsDirectory ? "" : "";
    public string SizeText => IsDirectory ? (IsParent ? "" : Item.Path.Length <= 3 && Item.Size > 0 ? L.F("Files.Free", FormatSize(Item.Size)) : "") : FormatSize(Item.Size);
    public string ModifiedText => IsParent ? "" : Item.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Permissions => IsParent ? "" : Item.Permissions ?? "";
    public string Owner => IsParent || Item.Owner == null ? "" : Item.Group == null || Item.Group == Item.Owner ? Item.Owner : $"{Item.Owner}:{Item.Group}";
    public double Opacity => !IsParent && Item.IsHidden ? 0.6 : 1;

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

/// <summary>One side of the file manager: a folder of the local PC or of the server.</summary>
public sealed class FilePanelModel : ObservableObject
{
    private readonly Func<IFileSystem> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IFileSystem? _fs;
    private string _path = "";
    private string _pathEdit = "";
    private bool _busy;
    private string? _error;
    private string _status = "";
    private bool _showHidden;
    private List<FileItem> _all = [];

    /// <param name="connect">Creates the file system (the SFTP connection); called on a worker thread.</param>
    public FilePanelModel(string title, bool remote, Func<IFileSystem> connect)
    {
        Title = title;
        IsRemote = remote;
        _connect = connect;
    }

    public string Title { get; }
    public bool IsRemote { get; }
    public string Icon => IsRemote ? "" : "";
    public ObservableCollection<FileRow> Rows { get; } = [];
    public IFileSystem? Fs => _fs;

    /// <summary>The folder shown ("" = the drive list).</summary>
    public string Path
    {
        get => _path;
        private set
        {
            if (Set(ref _path, value)) PathEdit = value;
        }
    }

    public string PathEdit
    {
        get => _pathEdit;
        set => Set(ref _pathEdit, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error != null;
    public bool Connected => _fs != null;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (Set(ref _showHidden, value)) Fill(null);
        }
    }

    /// <summary>Connects (first time or after an error) and shows <paramref name="path"/> (null = the home folder).</summary>
    public async Task OpenAsync(string? path, string? select = null)
    {
        await _gate.WaitAsync();
        Busy = true;
        try
        {
            var fs = _fs;
            if (fs == null)
            {
                fs = await Task.Run(_connect);
                _fs = fs;
                OnPropertyChanged(nameof(Connected));
            }
            var target = path == null ? fs.Home : fs.Normalize(path);
            // an error keeps the current folder on screen
            _all = await Task.Run(() => fs.List(target).ToList());
            Path = target;
            Error = null;
            Fill(select);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            PathEdit = Path;
            if (_fs is SftpFileSystem { IsConnected: false })
            {
                _fs.Dispose();
                _fs = null;
                OnPropertyChanged(nameof(Connected));
            }
        }
        finally
        {
            Busy = false;
            _gate.Release();
        }
    }

    public Task RefreshAsync(string? select = null) => OpenAsync(_fs == null ? null : Path, select);

    public Task UpAsync()
    {
        if (_fs?.Parent(Path) is not { } parent) return Task.CompletedTask;
        var name = _fs.IsRemote ? Path[(Path.LastIndexOf('/') + 1)..] : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
        return OpenAsync(parent, name);
    }

    /// <summary>Runs a file operation on the worker thread, then shows the error (if any) and refreshes.</summary>
    public async Task<bool> RunAsync(Action<IFileSystem> action, string? select = null)
    {
        if (_fs is not { } fs) return false;
        string? error = null;
        await _gate.WaitAsync();
        Busy = true;
        try
        {
            await Task.Run(() => action(fs));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            Busy = false;
            _gate.Release();
        }
        await RefreshAsync(select);
        if (error != null) Error = error;
        return error == null;
    }

    private void Fill(string? select)
    {
        Rows.Clear();
        if (_fs?.Parent(Path) != null) Rows.Add(new FileRow(new FileItem("..", _fs.Parent(Path)!, true, 0, null), true));
        var visible = _all.Where(i => _showHidden || !i.IsHidden).ToList();
        foreach (var i in visible.OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new FileRow(i));
        var dirs = visible.Count(i => i.IsDirectory);
        var files = visible.Count - dirs;
        var size = visible.Where(i => !i.IsDirectory).Sum(i => i.Size);
        var hidden = _all.Count - visible.Count;
        Status = L.F("Files.Status", dirs, files, FileRow.FormatSize(size)) + (hidden > 0 ? " · " + L.F("Files.HiddenCount", hidden) : "");
        SelectRequested?.Invoke(select);
    }

    /// <summary>After a refresh: select the row with this name (or the first one).</summary>
    public event Action<string?>? SelectRequested;

    public void Close()
    {
        _fs?.Dispose();
        _fs = null;
    }
}

/// <summary>A copy / move in the transfers list.</summary>
public sealed class TransferRow : ObservableObject
{
    private DateTime _lastAt = DateTime.Now;
    private long _lastBytes;
    private double _speed;

    public TransferRow(TransferJob job)
    {
        Job = job;
        CancelCommand = new RelayCommand(() => Job.Cancel(), () => Job.State == TransferState.Running);
    }

    public TransferJob Job { get; }
    public RelayCommand CancelCommand { get; }
    public string Title => Job.Title;
    public bool IsRunning => Job.State == TransferState.Running;
    public double Percent => Job.TotalBytes > 0 ? 100.0 * Job.DoneBytes / Job.TotalBytes : Job.TotalFiles > 0 ? 100.0 * Job.DoneFiles / Job.TotalFiles : IsRunning ? 0 : 100;
    public bool Failed => Job.State == TransferState.Failed;

    public string Text
    {
        get
        {
            var sizes = $"{FileRow.FormatSize(Job.DoneBytes)} / {FileRow.FormatSize(Job.TotalBytes)}";
            var files = $"{Job.DoneFiles}/{Job.TotalFiles}";
            return Job.State switch
            {
                TransferState.Running => $"{sizes} · {files}" + (_speed > 0 ? $" · {FileRow.FormatSize((long)_speed)}/s" : "") +
                                         (Job.Current != null ? " · " + Job.Current : ""),
                TransferState.Done => L.F("Files.TransferDone", files, FileRow.FormatSize(Job.TotalBytes)) +
                                      (Job.Skipped > 0 ? " · " + L.F("Files.Skipped", Job.Skipped) : ""),
                TransferState.Canceled => L.Get("Files.TransferCanceled"),
                _ => L.F("Files.TransferFailed", Job.Error ?? ""),
            };
        }
    }

    /// <summary>Called by the view's timer.</summary>
    public void Refresh()
    {
        var now = DateTime.Now;
        var dt = (now - _lastAt).TotalSeconds;
        if (dt >= 1)
        {
            _speed = (Job.DoneBytes - _lastBytes) / dt;
            _lastBytes = Job.DoneBytes;
            _lastAt = now;
        }
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Failed));
    }
}
