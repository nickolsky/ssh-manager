using System.Windows;
using System.Windows.Threading;
using SshManager.Core;
using SshManager.Core.Pipes;
using SshManager.Services;

namespace SshManager;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var tray = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(true, AppPaths.SingleInstanceLock, out var isFirst);
        if (!isFirst)
        {
            // Already running: bring it to front (unless this is just the autostart entry).
            if (!tray)
            {
                try
                {
                    ControlClient.SendAsync(new ControlRequest { Op = "activate" }).Wait(3000);
                }
                catch (AggregateException)
                {
                }
            }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        Host = new AppHost(Dispatcher);
        Host.Start(tray);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, L.Get("App.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
