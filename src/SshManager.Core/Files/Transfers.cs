namespace SshManager.Core.Files;

public enum ConflictChoice
{
    Overwrite,
    Skip,
    OverwriteAll,
    SkipAll,
    Cancel,
}

public enum TransferState
{
    Running,
    Done,
    Failed,
    Canceled,
}

/// <summary>Progress of one copy / move between panels; <see cref="Changed"/> fires on a worker thread.</summary>
public sealed class TransferJob(string title)
{
    private readonly CancellationTokenSource _cts = new();

    public string Title { get; } = title;
    public TransferState State { get; internal set; } = TransferState.Running;
    public long TotalBytes { get; internal set; }
    public long DoneBytes { get; internal set; }
    public int TotalFiles { get; internal set; }
    public int DoneFiles { get; internal set; }
    public int Skipped { get; internal set; }
    public string? Current { get; internal set; }
    public string? Error { get; internal set; }
    /// <summary>Files that failed (the job goes on with the rest).</summary>
    public List<string> Failures { get; } = [];
    public DateTime Started { get; } = DateTime.Now;
    public CancellationToken Token => _cts.Token;

    public event Action? Changed;

    public void Cancel() => _cts.Cancel();

    /// <summary>The job could not start (no connection).</summary>
    public void Fail(string error)
    {
        State = TransferState.Failed;
        Error = error;
        Notify();
    }

    internal void Notify() => Changed?.Invoke();
}

/// <summary>Copies (or moves) files and folders between two file systems with progress and cancellation.</summary>
public static class Transfers
{
    private sealed record Step(FileItem Source, string Target);

    /// <param name="ask">Target exists: what to do (called on the worker thread; the caller marshals to the UI).</param>
    public static void Run(TransferJob job, IFileSystem from, IReadOnlyList<FileItem> items, IFileSystem to, string targetDir,
        bool move, Func<FileItem, FileItem, ConflictChoice> ask)
    {
        var ct = job.Token;
        ConflictChoice? always = null;
        try
        {
            // plan: every folder and file under the selection
            var dirs = new List<Step>();
            var files = new List<Step>();
            foreach (var item in items) Plan(from, to, item, to.Combine(targetDir, item.Name), dirs, files, 0, ct);
            job.TotalFiles = files.Count;
            job.TotalBytes = files.Sum(f => f.Source.Size);
            job.Notify();

            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                if (to.Stat(d.Target) is not { IsDirectory: true }) to.CreateDirectory(d.Target);
            }

            var failed = false;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                job.Current = f.Source.Name;
                job.Notify();
                if (to.Stat(f.Target) is { } existing)
                {
                    var choice = always ?? ask(f.Source, existing);
                    if (choice is ConflictChoice.OverwriteAll) always = ConflictChoice.Overwrite;
                    if (choice is ConflictChoice.SkipAll) always = ConflictChoice.Skip;
                    if (choice == ConflictChoice.Cancel) throw new OperationCanceledException(ct);
                    if (choice is ConflictChoice.Skip or ConflictChoice.SkipAll)
                    {
                        job.Skipped++;
                        job.DoneBytes += f.Source.Size;
                        job.DoneFiles++;
                        job.Notify();
                        continue;
                    }
                }
                var before = job.DoneBytes;
                try
                {
                    CopyFile(from, f.Source, to, f.Target, n =>
                    {
                        job.DoneBytes += n;
                        job.Notify();
                    }, ct);
                    if (f.Source.Modified is { } m) to.SetModified(f.Target, m.ToUniversalTime());
                    if (move) from.Delete(f.Source, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed = true;
                    job.Failures.Add($"{f.Source.Path}: {ex.Message}");
                    job.DoneBytes = before + f.Source.Size;
                }
                job.DoneFiles++;
                job.Notify();
            }
            // moved folders are empty now (unless something failed or was skipped)
            if (move && !failed && job.Skipped == 0)
                foreach (var d in dirs.AsEnumerable().Reverse())
                    try
                    {
                        from.Delete(d.Source with { IsDirectory = true }, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                    }
            job.State = failed ? TransferState.Failed : TransferState.Done;
            if (failed) job.Error = string.Join("\n", job.Failures.Take(5));
        }
        catch (OperationCanceledException)
        {
            job.State = TransferState.Canceled;
        }
        catch (Exception ex)
        {
            job.State = TransferState.Failed;
            job.Error = ex.Message;
        }
        job.Current = null;
        job.Notify();
    }

    private static void Plan(IFileSystem from, IFileSystem to, FileItem item, string target, List<Step> dirs, List<Step> files,
        int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!item.IsDirectory)
        {
            files.Add(new Step(item, target));
            return;
        }
        dirs.Add(new Step(item, target));
        // a symlink to a folder is followed only when it was selected itself (no loops)
        if (item.IsLink && depth > 0) return;
        foreach (var child in from.List(item.Path)) Plan(from, to, child, to.Combine(target, child.Name), dirs, files, depth + 1, ct);
    }

    private static void CopyFile(IFileSystem from, FileItem source, IFileSystem to, string target, Action<long> progress,
        CancellationToken ct)
    {
        if (to is SftpFileSystem remote && !from.IsRemote)
        {
            using var input = new ProgressStream(from.OpenRead(source.Path), progress, ct);
            remote.Upload(input, target);
        }
        else if (from is SftpFileSystem source2 && !to.IsRemote)
        {
            var ok = false;
            try
            {
                using (var output = new ProgressStream(to.Create(target), progress, ct)) source2.Download(source.Path, output);
                ok = true;
            }
            finally
            {
                // no half-downloaded files left behind
                if (!ok)
                    try
                    {
                        File.Delete(target);
                    }
                    catch (IOException)
                    {
                    }
            }
        }
        else
        {
            using var input = new ProgressStream(from.OpenRead(source.Path), progress, ct);
            using var output = to.Create(target);
            input.CopyTo(output, 1 << 16);
        }
    }
}

/// <summary>Counts bytes read or written and stops the transfer when canceled.</summary>
internal sealed class ProgressStream(Stream inner, Action<long> progress, CancellationToken ct) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        var n = inner.Read(buffer, offset, count);
        if (n > 0) progress(n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
        progress(count);
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
