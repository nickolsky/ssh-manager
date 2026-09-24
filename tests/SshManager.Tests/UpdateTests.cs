using SshManager.Core.Updates;

namespace SshManager.Tests;

public class UpdateTests
{
    private const string Release = """
        {"tag_name":"v1.4.2","name":"SSH Manager 1.4.2","body":"notes","html_url":"https://github.com/nickolsky/ssh-manager/releases/tag/v1.4.2",
         "draft":false,"prerelease":false,"assets":[
          {"name":"SshManager-1.4.2-win-x64.zip","size":123,"browser_download_url":"https://github.com/nickolsky/ssh-manager/releases/download/v1.4.2/SshManager-1.4.2-win-x64.zip"},
          {"name":"SshManager-1.4.2-win-x64.zip.sha256","size":90,"browser_download_url":"https://github.com/nickolsky/ssh-manager/releases/download/v1.4.2/SshManager-1.4.2-win-x64.zip.sha256"}]}
        """;

    [Fact]
    public void Parses_A_Release()
    {
        var r = UpdateService.Parse(Release)!;
        Assert.Equal(new Version(1, 4, 2), r.Version);
        Assert.EndsWith("-win-x64.zip", r.ZipUrl);
        Assert.EndsWith(".sha256", r.ShaUrl);
        Assert.Equal(123, r.Size);

        // no package, a pre-release, a download from somewhere else: not an update
        Assert.Null(UpdateService.Parse("""{"tag_name":"v2.0.0","assets":[]}"""));
        Assert.Null(UpdateService.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true")));
        Assert.Null(UpdateService.Parse(Release.Replace("https://github.com/nickolsky/ssh-manager/releases/download/v1.4.2/SshManager-1.4.2-win-x64.zip\"", "https://evil.example/x.zip\"")));
    }

    [Fact]
    public void Parses_Versions()
    {
        Assert.Equal(new Version(1, 2, 3), UpdateService.ParseVersion("v1.2.3"));
        Assert.Equal(new Version(1, 2, 0), UpdateService.ParseVersion("1.2"));
        Assert.Equal(new Version(2, 0, 0), UpdateService.ParseVersion("v2"));
        Assert.Equal(new Version(1, 5, 0), UpdateService.ParseVersion("v1.5.0-beta"));
        Assert.Null(UpdateService.ParseVersion("latest"));
        Assert.True(UpdateService.Current >= new Version(1, 0, 0));
    }

    [Fact]
    public void Apply_Replaces_Files_In_Use_And_Keeps_Data()
    {
        using var tmp = new TempDir();
        var app = Directory.CreateDirectory(Path.Combine(tmp.Path, "app")).FullName;
        var pkg = Directory.CreateDirectory(Path.Combine(tmp.Path, "pkg", "SshManager")).FullName;
        File.WriteAllText(Path.Combine(app, "SshManager.exe"), "old exe");
        File.WriteAllText(Path.Combine(app, "keep.txt"), "not in the package");
        Directory.CreateDirectory(Path.Combine(app, "data"));
        File.WriteAllText(Path.Combine(app, "data", "vault.dat"), "secret");
        File.WriteAllText(Path.Combine(pkg, "SshManager.exe"), "new exe");
        Directory.CreateDirectory(Path.Combine(pkg, "Assets"));
        File.WriteAllText(Path.Combine(pkg, "Assets", "x.js"), "new asset");
        Directory.CreateDirectory(Path.Combine(pkg, "data"));
        File.WriteAllText(Path.Combine(pkg, "data", "vault.dat"), "must not land");

        var root = UpdateService.PackageRoot(Path.Combine(tmp.Path, "pkg"));
        Assert.Equal(pkg, root);
        // a running process keeps its exe open: it can be renamed, not overwritten
        using (new FileStream(Path.Combine(app, "SshManager.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            UpdateService.Apply(root, app);

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(app, "SshManager.exe")));
        Assert.Equal("new asset", File.ReadAllText(Path.Combine(app, "Assets", "x.js")));
        Assert.Equal("not in the package", File.ReadAllText(Path.Combine(app, "keep.txt")));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(app, "data", "vault.dat")));
        Assert.True(File.Exists(Path.Combine(app, "SshManager.exe" + UpdateService.OldSuffix)));

        UpdateService.CleanupOld(app);
        Assert.False(File.Exists(Path.Combine(app, "SshManager.exe" + UpdateService.OldSuffix)));
    }

    [Fact]
    public void Apply_Rolls_Back_On_Failure()
    {
        using var tmp = new TempDir();
        var app = Directory.CreateDirectory(Path.Combine(tmp.Path, "app")).FullName;
        var pkg = Directory.CreateDirectory(Path.Combine(tmp.Path, "pkg")).FullName;
        File.WriteAllText(Path.Combine(app, "a.dll"), "old a");
        File.WriteAllText(Path.Combine(app, "b.dll"), "old b");
        File.WriteAllText(Path.Combine(pkg, "a.dll"), "new a");
        File.WriteAllText(Path.Combine(pkg, "b.dll"), "new b");
        File.WriteAllText(Path.Combine(pkg, "SshManager.exe"), "exe");
        // b.dll is open without delete sharing: it cannot even be renamed
        using (new FileStream(Path.Combine(app, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => UpdateService.Apply(pkg, app));
        Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
        Assert.Equal("old b", File.ReadAllText(Path.Combine(app, "b.dll")));
        Assert.False(File.Exists(Path.Combine(app, "SshManager.exe")));
    }
}
