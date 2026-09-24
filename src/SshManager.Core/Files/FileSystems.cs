using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace SshManager.Core.Files;

/// <summary>A file or folder in a panel of the file manager.</summary>
/// <param name="Mode">Unix permission bits (remote only).</param>
public sealed record FileItem(string Name, string Path, bool IsDirectory, long Size, DateTime? Modified, bool IsLink = false,
    int? Mode = null, string? Owner = null, string? Group = null, bool HiddenAttribute = false)
{
    public bool IsHidden => HiddenAttribute || Name.StartsWith('.');

    /// <summary>"rwxr-xr-x".</summary>
    public string? Permissions => Mode is { } m ? FileSystems.ModeText(m) : null;
}

/// <summary>The local disks or a server over SFTP. Blocking calls: run them off the UI thread.</summary>
public interface IFileSystem : IDisposable
{
    bool IsRemote { get; }
    /// <summary>Where a panel starts: the user's home.</summary>
    string Home { get; }
    string Combine(string dir, string name);
    /// <summary>The containing folder, null at the top (a drive list locally, "/" remotely).</summary>
    string? Parent(string path);
    string Normalize(string path);
    IReadOnlyList<FileItem> List(string path);
    /// <summary>null when it does not exist.</summary>
    FileItem? Stat(string path);
    void CreateDirectory(string path);
    /// <summary>Folders with everything in them.</summary>
    void Delete(FileItem item, CancellationToken ct = default);
    void Rename(string from, string to);
    Stream OpenRead(string path);
    /// <summary>Creates or truncates (keeps the permissions and owner of an existing file).</summary>
    Stream Create(string path);
    void SetModified(string path, DateTime utc);
}

public static class FileSystems
{
    public static string ModeText(int mode)
    {
        var sb = new StringBuilder(9);
        for (var shift = 6; shift >= 0; shift -= 3)
        {
            var bits = (mode >> shift) & 7;
            sb.Append((bits & 4) != 0 ? 'r' : '-').Append((bits & 2) != 0 ? 'w' : '-').Append((bits & 1) != 0 ? 'x' : '-');
        }
        return sb.ToString();
    }

    /// <summary>"755" / "0644" → bits; null when not an octal mode.</summary>
    public static int? ParseMode(string text)
    {
        text = text.Trim();
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7')) return null;
        return Convert.ToInt32(text, 8);
    }

    /// <summary>A name the target file system accepts (no separators, not empty, not . or ..).</summary>
    public static bool IsValidName(string name, bool remote) =>
        name.Length > 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\0') &&
        (remote || name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
}

/// <summary>This PC: drives at the top, then Windows paths.</summary>
public sealed class LocalFileSystem : IFileSystem
{
    public bool IsRemote => false;
    public string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Combine(string dir, string name) => dir.Length == 0 ? name : Path.Combine(dir, name);

    public string? Parent(string path)
    {
        if (path.Length == 0) return null;
        // a drive root goes up to the drive list
        return new DirectoryInfo(path).Parent?.FullName ?? "";
    }

    public string Normalize(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return "";
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    public IReadOnlyList<FileItem> List(string path)
    {
        if (path.Length == 0)
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new FileItem(d.Name.TrimEnd('\\') + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : $"  {d.VolumeLabel}"), d.RootDirectory.FullName,
                    true, d.TotalFreeSpace, null))
                .ToList();
        var dir = new DirectoryInfo(path);
        var list = new List<FileItem>();
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var isDir = e is DirectoryInfo;
            list.Add(new FileItem(e.Name, e.FullName, isDir, isDir ? 0 : ((FileInfo)e).Length, e.LastWriteTime,
                e.LinkTarget != null, HiddenAttribute: (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0));
        }
        return list;
    }

    public FileItem? Stat(string path)
    {
        if (Directory.Exists(path)) return new FileItem(Path.GetFileName(path), path, true, 0, Directory.GetLastWriteTime(path));
        if (!File.Exists(path)) return null;
        var f = new FileInfo(path);
        return new FileItem(f.Name, f.FullName, false, f.Length, f.LastWriteTime);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Delete(FileItem item, CancellationToken ct = default)
    {
        if (item.IsDirectory) Directory.Delete(item.Path, recursive: true);
        else File.Delete(item.Path);
    }

    public void Rename(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
    public Stream Create(string path) => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);

    public void SetModified(string path, DateTime utc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, utc);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>A server over SFTP (as the login user). Owner names come from /etc/passwd and /etc/group.</summary>
public sealed class SftpFileSystem : IFileSystem
{
    private readonly SftpClient _sftp;
    private readonly Dictionary<int, string> _users = [];
    private readonly Dictionary<int, string> _groups = [];

    public SftpFileSystem(SftpClient sftp)
    {
        _sftp = sftp;
        _sftp.OperationTimeout = TimeSpan.FromSeconds(60);
        Home = _sftp.WorkingDirectory is { Length: > 0 } wd ? wd : "/";
        LoadNames("/etc/passwd", _users);
        LoadNames("/etc/group", _groups);
    }

    public bool IsRemote => true;
    public string Home { get; }
    public bool IsConnected => _sftp.IsConnected;

    public string Combine(string dir, string name) => dir.TrimEnd('/') + "/" + name;

    public string? Parent(string path)
    {
        path = Normalize(path);
        if (path == "/") return null;
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    public string Normalize(string path)
    {
        path = path.Trim();
        if (path == "~" || path.StartsWith("~/")) path = Home.TrimEnd('/') + path[1..];
        if (!path.StartsWith('/')) path = Home.TrimEnd('/') + "/" + path;
        var parts = new List<string>();
        foreach (var p in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(p);
        }
        return "/" + string.Join('/', parts);
    }

    public IReadOnlyList<FileItem> List(string path)
    {
        var list = new List<FileItem>();
        foreach (var f in _sftp.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            var isDir = f.IsDirectory;
            var size = f.Length;
            if (f.IsSymbolicLink)
            {
                // the link's target decides whether it opens like a folder
                try
                {
                    var target = _sftp.GetAttributes(f.FullName);
                    isDir = target.IsDirectory;
                    size = target.Size;
                }
                catch (SshException)
                {
                }
            }
            list.Add(Item(f.Name, f.FullName, f.Attributes, isDir, size, f.IsSymbolicLink));
        }
        return list;
    }

    public FileItem? Stat(string path)
    {
        try
        {
            var a = _sftp.GetAttributes(path);
            return Item(path == "/" ? "/" : path[(path.LastIndexOf('/') + 1)..], path, a, a.IsDirectory, a.Size, false);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private FileItem Item(string name, string path, SftpFileAttributes a, bool isDir, long size, bool link) =>
        new(name, path, isDir, isDir ? 0 : size, a.LastWriteTime, link, Mode(a),
            _users.GetValueOrDefault(a.UserId) ?? a.UserId.ToString(), _groups.GetValueOrDefault(a.GroupId) ?? a.GroupId.ToString());

    private static int Mode(SftpFileAttributes a)
    {
        var m = 0;
        if (a.OwnerCanRead) m |= 0x100;
        if (a.OwnerCanWrite) m |= 0x80;
        if (a.OwnerCanExecute) m |= 0x40;
        if (a.GroupCanRead) m |= 0x20;
        if (a.GroupCanWrite) m |= 0x10;
        if (a.GroupCanExecute) m |= 0x8;
        if (a.OthersCanRead) m |= 0x4;
        if (a.OthersCanWrite) m |= 0x2;
        if (a.OthersCanExecute) m |= 0x1;
        return m;
    }

    public void CreateDirectory(string path) => _sftp.CreateDirectory(path);

    public void Delete(FileItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!item.IsDirectory || item.IsLink)
        {
            _sftp.DeleteFile(item.Path);
            return;
        }
        foreach (var child in List(item.Path)) Delete(child, ct);
        _sftp.DeleteDirectory(item.Path);
    }

    public void Rename(string from, string to) => _sftp.RenameFile(from, to);

    public Stream OpenRead(string path) => _sftp.OpenRead(path);

    public Stream Create(string path) => _sftp.Open(path, FileMode.Create, FileAccess.Write);

    /// <summary>Pipelined upload (much faster than a stream over a high-latency link).</summary>
    public void Upload(Stream input, string path) => _sftp.UploadFile(input, path, canOverride: true);

    public void Download(string path, Stream output) => _sftp.DownloadFile(path, output);

    /// <param name="mode">Permission bits (0o644); SSH.NET wants the octal digits as a number (644).</param>
    public void ChangeMode(string path, int mode) => _sftp.ChangePermissions(path, short.Parse(Convert.ToString(mode & 0x1FF, 8)));

    public void SetModified(string path, DateTime utc)
    {
        try
        {
            var a = _sftp.GetAttributes(path);
            a.LastWriteTimeUtc = utc;
            a.LastAccessTimeUtc = utc;
            _sftp.SetAttributes(path, a);
        }
        catch (SshException)
        {
        }
    }

    public byte[] ReadAllBytes(string path) => _sftp.ReadAllBytes(path);

    private void LoadNames(string file, Dictionary<int, string> map)
    {
        try
        {
            foreach (var line in _sftp.ReadAllText(file).Split('\n'))
            {
                var p = line.Split(':');
                if (p.Length > 2 && int.TryParse(p[2], out var id)) map.TryAdd(id, p[0]);
            }
        }
        catch (Exception ex) when (ex is SshException or IOException)
        {
        }
    }

    public void Dispose() => _sftp.Dispose();
}
