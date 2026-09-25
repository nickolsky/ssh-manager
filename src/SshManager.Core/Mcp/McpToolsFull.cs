using System.Text;
using SshManager.Core.Files;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Mcp;

/// <summary>Full access: any command, the server's files, copying to and from the allowed local folders.</summary>
public sealed partial class McpServer
{
    private const int MaxReadBytes = 5 * 1024 * 1024;

    /// <summary>Remote folders an agent may not delete, whatever its access (a typo there wrecks the server).</summary>
    private static readonly HashSet<string> Protected =
    [
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin", "/srv",
        "/sys", "/tmp", "/usr", "/var", "/var/lib", "/var/log",
    ];

    private IEnumerable<McpTool> FullTools() =>
    [
        new("run_command", "Run command",
            "Runs a shell command on the server (sh -c, no terminal, no input) and returns exit code, stdout and stderr.",
            McpAccess.Full, Schema(ServerProp, new Prop("command", "string", "The shell command.", true),
                new Prop("sudo", "boolean", "Run as root (sudo for a non-root login, with the saved password when needed)."),
                new Prop("timeout_seconds", "integer", "Default 60, at most 1800.")), RunCommand, ReadOnly: false, Destructive: true),
        new("list_directory", "List directory",
            "Files and folders in a remote directory with size, mode, owner and modification time.",
            McpAccess.Full, Schema(ServerProp, new Prop("path", "string", "Remote directory; ~ is the login user's home.", true)), ListDirectory),
        new("read_file", "Read file",
            "A remote text file's content (UTF-8 / Latin-1). Unreadable for the login user: read with sudo. Binary files: use download.",
            McpAccess.Full, Schema(ServerProp, new Prop("path", "string", "Remote file.", true)), ReadFile),
        new("write_file", "Write file",
            "Creates or replaces a remote text file. An existing file keeps its encoding, line endings, owner and mode; " +
            "without write permission it is written through sudo.",
            McpAccess.Full, Schema(ServerProp, new Prop("path", "string", "Remote file.", true), new Prop("content", "string", "The new text.", true)),
            WriteFile, ReadOnly: false, Destructive: true, HiddenArgs: ["content"]),
        new("delete_path", "Delete file or folder",
            "Deletes a remote file, or a folder with everything in it. The user may have to confirm it in SSH Manager.",
            McpAccess.Full, Schema(ServerProp, new Prop("path", "string", "Remote file or folder.", true),
                new Prop("sudo", "boolean", "Delete as root (rm -rf).")), DeletePath, ReadOnly: false, Destructive: true),
        new("upload", "Upload",
            "Copies a local file or folder to a remote directory. The local path must be inside one of the server's allowed local folders (see list_servers).",
            McpAccess.Full, Schema(ServerProp, new Prop("local_path", "string", "Local file or folder (full Windows path).", true),
                new Prop("remote_dir", "string", "Remote directory to copy into (created when missing).", true)), Upload,
            ReadOnly: false, Destructive: true),
        new("download", "Download",
            "Copies a remote file or folder into a local directory inside one of the server's allowed local folders.",
            McpAccess.Full, Schema(ServerProp, new Prop("remote_path", "string", "Remote file or folder.", true),
                new Prop("local_dir", "string", "Local directory (full Windows path) inside an allowed folder.", true)), Download, ReadOnly: false),
        new("script_results", "Script results",
            "Values saved by install scripts on this server in SSH Manager: VPN links, addresses, generated admin passwords.",
            McpAccess.Full, Schema(ServerProp), ScriptResults),
    ];

    private async Task<ToolResult> RunCommand(ToolCall c)
    {
        var command = c.Required("command");
        var timeout = TimeSpan.FromSeconds(c.Int("timeout_seconds", 60, 1, 1800));
        ShellResult r;
        try
        {
            r = await Exec(c, command, elevated: c.Bool("sudo"), timeout);
        }
        catch (TimeoutException)
        {
            return ToolResult.Fail(L.F("Mcp.Timeout", (int)timeout.TotalSeconds));
        }
        var text = $"exit code: {r.ExitCode}\n--- stdout ---\n{Cut(r.Output)}" + (r.Error.Length > 0 ? $"\n--- stderr ---\n{Cut(r.Error, 20_000)}" : "");
        return new ToolResult(text, IsError: false, LogText: $"exit {r.ExitCode}: " +
            string.Join("\n", new[] { r.Output.TrimEnd('\n'), r.Error.TrimEnd('\n') }.Where(o => o.Length > 0)));
    }

    private SftpFileSystem Sftp(ToolCall c)
    {
        CheckDirect(c.S);
        return new SftpFileSystem(_host.Ssh.ConnectSftp(c.S, interactive: false));
    }

    private Task<ToolResult> ListDirectory(ToolCall c) => Task.Run(() =>
    {
        using var fs = Sftp(c);
        var path = fs.Normalize(c.Required("path"));
        var items = fs.List(path);
        return ToolResult.Data(new
        {
            path,
            entries = items.Select(i => new
            {
                name = i.Name, type = i.IsDirectory ? "dir" : i.IsLink ? "link" : "file", size = i.IsDirectory ? (long?)null : i.Size,
                mode = i.Permissions, owner = i.Owner, group = i.Group, modified = i.Modified,
            }).ToList(),
        }, $"{items.Count} entries in {path}");
    }, c.Ct);

    private Task<ToolResult> ReadFile(ToolCall c) => Task.Run(() =>
    {
        CheckDirect(c.S);
        using var file = new RemoteTextFile(_host.Ssh, c.S, c.Required("path"), interactive: false);
        string text;
        try
        {
            text = file.Load();
        }
        catch (NotTextException ex)
        {
            return ToolResult.Fail(ex.Message + " " + L.Get("Mcp.UseDownload"));
        }
        if (file.IsNew) return ToolResult.Fail(L.F("Mcp.NoSuchFile", file.Path));
        return new ToolResult(Cut(text, MaxReadBytes), LogText: $"{text.Length} chars" + (file.UsesSudo ? " (sudo)" : ""));
    }, c.Ct);

    private Task<ToolResult> WriteFile(ToolCall c) => Task.Run(() =>
    {
        CheckDirect(c.S);
        var content = c.Args["content"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? c.Args["content"]!.GetValue<string>() : null;
        if (content == null) return ToolResult.Fail("Missing argument: content");
        using var file = new RemoteTextFile(_host.Ssh, c.S, c.Required("path"), interactive: false);
        try
        {
            file.Load(); // learn the file's encoding and line endings (or that it is new)
        }
        catch (NotTextException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        try
        {
            file.Save(content);
        }
        catch (UnauthorizedAccessException) when (file.CanSudo)
        {
            file.Save(content, sudo: true);
        }
        return new ToolResult(L.F("Mcp.Written", file.Path, content.Length) + (file.UsesSudo ? " (sudo)" : "") + (file.IsNew ? " " + L.Get("Mcp.NewFile") : ""));
    }, c.Ct);

    private async Task<ToolResult> DeletePath(ToolCall c)
    {
        var s = c.S;
        CheckDirect(s);
        var raw = c.Required("path");
        using var fs = await Task.Run(() => Sftp(c), c.Ct);
        var path = fs.Normalize(raw).TrimEnd('/');
        if (path.Length == 0 || Protected.Contains(path) || path.Count(ch => ch == '/') < 1)
            throw new McpDeniedException(L.F("Mcp.ProtectedPath", raw));
        var item = await Task.Run(() => fs.Stat(path), c.Ct);
        if (item == null && !c.Bool("sudo")) return ToolResult.Fail(L.F("Mcp.NoSuchFile", path));
        var what = item?.IsDirectory == true ? L.F("Mcp.ConfirmDeleteDir", path, s.Name) : L.F("Mcp.ConfirmDeleteFile", path, s.Name);
        if (!await Confirm(c, what)) throw new McpDeniedException(L.Get("Mcp.UserDeclined"));
        if (c.Bool("sudo"))
        {
            var r = await Exec(c, "rm -rf -- " + RemoteShell.Quote(path), elevated: true, TimeSpan.FromMinutes(10));
            return r.Ok ? new ToolResult(L.F("Mcp.Deleted", path)) : ToolResult.Fail(Cut(r.Combined));
        }
        await Task.Run(() => fs.Delete(item!, c.Ct), c.Ct);
        return new ToolResult(L.F("Mcp.Deleted", path));
    }

    private string Allowed(ToolCall c, string path)
    {
        if (c.S.McpAccess != McpAccess.Full) throw new McpDeniedException(L.Get("Mcp.NoFolders"));
        return McpPolicy.LocalPath(path, c.S.McpFolders, out var error) ?? throw new McpDeniedException(error);
    }

    private Task<ToolResult> Upload(ToolCall c) => Task.Run(() =>
    {
        var local = Allowed(c, c.Required("local_path"));
        var localFs = new LocalFileSystem();
        var item = localFs.Stat(local) ?? throw new FileNotFoundException(L.F("Mcp.NoSuchFile", local));
        using var remote = Sftp(c);
        var dir = remote.Normalize(c.Required("remote_dir")).TrimEnd('/');
        if (dir.Length == 0) dir = "/";
        if (remote.Stat(dir) == null) CreateDirs(remote, dir);
        var job = new TransferJob(L.Get("Mcp.Upload"));
        Transfers.Run(job, localFs, [item], remote, dir, move: false, (_, _) => ConflictChoice.Overwrite);
        return TransferResult(job, $"{local} → {c.S.Name}:{dir}");
    }, c.Ct);

    private Task<ToolResult> Download(ToolCall c) => Task.Run(() =>
    {
        var localDir = Allowed(c, c.Required("local_dir"));
        Directory.CreateDirectory(localDir);
        using var remote = Sftp(c);
        var path = remote.Normalize(c.Required("remote_path"));
        var item = remote.Stat(path) ?? throw new FileNotFoundException(L.F("Mcp.NoSuchFile", path));
        var job = new TransferJob(L.Get("Mcp.Download"));
        Transfers.Run(job, remote, [item], new LocalFileSystem(), localDir, move: false, (_, _) => ConflictChoice.Overwrite);
        return TransferResult(job, $"{c.S.Name}:{path} → {localDir}");
    }, c.Ct);

    private static void CreateDirs(SftpFileSystem fs, string dir)
    {
        var path = "";
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + part;
            if (fs.Stat(path) == null) fs.CreateDirectory(path);
        }
    }

    private static ToolResult TransferResult(TransferJob job, string what)
    {
        var text = new StringBuilder($"{what}: {job.DoneFiles} file(s), {job.DoneBytes} bytes");
        if (job.Failures.Count > 0) text.Append("\n" + L.Get("Mcp.TransferFailures") + "\n" + string.Join('\n', job.Failures.Take(50)));
        if (job.Error != null) text.Append("\n" + job.Error);
        return job.State == TransferState.Done && job.Failures.Count == 0 ? new ToolResult(text.ToString()) : ToolResult.Fail(text.ToString());
    }

    private Task<ToolResult> ScriptResults(ToolCall c) =>
        Task.FromResult(ToolResult.Data(c.S.Attributes.Select(a => new
        {
            key = a.Key, label = a.Label, value = a.Value, source = a.Source, updated = a.Updated,
        }).ToList(), $"{c.S.Attributes.Count} values"));
}
