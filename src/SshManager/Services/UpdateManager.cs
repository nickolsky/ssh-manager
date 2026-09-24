using System.Diagnostics;
using System.Windows.Threading;
using SshManager.Core;
using SshManager.Core.Updates;

namespace SshManager.Services;

/// <summary>
/// Update state for the UI: checks GitHub Releases once a day (when enabled), downloads the package and restarts
/// into the new version. The side-by-side test copy never updates itself.
/// </summary>
public sealed class UpdateManager
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private readonly AppHost _host;
    private readonly UpdateService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(1) };
    private bool _busy;

    public UpdateManager(AppHost host)
    {
        _host = host;
        _timer.Tick += (_, _) => AutoCheck();
    }

    public Version Current => UpdateService.Current;
    public ReleaseInfo? Available { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy => _busy;
    public double Progress { get; private set; }
    public bool CanInstall => !AppPaths.IsSideBySide;
    public string ReleasesPage => _service.ReleasesPage;

    /// <summary>Status, progress or the available version changed (UI thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        _ = Task.Run(() => UpdateService.CleanupOld(AppPaths.ExeDir));
        _timer.Start();
        AutoCheck();
    }

    private void AutoCheck()
    {
        var s = _host.SettingsStore.Settings;
        if (!s.CheckUpdates || AppPaths.IsSideBySide) return;
        if (s.LastUpdateCheck is { } last && DateTime.UtcNow - last < CheckEvery) return;
        _ = CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        Set(L.Get("Update.Checking"));
        try
        {
            var release = await _service.CheckAsync();
            Available = release;
            var s = _host.SettingsStore.Settings;
            s.LastUpdateCheck = DateTime.UtcNow;
            _host.SettingsStore.Save();
            if (release == null) Set(L.F("Update.UpToDate", Current.ToString(3)));
            else
            {
                Set(L.F("Update.Available", release.Version.ToString(3)));
                // tell once per version, the settings page shows it anyway
                if (!manual && s.NotifiedUpdate != release.Version.ToString(3))
                {
                    s.NotifiedUpdate = release.Version.ToString(3);
                    _host.SettingsStore.Save();
                    _host.Notify(L.Get("Update.Title"), L.F("Update.AvailableBalloon", release.Version.ToString(3)));
                }
            }
        }
        catch (Exception ex)
        {
            Set(L.F("Update.CheckFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads, replaces the program files and restarts. False when it failed (see <see cref="Status"/>).</summary>
    public async Task<bool> InstallAsync()
    {
        if (Available is not { } release || _busy || !CanInstall) return false;
        _busy = true;
        Progress = 0;
        Set(L.F("Update.Downloading", release.Version.ToString(3)));
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            var package = await _service.DownloadAsync(release, progress);
            Set(L.Get("Update.Installing"));
            await Task.Run(() => UpdateService.Apply(package, AppPaths.ExeDir));
            // the new process waits for this one to exit before it takes the single-instance lock
            Process.Start(new ProcessStartInfo(AppPaths.MainExe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
            _host.Exit();
            return true;
        }
        catch (Exception ex)
        {
            Set(L.F("Update.InstallFailed", ex.Message));
            return false;
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    public void OpenReleasePage() =>
        Process.Start(new ProcessStartInfo(Available?.PageUrl is { Length: > 0 } url ? url : ReleasesPage) { UseShellExecute = true })?.Dispose();

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
