using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SshManager.Core.Pipes;

internal static class PipeFactory
{
    /// <summary>Named pipe instance accessible only to the current Windows user.</summary>
    public static NamedPipeServerStream Create(string name, bool first)
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.SetOwner(me);
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 0, 0, security);
    }
}

/// <summary>Accept loop: one server instance waiting at a time, each client handled on its own task.</summary>
public abstract class PipeServerBase : IDisposable
{
    private CancellationTokenSource? _cts;

    public string? PipeName { get; private set; }
    public bool IsRunning => PipeName != null;

    /// <summary>Tries each candidate name; returns false if all are taken by another process.</summary>
    protected bool Start(params string[] candidates)
    {
        foreach (var name in candidates)
        {
            NamedPipeServerStream first;
            try
            {
                first = PipeFactory.Create(name, first: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            PipeName = name;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(first, _cts.Token);
            return true;
        }
        return false;
    }

    private async Task AcceptLoop(NamedPipeServerStream server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync();
                server = PipeFactory.Create(PipeName!, first: false);
                continue;
            }

            var client = server;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(client, ct);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                               or FormatException or EndOfStreamException)
                {
                    // client went away or sent garbage
                }
                finally
                {
                    await client.DisposeAsync();
                }
            }, ct);

            server = PipeFactory.Create(PipeName!, first: false);
        }
    }

    protected abstract Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct);

    public void Dispose()
    {
        _cts?.Cancel();
        PipeName = null;
    }
}
