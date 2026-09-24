using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SshManager.Core.Ssh;

namespace SshManager.Core.Terminal;

/// <summary>
/// Shell integration for the built-in terminal: a small bash/zsh script that marks the prompt, the input and the
/// working directory with OSC 7337 sequences. It is written to ~/.cache/sshm over a separate channel and sourced
/// with one typed line whose echo (and the first prompt it was typed at) is cut out of the output.
/// </summary>
public static partial class ShellIntegration
{
    public const string Mark = "\u001b]7337;";
    public const string LoadedMark = Mark + "I\u0007";

    public static string Script { get; } = Load();
    public static string Hash { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Script)))[..10].ToLowerInvariant();

    /// <summary>Shells the script supports (the login shell's basename).</summary>
    public static bool Supports(string? shell) => shell is "bash" or "zsh";

    /// <summary>
    /// Writes the script (when this version is not there yet) and prints the login shell and the file path.
    /// Plain sh, so it works whatever the login shell is; the script goes in through a quoted here-document.
    /// </summary>
    public static string SetupCommand() =>
        $$"""
        d="$HOME/.cache/sshm"; f="$d/shell-{{Hash}}.sh"
        mkdir -p "$d" 2>/dev/null && chmod 700 "$d" 2>/dev/null
        for x in "$d"/shell-*.sh; do [ "$x" = "$f" ] || rm -f "$x"; done
        [ -f "$f" ] || cat > "$f" <<'__SSHM_EOF__'
        {{Script.TrimEnd()}}
        __SSHM_EOF__
        [ -f "$f" ] || exit 1
        printf '%s\n%s\n' "${SHELL##*/}" "$f"
        """;

    /// <summary>Login shell name and script path from the setup output.</summary>
    public static (string? Shell, string? File) ParseSetup(string output)
    {
        var lines = output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2 && lines[1].StartsWith('/') ? (lines[0].Trim(), lines[1].Trim()) : (null, null);
    }

    /// <summary>The typed line: leading space keeps it out of the history in most setups.</summary>
    public static string SourceLine(string file) => " . " + RemoteShell.Quote(file) + "\n";

    /// <summary>
    /// The output ends with something that can be a shell prompt, so typing there is safe: not empty, and not a
    /// question like "New password:" (typing the source line into it would be sent as the answer).
    /// </summary>
    public static bool LooksLikePrompt(string output)
    {
        var text = StripAnsi(output).Replace("\r\n", "\n");
        var cr = text.LastIndexOf('\r');
        var last = text[(text.LastIndexOf('\n') + 1)..];
        if (cr >= 0 && cr >= text.LastIndexOf('\n')) last = text[(cr + 1)..];
        last = last.TrimEnd();
        if (last.Length == 0) return false;
        if (last.Contains("password", StringComparison.OrdinalIgnoreCase) || last.Contains("пароль", StringComparison.OrdinalIgnoreCase))
            return false;
        // user@host:~$, [root@host ~]#, host%, ❯ (starship, p10k), ➜ (oh-my-zsh); anything else (a menu such as
        // zsh-newuser-install, "Press any key") is left alone
        return "$#%>❯»λ".Contains(last[^1]) || last.Contains('➜') || last.Contains('❯');
    }

    /// <summary>
    /// What to show once the integration loaded: the output before the source line up to its last line break
    /// (the first prompt is dropped, a new one follows) plus whatever came after the loaded mark.
    /// </summary>
    public static string Splice(string before, string after)
    {
        var i = after.IndexOf(LoadedMark, StringComparison.Ordinal);
        if (i < 0) return before + after;
        var keep = before[..(before.LastIndexOf('\n') + 1)];
        return keep + after[(i + LoadedMark.Length)..];
    }

    public static string StripAnsi(string s) => Ansi().Replace(s, "");

    // CSI, OSC, charset selection, then any other two-character escape (ESC =, ESC >, ESC 7…)
    [GeneratedRegex(@"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)?|[()#][0-9A-Za-z]|[0-~])")]
    private static partial Regex Ansi();

    private static string Load()
    {
        using var s = typeof(ShellIntegration).Assembly.GetManifestResourceStream("SshManager.Core.Terminal.shell-integration.sh")
                      ?? throw new InvalidOperationException("shell-integration.sh is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd().Replace("\r\n", "\n");
    }
}
