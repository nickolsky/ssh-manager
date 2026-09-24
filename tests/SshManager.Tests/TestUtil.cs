using System.Diagnostics;

namespace SshManager.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("sshm-test-").FullName;
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class OpenSsh
{
    private static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH");

    public static bool Available => File.Exists(Exe("ssh-keygen"));

    public static string Exe(string name) => System.IO.Path.Combine(Dir, name + ".exe");

    public static (int Code, string Out, string Err) Run(string tool, IEnumerable<string> args,
        Dictionary<string, string>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(Exe(tool))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            p.Kill();
            throw new TimeoutException(tool + " timed out");
        }
        return (p.ExitCode, outTask.Result.Trim(), errTask.Result.Trim());
    }
}
