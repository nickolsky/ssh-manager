using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SshManager.Core.Mcp;

/// <summary>
/// The agent log as a terminal session (ANSI text for xterm.js): every call is a prompt with the command, then its output.
/// run_command shows the shell command itself, other tools show as "tool --arg value".
/// </summary>
public sealed partial class AgentConsole
{
    private const string Reset = "\x1b[0m", Dim = "\x1b[90m", Red = "\x1b[31m", Green = "\x1b[1;32m", Blue = "\x1b[1;34m",
        Cyan = "\x1b[36m", Yellow = "\x1b[33m", Bold = "\x1b[1m";

    private DateTime? _day;

    /// <summary>Starts over (the date line is written again before the next entry).</summary>
    public void Clear() => _day = null;

    /// <summary>One entry: a date line when the day changes, the prompt with the command, the output, a blank line.</summary>
    public string Render(AgentLogEntry e)
    {
        var sb = new StringBuilder();
        var local = e.Utc.ToLocalTime();
        if (_day != local.Date)
        {
            _day = local.Date;
            sb.Append(Dim).Append("──── ").Append(local.ToString("yyyy-MM-dd, dddd", CultureInfo.CurrentCulture)).Append(" ────").Append(Reset).Append("\r\n");
        }
        var mark = e.Outcome switch { AgentOutcome.Ok => "", AgentOutcome.Failed => $" {Red}✗{Reset}", _ => $" {Red}⛔{Reset}" };
        sb.Append(Dim).Append('[').Append(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(" +")
            .Append(e.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("s]").Append(Reset).Append(mark).Append(' ')
            .Append(Green).Append(Clean(e.Client.Length > 0 ? e.Client : "agent", keepColors: false))
            .Append(e.ServerName.Length > 0 ? "@" + Clean(e.ServerName, keepColors: false) : "").Append(Reset)
            .Append(':').Append(Blue).Append('~').Append(Reset).Append(Sudo(e) ? "# " : "$ ");
        sb.Append(Command(e)).Append(Reset).Append("\r\n");

        var (output, exit) = Output(e);
        if (output.Length > 0)
        {
            var color = e.Outcome == AgentOutcome.Ok ? "" : Red;
            sb.Append(color).Append(Lines(Clean(output, keepColors: e.Outcome == AgentOutcome.Ok))).Append(Reset).Append("\r\n");
        }
        if (e.Outcome == AgentOutcome.Denied) sb.Append(Red).Append("[denied]").Append(Reset).Append("\r\n");
        else if (exit is { } code and not 0) sb.Append(Red).Append("[exit ").Append(code).Append(']').Append(Reset).Append("\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>What the "user" typed: the shell command of run_command, else the tool with its arguments.</summary>
    private static string Command(AgentLogEntry e)
    {
        JsonObject? args = null;
        try
        {
            args = e.Args.Length > 0 ? JsonNode.Parse(e.Args) as JsonObject : null;
        }
        catch (JsonException)
        {
            // cut at the length limit: shown as it is
        }
        if (e.Tool == "run_command")
        {
            var command = args?["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : CommandFromCut(e.Args);
            if (command != null) return Bold + Lines(Clean(command, keepColors: false));
        }
        var sb = new StringBuilder(Cyan).Append(e.Tool).Append(Reset);
        if (args == null)
        {
            if (e.Args.Length > 0) sb.Append(' ').Append(Yellow).Append(Lines(Clean(e.Args, keepColors: false))).Append(Reset);
            return sb.ToString();
        }
        foreach (var (key, value) in args)
        {
            var text = value is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : value?.ToJsonString(McpServer.JsonLine) ?? "null";
            sb.Append(' ').Append(Dim).Append("--").Append(key).Append(Reset).Append(' ').Append(Yellow).Append(Lines(Clean(Quote(text), keepColors: false))).Append(Reset);
        }
        return sb.ToString();
    }

    /// <summary>Run as root (sudo: true), shown with root's prompt "#" as in a terminal.</summary>
    private static bool Sudo(AgentLogEntry e) => e.Tool == "run_command" && e.Args.Contains("\"sudo\":true", StringComparison.Ordinal);

    /// <summary>The command of a run_command whose arguments were cut in the log (no closing quote).</summary>
    private static string? CommandFromCut(string args)
    {
        var m = CutCommandRegex().Match(args);
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        if (raw.EndsWith('\\')) raw = raw[..^1];
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"");
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static string Quote(string s) =>
        s.Length > 0 && !s.Any(ch => char.IsWhiteSpace(ch) || "\"'$`\\|&;<>(){}*?!#".Contains(ch)) ? s : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>The output to show and the exit code, for results that start with "exit N: " (run_command).</summary>
    private static (string Output, int? Exit) Output(AgentLogEntry e)
    {
        var text = e.Result;
        int? exit = null;
        var m = ExitRegex().Match(text);
        if (m.Success)
        {
            exit = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            text = text[m.Length..];
        }
        var trimmed = text.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == '{' && trimmed[^1] == '}' || trimmed[0] == '[' && trimmed[^1] == ']'))
        {
            try
            {
                text = JsonNode.Parse(trimmed)?.ToJsonString(McpServer.Json) ?? text;
            }
            catch (JsonException)
            {
            }
        }
        return (text.TrimEnd('\n', ' '), exit);
    }

    /// <summary>xterm line breaks.</summary>
    private static string Lines(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>
    /// Text safe to write to the terminal: colours (SGR) may stay, every other escape sequence and control character goes,
    /// so logged output cannot clear the screen, move the cursor or set the title.
    /// </summary>
    internal static string Clean(string s, bool keepColors)
    {
        s = EscapeRegex().Replace(s, m => keepColors && m.Value.StartsWith("\x1b[") && m.Value.EndsWith('m') && SgrRegex().IsMatch(m.Value) ? m.Value : "");
        return ControlRegex().Replace(s, "");
    }

    [GeneratedRegex(@"^exit (-?\d+): ?")]
    private static partial Regex ExitRegex();

    [GeneratedRegex("""^\{"command":"((?:[^"\\]|\\.)*\\?)""")]
    private static partial Regex CutCommandRegex();

    // CSI … final byte, OSC … BEL/ST, any other ESC + one character
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?|\x1b[@-_]?")]
    private static partial Regex EscapeRegex();

    [GeneratedRegex(@"^\x1b\[[0-9;]*m$")]
    private static partial Regex SgrRegex();

    // ESC is left for the colours kept above
    [GeneratedRegex(@"[\x00-\x08\x0b-\x1a\x1c-\x1f\x7f]")]
    private static partial Regex ControlRegex();
}
