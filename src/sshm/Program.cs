using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SshManager.Core;
using SshManager.Core.Pipes;

namespace SshManager.Cli;

/// <summary>
/// Console helper for SSH Manager:
///   sshm tab &lt;token&gt;   — started by the GUI inside a terminal tab, runs ssh.exe
///   sshm &lt;server&gt;      — connect by server name from any terminal
///   sshm list          — list saved servers
/// It is also SSH_ASKPASS for password sessions (ssh calls it with the prompt as the only argument).
/// </summary>
internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        var askToken = Environment.GetEnvironmentVariable("SSHM_ASKPASS_TOKEN");
        if (!string.IsNullOrEmpty(askToken) && (args.Length == 0 || !IsCommand(args[0])))
            return await AskPass(askToken, args.Length > 0 ? args[0] : "");

        try
        {
            return args switch
            {
                ["tab", var token, ..] => await RunTab(token),
                ["list"] => await List(),
                [] or ["help"] or ["-h"] or ["--help"] or ["/?"] => Help(),
                ["connect", var name] => await ConnectByName(name),
                [var name] => await ConnectByName(name),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sshm: " + ex.Message);
            return 1;
        }
    }

    private static bool IsCommand(string a) => a is "tab" or "list" or "connect" or "help";

    private static int Help()
    {
        Console.WriteLine("""
            SSH Manager — консольный помощник
              sshm <имя сервера|хост>   подключиться к сохранённому серверу в этом окне
              sshm list                 список серверов
            """);
        return 0;
    }

    private static async Task<int> RunTab(string token)
    {
        var r = await Send(new ControlRequest { Op = "launch", Token = token }, startApp: false);
        if (!r.Ok || r.Spec == null)
        {
            Console.Error.WriteLine("sshm: " + (r.Error ?? "сессия не найдена или устарела"));
            Pause();
            return 1;
        }
        return RunSsh(r.Spec);
    }

    private static async Task<int> ConnectByName(string name)
    {
        var r = await Send(new ControlRequest { Op = "connect", Name = name }, startApp: true);
        if (!r.Ok || r.Spec == null)
        {
            Console.Error.WriteLine("sshm: " + (r.Error ?? "не удалось подключиться"));
            return 1;
        }
        return RunSsh(r.Spec);
    }

    private static async Task<int> List()
    {
        var r = await Send(new ControlRequest { Op = "list" }, startApp: true);
        if (!r.Ok)
        {
            Console.Error.WriteLine("sshm: " + r.Error);
            return 1;
        }
        foreach (var n in r.Names ?? []) Console.WriteLine(n);
        return 0;
    }

    private static int RunSsh(LaunchSpec spec)
    {
        if (spec.PauseOnError && !string.IsNullOrEmpty(spec.Title)) Console.Title = spec.Title;
        var psi = new ProcessStartInfo(spec.SshPath) { UseShellExecute = false };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        // ssh handles Ctrl+C itself; the helper must not die underneath it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0 && spec.PauseOnError) Pause();
        return p.ExitCode;
    }

    private static async Task<ControlResponse> Send(ControlRequest req, bool startApp)
    {
        try
        {
            return await ControlClient.SendAsync(req);
        }
        catch (TimeoutException) when (startApp && File.Exists(AppPaths.MainExe))
        {
            Console.Error.WriteLine("sshm: запускаю SSH Manager…");
            Process.Start(new ProcessStartInfo(AppPaths.MainExe, "--tray") { UseShellExecute = true })?.Dispose();
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    return await ControlClient.SendAsync(req, 1000);
                }
                catch (TimeoutException)
                {
                }
            }
            return ControlResponse.Fail("SSH Manager не отвечает");
        }
        catch (TimeoutException)
        {
            return ControlResponse.Fail("SSH Manager не запущен");
        }
    }

    // ---------- SSH_ASKPASS ----------

    private static async Task<int> AskPass(string token, string prompt)
    {
        if (prompt.Contains("(yes/no", StringComparison.OrdinalIgnoreCase))
        {
            var yes = MessageBoxW(IntPtr.Zero, prompt, "SSH Manager — новый сервер",
                0x4 /*YESNO*/ | 0x30 /*WARNING*/ | 0x40000 /*TOPMOST*/) == 6;
            return Answer(yes ? "yes" : "no");
        }

        if (prompt.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("пароль", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var r = await ControlClient.SendAsync(new ControlRequest { Op = "askpass", Token = token, Prompt = prompt });
                if (r.Ok && r.Value != null) return Answer(r.Value);
                WriteConsole("\r\nSSH Manager: сохранённый пароль не подошёл или сессия устарела.\r\n");
            }
            catch (TimeoutException)
            {
                WriteConsole("\r\nSSH Manager не запущен — пароль недоступен.\r\n");
            }
            return 1;
        }

        // Anything else (OTP codes, etc.): ask the user directly in the terminal.
        var line = ReadConsoleLine(prompt);
        return line == null ? 1 : Answer(line);
    }

    private static int Answer(string value)
    {
        using var stdout = Console.OpenStandardOutput();
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        stdout.Write(bytes);
        return 0;
    }

    private static void WriteConsole(string text)
    {
        try
        {
            using var con = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            var b = Encoding.UTF8.GetBytes(text);
            con.Write(b);
        }
        catch (IOException)
        {
        }
    }

    private static string? ReadConsoleLine(string prompt)
    {
        try
        {
            WriteConsole(prompt);
            using var con = new FileStream("CONIN$", FileMode.Open, FileAccess.Read);
            using var reader = new StreamReader(con, Console.InputEncoding);
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Pause()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Нажмите любую клавишу, чтобы закрыть окно…");
        try
        {
            Console.ReadKey(true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
