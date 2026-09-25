using SshManager.Core.Models;

namespace SshManager.Core.Mcp;

/// <summary>What an agent may do: the server's access level, and which local folders it may copy to and from.</summary>
public static class McpPolicy
{
    public static bool Allows(McpAccess have, McpAccess need) => have != McpAccess.Off && have >= need;

    /// <summary>
    /// The real full path of <paramref name="path"/> when it lies inside one of <paramref name="folders"/>, else null with
    /// the reason. ".." is resolved, and so are links and junctions on the way (a link inside an allowed folder that
    /// points outside does not count as inside). Comparison ignores case, at folder boundaries.
    /// </summary>
    public static string? LocalPath(string path, IReadOnlyList<string> folders, out string error)
    {
        error = "";
        if (folders.Count == 0)
        {
            error = L.Get("Mcp.NoFolders");
            return null;
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
        {
            error = L.F("Mcp.BadLocalPath", path);
            return null;
        }
        var real = RealPath(path);
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f) || !Path.IsPathFullyQualified(f)) continue;
            var root = RealPath(f).TrimEnd('\\', '/');
            if (string.Equals(real.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase) ||
                real.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return real;
        }
        error = L.F("Mcp.FolderNotAllowed", path, string.Join("; ", folders));
        return null;
    }

    /// <summary>Full path with every existing link / junction on the way replaced by its final target.</summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.ResolveLinkTarget(true) is { } target)
                    next = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // not resolvable: keep the path as written
            }
            current = next;
        }
        return current;
    }
}
