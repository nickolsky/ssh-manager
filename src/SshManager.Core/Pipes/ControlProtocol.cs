using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SshManager.Core.Pipes;

/// <summary>What the sshm.exe helper needs to start ssh.exe in its own console.</summary>
public sealed class LaunchSpec
{
    public string SshPath { get; set; } = "ssh.exe";
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string Title { get; set; } = "";
    public bool PauseOnError { get; set; }
    /// <summary>Keep the window open after ssh exits (scripts, logs).</summary>
    public bool PauseAlways { get; set; }
}

public sealed class ControlRequest
{
    /// <summary>activate | launch | connect | list | askpass</summary>
    public string Op { get; set; } = "";
    public string? Token { get; set; }
    public string? Name { get; set; }
    public string? Prompt { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public LaunchSpec? Spec { get; set; }
    public string? Value { get; set; }
    public List<string>? Names { get; set; }

    public static ControlResponse Fail(string error) => new() { Error = error };
}

/// <summary>Line-delimited JSON over the per-user control pipe.</summary>
public static class ControlClient
{
    public static async Task<ControlResponse> SendAsync(ControlRequest request, int connectTimeoutMs = 2000)
    {
        await using var pipe = new NamedPipeClientStream(".", AppPaths.ControlPipe, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync() ?? throw new IOException(L.Get("Ipc.Closed"));
        return JsonSerializer.Deserialize<ControlResponse>(line) ?? ControlResponse.Fail(L.Get("Ipc.Empty"));
    }
}

public sealed class ControlServer(Func<ControlRequest, Task<ControlResponse>> handler) : PipeServerBase
{
    public bool Start() => Start(AppPaths.ControlPipe);

    protected override async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(ct);
        if (line == null) return;
        ControlResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ControlRequest>(line) ?? new ControlRequest();
            response = await handler(request);
        }
        catch (Exception ex)
        {
            response = ControlResponse.Fail(ex.Message);
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), ct);
    }
}
