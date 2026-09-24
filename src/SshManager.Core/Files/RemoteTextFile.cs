using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshManager.Core.Models;
using SshManager.Core.Ssh;

namespace SshManager.Core.Files;

/// <summary>How the file was stored, so saving writes it back the same way.</summary>
/// <param name="Encoding">"UTF-8" or "ISO-8859-1" (bytes that are not UTF-8, kept as they are).</param>
public sealed record TextFormat(bool Bom, bool Crlf, string Encoding, bool FinalNewline);

public sealed class NotTextException(string message) : Exception(message);

public static class TextFiles
{
    public const long MaxSize = 10 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Editor text (always \n) and the format; throws <see cref="NotTextException"/> for binary data.</summary>
    public static (string Text, TextFormat Format) Decode(byte[] bytes)
    {
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 8192));
        if (head.Contains((byte)0)) throw new NotTextException(L.Get("TextEd.Binary"));
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();
        string text;
        string encoding;
        try
        {
            text = StrictUtf8.GetString(body);
            encoding = "UTF-8";
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(body);
            encoding = "ISO-8859-1";
        }
        var crlf = text.Contains("\r\n") && text.Split("\r\n").Length - 1 >= text.Count(c => c == '\n') / 2;
        if (crlf) text = text.Replace("\r\n", "\n");
        return (text, new TextFormat(bom, crlf, encoding, text.EndsWith('\n')));
    }

    public static byte[] Encode(string text, TextFormat format)
    {
        text = text.Replace("\r\n", "\n");
        if (format.Crlf) text = text.Replace("\n", "\r\n");
        var body = format.Encoding == "UTF-8" ? Encoding.UTF8.GetBytes(text) : Encoding.Latin1.GetBytes(text);
        return format.Bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }
}

/// <summary>
/// One file open in the editor: read and written over SFTP as the login user, or through sudo when that user
/// may not (the stored password is fed to sudo on stdin). Writing goes into the existing file, so its owner and
/// permissions stay as they were.
/// </summary>
public sealed class RemoteTextFile(SshClientFactory ssh, ServerEntry server, string path) : IDisposable
{
    private SftpClient? _sftp;
    private DateTime? _modified;
    private long _size;

    public string Path { get; } = path;
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    /// <summary>Read (and saved) with sudo: the login user has no access.</summary>
    public bool UsesSudo { get; private set; }
    public bool IsNew { get; private set; }
    public TextFormat Format { get; private set; } = new(false, false, "UTF-8", true);

    private SftpClient Sftp()
    {
        if (_sftp is { IsConnected: true }) return _sftp;
        _sftp?.Dispose();
        _sftp = ssh.ConnectSftp(server);
        _sftp.OperationTimeout = TimeSpan.FromSeconds(60);
        return _sftp;
    }

    /// <summary>The text for the editor. Blocking.</summary>
    public string Load()
    {
        byte[] bytes;
        var sftp = Sftp();
        try
        {
            var a = sftp.GetAttributes(Path);
            if (a.IsDirectory) throw new NotTextException(L.Get("TextEd.IsFolder"));
            if (a.Size > TextFiles.MaxSize) throw new NotTextException(L.F("TextEd.TooLarge", TextFiles.MaxSize / 1024 / 1024));
            _modified = a.LastWriteTimeUtc;
            _size = a.Size;
        }
        catch (SftpPathNotFoundException)
        {
            // "edit new.conf": an empty file, created on save
            IsNew = true;
            return "";
        }
        catch (SftpPermissionDeniedException)
        {
            // cannot even stat it (a folder we may not enter); sudo below
        }
        try
        {
            bytes = sftp.ReadAllBytes(Path);
            UsesSudo = false;
        }
        catch (SftpPermissionDeniedException)
        {
            bytes = SudoRead();
            UsesSudo = true;
        }
        var (text, format) = TextFiles.Decode(bytes);
        Format = format;
        return text;
    }

    /// <summary>Changed on the server since it was loaded (or last saved).</summary>
    public bool ChangedOnServer()
    {
        if (IsNew || _modified == null) return false;
        try
        {
            var a = Sftp().GetAttributes(Path);
            return a.LastWriteTimeUtc != _modified || a.Size != _size;
        }
        catch (SshException)
        {
            return false;
        }
    }

    /// <summary>Writes the text back. Blocking.</summary>
    /// <param name="sudo">Retry through sudo after the login user was denied.</param>
    public void Save(string text, bool sudo = false)
    {
        var bytes = TextFiles.Encode(text, Format);
        if (UsesSudo || sudo) SudoWrite(bytes);
        else
        {
            try
            {
                using var s = Sftp().Open(Path, FileMode.Create, FileAccess.Write);
                s.Write(bytes);
            }
            catch (SftpPermissionDeniedException ex)
            {
                throw new UnauthorizedAccessException(ex.Message, ex);
            }
        }
        IsNew = false;
        try
        {
            var a = Sftp().GetAttributes(Path);
            _modified = a.LastWriteTimeUtc;
            _size = a.Size;
        }
        catch (SshException)
        {
        }
    }

    /// <summary>sudo can be tried: not root (root is never denied by SFTP for these reasons).</summary>
    public bool CanSudo => !RemoteShell.IsRoot(server);

    private byte[] SudoRead()
    {
        if (!CanSudo) throw new UnauthorizedAccessException(L.Get("TextEd.Denied"));
        using var client = ssh.Connect(server);
        var r = RemoteShell.Run(client, server, $"test -f {RemoteShell.Quote(Path)} || exit 3\nbase64 < {RemoteShell.Quote(Path)}", elevated: true,
            TimeSpan.FromSeconds(60));
        if (RemoteShell.SudoFailed(r)) throw new UnauthorizedAccessException(L.F("TextEd.SudoFailed", r.Error.Trim()));
        if (!r.Ok) throw new IOException(r.Combined);
        var bytes = Convert.FromBase64String(string.Concat(r.Output.Where(c => !char.IsWhiteSpace(c))));
        if (bytes.Length > TextFiles.MaxSize) throw new NotTextException(L.F("TextEd.TooLarge", TextFiles.MaxSize / 1024 / 1024));
        return bytes;
    }

    /// <summary>Uploads to a private temp file, then "sudo cat temp > file" (keeps the file's owner and mode).</summary>
    private void SudoWrite(byte[] bytes)
    {
        if (!CanSudo) throw new UnauthorizedAccessException(L.Get("TextEd.Denied"));
        var sftp = Sftp();
        var dir = sftp.WorkingDirectory.TrimEnd('/') + "/.cache/sshm";
        foreach (var d in new[] { sftp.WorkingDirectory.TrimEnd('/') + "/.cache", dir })
            if (!sftp.Exists(d))
            {
                sftp.CreateDirectory(d);
                if (d == dir) sftp.ChangePermissions(d, 700); // octal digits: rwx------
            }
        var tmp = $"{dir}/edit-{Guid.NewGuid():N}";
        try
        {
            using (var s = sftp.Open(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                sftp.ChangePermissions(tmp, 600);
                s.Write(bytes);
            }
            using var client = ssh.Connect(server);
            var q = RemoteShell.Quote(Path);
            var r = RemoteShell.Run(client, server, $"cat {RemoteShell.Quote(tmp)} > {q}", elevated: true, TimeSpan.FromSeconds(60));
            if (RemoteShell.SudoFailed(r)) throw new UnauthorizedAccessException(L.F("TextEd.SudoFailed", r.Error.Trim()));
            if (!r.Ok) throw new IOException(r.Combined);
            UsesSudo = true;
        }
        finally
        {
            try
            {
                sftp.DeleteFile(tmp);
            }
            catch (SshException)
            {
            }
        }
    }

    public void Dispose()
    {
        _sftp?.Dispose();
        _sftp = null;
    }
}
