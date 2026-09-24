using System.IO.Compression;
using SshManager.Core.Models;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;

namespace SshManager.Core.Backup;

/// <summary>Zips the data folder and stores the archive in a folder or on a server over SFTP.</summary>
public sealed class BackupService(VaultService vault, SettingsService settings, SshClientFactory ssh, string? dataDir = null)
{
    public const string FilePrefix = "sshmanager-data-";
    private readonly string _dataDir = dataDir ?? AppPaths.DataDir;
    private readonly SemaphoreSlim _gate = new(1);

    public BackupSettings Config => settings.Settings.Backup;

    public bool IsConfigured => Config.Target switch
    {
        BackupTarget.Folder => !string.IsNullOrWhiteSpace(Config.Folder),
        BackupTarget.Ssh => Config.ServerId != null,
        _ => false,
    };

    /// <summary>True when an automatic backup is due.</summary>
    public bool AutoDue =>
        Config.AutoHours > 0 && IsConfigured &&
        (Config.LastBackup is not { } last || DateTime.Now - last >= TimeSpan.FromHours(Config.AutoHours));

    /// <summary>Creates the archive and uploads it; returns where it went. Updates LastBackup / LastResult.</summary>
    public async Task<string> RunAsync(bool interactive = true)
    {
        await _gate.WaitAsync();
        try
        {
            var cfg = Config;
            var name = $"{FilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            string destination;
            try
            {
                var zip = await Task.Run(CreateArchive);
                destination = cfg.Target switch
                {
                    BackupTarget.Folder => await Task.Run(() => ToFolder(zip, name, cfg)),
                    BackupTarget.Ssh => await Task.Run(() => ToServer(zip, name, cfg, interactive)),
                    _ => throw new InvalidOperationException(),
                };
            }
            catch (Exception ex)
            {
                cfg.LastResult = L.F("Backup.ResultFailed", DateTime.Now, ex.Message);
                settings.Save();
                throw;
            }
            cfg.LastBackup = DateTime.Now;
            cfg.LastResult = L.F("Backup.ResultOk", DateTime.Now, destination);
            settings.Save();
            return destination;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>In-memory zip of the data folder, taken while the vault cannot be written.</summary>
    public byte[] CreateArchive()
    {
        var root = Path.GetFullPath(_dataDir);
        var exclude = Config.Target == BackupTarget.Folder && !string.IsNullOrWhiteSpace(Config.Folder)
            ? Path.GetFullPath(Config.Folder).TrimEnd('\\') + "\\"
            : null;
        var files = vault.WithFilesLocked(() => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Where(f => exclude == null || !f.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
            .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/'), Data: TryRead(f)))
            .Where(f => f.Data != null)
            .ToList());

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (fileName, data) in files)
            {
                var entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(data!);
            }
        }
        return ms.ToArray();
    }

    private static byte[]? TryRead(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ToFolder(byte[] zip, string name, BackupSettings cfg)
    {
        var dir = Path.GetFullPath(cfg.Folder!);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, zip);
        if (cfg.Keep > 0)
        {
            foreach (var old in new DirectoryInfo(dir).GetFiles(FilePrefix + "*.zip")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(cfg.Keep))
                old.Delete();
        }
        return path;
    }

    private string ToServer(byte[] zip, string name, BackupSettings cfg, bool interactive)
    {
        var server = vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == cfg.ServerId)?.Clone())
                     ?? throw new InvalidOperationException(L.Get("Backup.ServerMissing"));
        using var sftp = ssh.ConnectSftp(server, interactive);
        var dir = string.IsNullOrWhiteSpace(cfg.RemotePath) ? "." : cfg.RemotePath.Trim().TrimEnd('/');
        if (dir.StartsWith("~/", StringComparison.Ordinal)) dir = dir[2..];
        if (dir == "~") dir = ".";
        EnsureRemoteDir(sftp, dir);
        var remote = dir == "." ? name : $"{dir}/{name}";
        using (var ms = new MemoryStream(zip)) sftp.UploadFile(ms, remote);
        sftp.ChangePermissions(remote, 600); // SSH.NET reads the digits as octal
        if (cfg.Keep > 0)
        {
            foreach (var old in sftp.ListDirectory(dir)
                         .Where(f => f.IsRegularFile && f.Name.StartsWith(FilePrefix, StringComparison.Ordinal) && f.Name.EndsWith(".zip"))
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(cfg.Keep))
                sftp.DeleteFile(old.FullName);
        }
        return $"{server.Name}:{remote}";
    }

    private static void EnsureRemoteDir(Renci.SshNet.SftpClient sftp, string dir)
    {
        if (dir == ".") return;
        var path = dir.StartsWith('/') ? "" : null;
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path == null ? part : $"{path}/{part}";
            if (!sftp.Exists(path)) sftp.CreateDirectory(path);
        }
    }
}
