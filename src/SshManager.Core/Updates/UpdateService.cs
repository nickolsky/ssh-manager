using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SshManager.Core.Updates;

/// <summary>A published version on GitHub Releases.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string ZipUrl, string? ShaUrl,
    long Size);

/// <summary>
/// Updates from GitHub Releases: the release's SshManager-X.Y.Z-win-x64.zip (checked against its .sha256) replaces the
/// program files. Files in use (the running exe, a helper still open in a terminal) cannot be overwritten but can be
/// renamed, so every replaced file is renamed to *.sshm-old first and removed on the next start. The data folder is
/// never part of the package and never touched.
/// </summary>
public sealed class UpdateService(string repository = UpdateService.Repository)
{
    public const string Repository = "nickolsky/ssh-manager";
    public const string OldSuffix = ".sshm-old";

    // Current first: the client's User-Agent uses it
    public static Version Current { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    public string ReleasesPage => $"https://github.com/{repository}/releases";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SshManager", Current.ToString(3)));
        return http;
    }

    /// <summary>The latest release when it is newer than this build, otherwise null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException(L.Get("Update.NoReleases"));
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var release = Parse(json);
        return release != null && release.Version > Current ? release : null;
    }

    /// <summary>A GitHub "release" object; null when it has no package for this program.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = S(r, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;
        string? zip = null, sha = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = S(a, "name");
                if (name.StartsWith("SshManager-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip = S(a, "browser_download_url");
                    size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                }
                else if (name.EndsWith("-win-x64.zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = S(a, "browser_download_url");
            }
        if (zip == null || !IsGitHub(zip) || sha != null && !IsGitHub(sha)) return null;
        var title = S(r, "name");
        return new ReleaseInfo(version, tag, title.Length > 0 ? title : tag, S(r, "body"), S(r, "html_url"), zip, sha, size);
    }

    /// <summary>"v1.2.3" / "1.2" → 1.2.3 / 1.2.0.</summary>
    public static Version? ParseVersion(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t.Contains('.') ? t : t + ".0", out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool IsGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com"));

    /// <summary>Downloads and unpacks the package; returns the folder with SshManager.exe.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "SshManager-update", release.Version.ToString(3));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "package.zip");
        using (var resp = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.Size;
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        if (release.ShaUrl != null)
        {
            var expected = (await Http.GetStringAsync(release.ShaUrl, ct).ConfigureAwait(false)).Trim().Split(' ', '\t')[0];
            await using var f = File.OpenRead(zip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.Get("Update.BadChecksum"));
        }
        var staging = Path.Combine(root, "files");
        ZipFile.ExtractToDirectory(zip, staging);
        return PackageRoot(staging);
    }

    /// <summary>The folder with SshManager.exe: the archive root or its single top folder.</summary>
    public static string PackageRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "SshManager.exe"))) return staging;
        var dirs = Directory.GetDirectories(staging);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "SshManager.exe"))) return dirs[0];
        throw new InvalidDataException(L.Get("Update.BadPackage"));
    }

    /// <summary>
    /// Copies the package over <paramref name="appDir"/>: each existing file is renamed to *.sshm-old first (works
    /// for files in use), then the new one is copied. Any failure puts the old files back.
    /// </summary>
    public static void Apply(string package, string appDir)
    {
        var moved = new List<(string Target, string Old)>();
        var created = new List<string>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(package, src);
                if (IsProtected(rel)) continue;
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old))
                    {
                        try
                        {
                            File.Delete(old);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            old = $"{target}.{Guid.NewGuid():N}{OldSuffix}"; // an older copy is still in use
                        }
                    }
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                else created.Add(target);
                File.Copy(src, target);
            }
        }
        catch
        {
            foreach (var c in created)
                try
                {
                    File.Delete(c);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            foreach (var (target, old) in moved.AsEnumerable().Reverse())
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(old, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            throw;
        }
    }

    /// <summary>Never overwritten by a package: user data and the side-by-side marker.</summary>
    private static bool IsProtected(string rel)
    {
        var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("data", StringComparison.OrdinalIgnoreCase) || first.StartsWith("data-", StringComparison.OrdinalIgnoreCase) ||
               first.Equals(AppPaths.InstanceFile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes the renamed files of the previous version (those no longer in use).</summary>
    public static void CleanupOld(string appDir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(appDir, "*" + OldSuffix, SearchOption.AllDirectories))
                try
                {
                    File.Delete(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
