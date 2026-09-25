using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Agent;
using SshManager.Core.Backup;
using SshManager.Core.Forwarding;
using SshManager.Core.Geo;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Pipes;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;
using SshManager.Views;

namespace SshManager.Services;

/// <summary>Owns all long-lived services and the app lifecycle (tray, lock/unlock, IPC).</summary>
public sealed partial class AppHost : IDisposable
{
    private static readonly TimeSpan AgentPromptCooldown = TimeSpan.FromSeconds(30);
    /// <summary>Time for the user to accept the host key in the terminal before we log in in the background.</summary>
    private static readonly TimeSpan FirstInventoryDelay = TimeSpan.FromSeconds(45);

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _backupTimer;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private MainWindow? _main;
    private Task<bool>? _unlockTask;
    private DateTime _lastUnlockCancel = DateTime.MinValue;

    public AppHost(Dispatcher ui)
    {
        _ui = ui;
        SettingsStore.Load();
        L.Language = SettingsStore.Settings.Language;
        Agent = new SshAgentServer(Vault) { UnlockRequested = () => EnsureUnlockedAsync(fromAgent: true) };
        KnownHosts = new KnownHostsService();
        Launcher = new SessionLauncher(Vault, SettingsStore, Agent, KnownHosts);
        Ssh = new SshClientFactory(Vault, KnownHosts) { ConfirmHostKey = ConfirmHostKey };
        KeySetup = new KeySetupService(Vault, Ssh);
        Inventory = new ServerInventoryService(Vault, Ssh);
        Geo = new GeoIpService(Vault, SettingsStore);
        Health = new HealthMonitor(Vault, SettingsStore, Uptime);
        Metrics = new MetricsCollector(Ssh);
        Forwards = new PortForwardService(Vault, Ssh);
        Backup = new BackupService(Vault, SettingsStore, Ssh);
        Scripts = new ScriptRunner(Ssh);
        Updates = new UpdateManager(this);
        Control = new ControlServer(HandleControlAsync);
        Mcp = new McpService(this, _ui);

        Health.WentDown += (_, t) => _ui.BeginInvoke(() => OnServerDown(t));
        Health.PortWentDown += (_, t) => _ui.BeginInvoke(() => OnPortDown(t));
        Health.ServerOnline += (_, s) =>
        {
            if (SettingsStore.Settings.CollectMetrics && KnownHosts.IsKnown(s.Host, s.Port)) _ = Metrics.CollectAsync(s);
        };

        Vault.LockStateChanged += (_, _) => _ui.BeginInvoke(OnLockStateChanged);
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _backupTimer.Tick += (_, _) => RunAutoBackup();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public SettingsService SettingsStore { get; } = new();
    public VaultService Vault { get; } = new();
    public SshAgentServer Agent { get; }
    public KnownHostsService KnownHosts { get; }
    public SessionLauncher Launcher { get; }
    public SshClientFactory Ssh { get; }
    public KeySetupService KeySetup { get; }
    public ServerInventoryService Inventory { get; }
    public GeoIpService Geo { get; }
    public HealthMonitor Health { get; }
    public UptimeLog Uptime { get; } = new();
    public MetricsCollector Metrics { get; }
    public PortForwardService Forwards { get; }
    public BackupService Backup { get; }
    public ScriptRunner Scripts { get; }
    public UpdateManager Updates { get; }

    /// <summary>A tray notification (clicking it opens the main window).</summary>
    public void Notify(string title, string text) => _tray?.Balloon(title, text);

    // ---------- reboot ----------

    private readonly ConcurrentDictionary<Guid, DateTime> _rebooting = new();

    /// <summary>A reboot started or its wait ended.</summary>
    public event EventHandler<Guid>? RebootingChanged;

    public bool IsRebooting(Guid id) => _rebooting.ContainsKey(id);

    /// <summary>
    /// Reboots the server and, in the background, waits for it to come back (up to 10 minutes), then checks it and
    /// shows a tray notice. Returns once the server accepted the reboot. <paramref name="interactive"/>: may ask to trust
    /// an unknown host key (the menu; an agent may not).
    /// </summary>
    public async Task RebootAsync(Guid id, bool interactive)
    {
        if (!Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Clone(), out var server) || server == null)
            throw new InvalidOperationException(L.Get("Backup.ServerMissing"));
        if (!ServerInventoryService.Supported(server) || !string.IsNullOrWhiteSpace(server.ExtraArgs))
            throw new InvalidOperationException(L.Get("Files.NoJump"));
        if (!_rebooting.TryAdd(id, DateTime.Now)) throw new InvalidOperationException(L.F("Reboot.AlreadyRunning", server.Name));
        string bootId;
        try
        {
            bootId = await Task.Run(() => ServerPower.Reboot(Ssh, server, interactive));
        }
        catch
        {
            _rebooting.TryRemove(id, out _);
            throw;
        }
        RebootingChanged?.Invoke(this, id);
        _ = WaitAfterRebootAsync(server, bootId);
    }

    private async Task WaitAfterRebootAsync(ServerEntry server, string bootId)
    {
        try
        {
            var back = await ServerPower.WaitBackAsync(Ssh, server, bootId, TimeSpan.FromMinutes(10));
            _ui.Invoke(() =>
            {
                if (back is { } t) Notify(L.Get("Reboot.Title"), L.F("Reboot.Back", server.Name, (int)t.TotalSeconds));
                else _tray?.Balloon(L.Get("Reboot.Title"), L.F("Reboot.NotBack", server.Name), System.Windows.Forms.ToolTipIcon.Warning);
            });
        }
        finally
        {
            _rebooting.TryRemove(server.Id, out _);
            RebootingChanged?.Invoke(this, server.Id);
            Health.CheckNow(server.Id);
            _ = Inventory.RefreshAsync(server.Id);
        }
    }
    public ControlServer Control { get; }
    /// <summary>AI agents over MCP (stdio through sshm.exe, HTTP on 127.0.0.1).</summary>
    public McpService Mcp { get; }

    public event EventHandler? StateChanged;

    public void Start(bool startInTray)
    {
        Agent.Start();
        Control.Start();
        Mcp.ApplySettings();
        _tray = new TrayIcon(this);
        Updates.Start();
        _hotkey = new GlobalHotkey(ToggleMainWindow);
        ApplyHotkey();
        _idleTimer.Start();
        _backupTimer.Start();

        if (!Vault.Exists)
        {
            if (!ShowUnlockDialog())
            {
                Exit();
                return;
            }
            ShowMainWindow();
            return;
        }

        if (SettingsStore.Settings.Autostart) TryApplyAutostart(true);

        if (startInTray)
        {
            _tray.Balloon(AppPaths.ProductTitle, L.Get("Tray.StartedLocked"));
            return;
        }
        ShowMainWindow();
    }

    /// <summary>null = Windows language.</summary>
    public void SetLanguage(string? language)
    {
        SettingsStore.Settings.Language = language;
        SettingsStore.Save();
        L.Language = language;
        _tray?.UpdateState();
    }

    // ---------- windows ----------

    public void ShowMainWindow()
    {
        _ui.Invoke(() =>
        {
            if (!Vault.IsUnlocked && !ShowUnlockDialog()) return;
            _main ??= new MainWindow(this);
            if (!_main.IsVisible) _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            _main.Topmost = true;
            _main.Topmost = false;
            _main.Focus();
        });
    }

    public void OnMainWindowClosed() => _main = null;

    /// <summary>The global shortcut: brings the window up, or hides it when it is already in front.</summary>
    public void ToggleMainWindow()
    {
        if (_main is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized })
        {
            if (SettingsStore.Settings.CloseToTray) _main.Hide();
            else _main.WindowState = WindowState.Minimized;
            return;
        }
        ShowMainWindow();
    }

    /// <summary>(Re)registers the shortcut from the settings; false when it is taken or not valid.</summary>
    public bool ApplyHotkey() => _hotkey?.Set(SettingsStore.Settings.GlobalHotkey) ?? false;

    /// <summary>Off while the settings box records a new shortcut (so pressing the old one is not swallowed).</summary>
    public void SuspendHotkey() => _hotkey?.Set(null);

    public string? ActiveHotkey => _hotkey?.Current;

    /// <summary>Unlock (or first-run create) dialog. Must run on the UI thread.</summary>
    private bool ShowUnlockDialog()
    {
        if (Vault.IsUnlocked) return true;
        var dlg = new UnlockWindow(this, create: !Vault.Exists);
        var ok = dlg.ShowDialog() == true && Vault.IsUnlocked;
        if (!ok) _lastUnlockCancel = DateTime.UtcNow;
        return ok;
    }

    /// <summary>Makes sure the vault is unlocked, prompting the user once even for concurrent callers.</summary>
    public Task<bool> EnsureUnlockedAsync(bool fromAgent = false)
    {
        if (Vault.IsUnlocked) return Task.FromResult(true);
        if (fromAgent && (!SettingsStore.Settings.UnlockOnAgentRequest ||
                          DateTime.UtcNow - _lastUnlockCancel < AgentPromptCooldown))
            return Task.FromResult(false);

        return _ui.InvokeAsync(() =>
        {
            if (Vault.IsUnlocked) return Task.FromResult(true);
            if (_unlockTask != null) return _unlockTask;
            var tcs = new TaskCompletionSource<bool>();
            _unlockTask = tcs.Task;
            _ui.BeginInvoke(() =>
            {
                var ok = false;
                try
                {
                    ok = ShowUnlockDialog();
                }
                finally
                {
                    _unlockTask = null;
                    tcs.TrySetResult(ok);
                }
            });
            return tcs.Task;
        }).Task.Unwrap();
    }

    public void Lock()
    {
        Vault.Lock();
    }

    private void OnLockStateChanged()
    {
        if (!Vault.IsUnlocked)
        {
            Agent.ClearCache();
            Health.Stop();
            Metrics.Clear();
            _main?.Close();
            _main = null;
        }
        else
        {
            SyncBuiltinScripts();
            Health.Start();
            RefreshBackground(force: false);
        }
        _tray?.UpdateState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds the scripts shipped with the app and updates the ones the user did not edit.</summary>
    private void SyncBuiltinScripts()
    {
        try
        {
            var pending = Vault.Read(d =>
            {
                var copy = new VaultData { Scripts = d.Scripts.Select(s => s.Clone()).ToList(), RemovedBuiltins = [.. d.RemovedBuiltins] };
                return BuiltinScripts.Sync(copy);
            });
            if (pending) Vault.Update(d => BuiltinScripts.Sync(d), backup: false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // a vault that cannot be saved right now gets them on the next unlock
        }
    }

    /// <summary>
    /// GeoIP for every server; inventory for servers without data whose host key is already trusted
    /// (never prompts from the background).
    /// </summary>
    public void RefreshBackground(bool force)
    {
        if (!Vault.TryRead(d => d.Servers.Select(s => s.Clone()).ToList(), out var servers)) return;
        foreach (var s in servers)
        {
            _ = Geo.RefreshAsync(s.Id, force);
            if (!SettingsStore.Settings.AutoInventory && !force) continue;
            if (!KnownHosts.IsKnown(s.Host, s.Port)) continue;
            if (force ? ServerInventoryService.Supported(s) : s.Facts?.InventoryUpdated == null && Inventory.NeedsRefresh(s))
                _ = Inventory.RefreshAsync(s.Id);
        }
        if (force) Health.CheckAll();
    }

    /// <summary>Everything about one server, now (menu "Refresh info").</summary>
    public void RefreshServer(Guid id)
    {
        _ = Inventory.RefreshAsync(id, interactive: true);
        _ = Geo.RefreshAsync(id, force: true);
        Health.CheckNow(id);
    }

    /// <summary>Starts an ssh session; after the first one, learns the OS in the background.</summary>
    public void Launch(ServerEntry server)
    {
        OpenSession(server);
        AfterSessionStarted(server);
    }

    /// <summary>
    /// Opens a shell (or runs <paramref name="command"/>): in a tab of the main window with the built-in terminal,
    /// otherwise in Windows Terminal / a console. Jump hosts and extra ssh arguments need ssh.exe, so they always go outside.
    /// </summary>
    public void OpenSession(ServerEntry server, string? command = null, string? title = null)
    {
        if (!UseBuiltInTerminal(server))
        {
            Launcher.Launch(server, command, title);
            return;
        }
        ShowMainWindow();
        if (_main == null) return; // unlock cancelled
        _main.OpenTerminal(server, command, title);
        Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
            if (s != null) s.LastConnected = DateTime.Now;
        });
    }

    /// <summary>The server's file manager tab (SFTP). Not for servers behind a jump host.</summary>
    public void OpenFiles(ServerEntry server, string? dir = null)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost) || !string.IsNullOrWhiteSpace(server.ExtraArgs))
            throw new InvalidOperationException(L.Get("Files.NoJump"));
        ShowMainWindow();
        _main?.OpenFiles(server, dir);
    }

    /// <summary>A remote file in the built-in editor tab. Not for servers behind a jump host.</summary>
    public void OpenEditor(ServerEntry server, string path)
    {
        if (!string.IsNullOrWhiteSpace(server.JumpHost) || !string.IsNullOrWhiteSpace(server.ExtraArgs))
            throw new InvalidOperationException(L.Get("Files.NoJump"));
        ShowMainWindow();
        _main?.OpenEditor(server, path);
    }

    public bool UseBuiltInTerminal(ServerEntry server) =>
        SettingsStore.Settings.Terminal == TerminalMode.BuiltIn &&
        string.IsNullOrWhiteSpace(server.JumpHost) && string.IsNullOrWhiteSpace(server.ExtraArgs);

    public TerminalSession CreateTerminalSession(ServerEntry server) =>
        new(Ssh, server, SettingsStore.Settings.ServerAliveInterval, SettingsStore.Settings.TerminalIntegration);

    private void AfterSessionStarted(ServerEntry server)
    {
        _ = Geo.RefreshAsync(server.Id);
        if (!SettingsStore.Settings.AutoInventory || server.Facts?.OsId != null || !ServerInventoryService.Supported(server)) return;
        var id = server.Id;
        _ = Task.Run(async () =>
        {
            await Task.Delay(FirstInventoryDelay);
            if (Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Clone(), out var s) && s != null &&
                KnownHosts.IsKnown(s.Host, s.Port) && s.Facts?.OsId == null)
                await Inventory.RefreshAsync(id);
        });
    }

    public void Connect(Guid serverId) => _ui.InvokeAsync(() => ConnectAsync(serverId));

    private async Task ConnectAsync(Guid serverId)
    {
        if (!await EnsureUnlockedAsync()) return;
        var server = Vault.Data.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null) return;
        try
        {
            Launch(server);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, L.Get("Main.SessionFailed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ConfirmHostKey(HostKeyInfo info) => _ui.Invoke(() =>
    {
        var host = KnownHostsService.HostPattern(info.Host, info.Port);
        var text = info.Status == HostKeyStatus.Mismatch
            ? L.F("HostKey.Changed", host, info.KeyType, info.Fingerprint)
            : L.F("HostKey.New", host, info.KeyType, info.Fingerprint);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var icon = info.Status == HostKeyStatus.Mismatch ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var title = L.Get("HostKey.Title");
        var result = owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    });

    private void OnServerDown(HealthTransition t)
    {
        if (IsRebooting(t.ServerId)) return; // expected; the reboot reports its own result
        if (!SettingsStore.Settings.NotifyOnServerDown || !Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == t.ServerId)?.Name, out var name) || name == null)
            return;
        _tray?.Balloon(L.Get("Health.DownTitle"), L.F("Health.DownText", name, t.After.Error), System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void OnPortDown(PortTransition t)
    {
        if (IsRebooting(t.ServerId)) return;
        if (!SettingsStore.Settings.NotifyOnServerDown || !Vault.TryRead(d => d.Servers.FirstOrDefault(s => s.Id == t.ServerId)?.Name, out var name) || name == null)
            return;
        _tray?.Balloon(L.Get("Health.PortDownTitle"), L.F("Health.PortDownText", name, t.Port.Label, t.After.Error), System.Windows.Forms.ToolTipIcon.Warning);
    }

    // ---------- backup ----------

    private async void RunAutoBackup()
    {
        // a test instance works on a copy of the data; its backups would rotate the real ones away
        if (AppPaths.IsSideBySide || !Vault.IsUnlocked || !Backup.AutoDue) return;
        try
        {
            await Backup.RunAsync(interactive: false);
        }
        catch (Exception ex)
        {
            _tray?.Balloon(L.Get("Backup.Title"), L.Get("Backup.AutoFailed") + " " + ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Replaces the data folder with a backup (password already checked) and reopens the vault with it.
    /// Returns the snapshot of the previous data, which is the way back.
    /// </summary>
    public async Task<string> RestoreAsync(byte[] zip, string password)
    {
        var snapshot = await Task.Run(Backup.SnapshotBeforeRestore);
        Vault.Lock(); // drops the in-memory vault so nothing saves over the restored file
        await Task.Run(() => Backup.Extract(zip));
        SettingsStore.Load();
        L.Language = SettingsStore.Settings.Language;
        await Task.Run(() => Vault.Unlock(password));
        _ = _ui.BeginInvoke(ShowMainWindow);
        return snapshot;
    }

    public void Exit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void TryApplyAutostart(bool enabled)
    {
        if (AppPaths.IsSideBySide) return; // the Run entry belongs to the main copy
        try
        {
            Autostart.Apply(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _tray?.Balloon(L.Get("Settings.Autostart.Title"), L.Get("Settings.Autostart.Failed") + " " + ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    // ---------- IPC from sshm.exe ----------

    private async Task<ControlResponse> HandleControlAsync(ControlRequest req)
    {
        switch (req.Op)
        {
            case "activate":
                _ = _ui.BeginInvoke(ShowMainWindow);
                return new ControlResponse { Ok = true };

            case "launch":
            {
                var spec = req.Token == null ? null : Launcher.TakeLaunch(req.Token);
                return spec == null ? ControlResponse.Fail(L.Get("Cli.SessionNotFound")) : new ControlResponse { Ok = true, Spec = spec };
            }

            case "askpass":
            {
                var value = req.Token == null ? null : Launcher.AnswerAskPass(req.Token);
                return value == null ? ControlResponse.Fail(L.Get("Ipc.NoPassword")) : new ControlResponse { Ok = true, Value = value };
            }

            case "list":
                if (!await EnsureUnlockedAsync()) return ControlResponse.Fail(L.Get("Vault.Locked"));
                return new ControlResponse
                {
                    Ok = true,
                    Names = Vault.Read(d => d.Servers
                        .OrderBy(s => s.Group).ThenBy(s => s.Name)
                        .Select(s => $"{s.Name,-24} {s.Display,-32} {(s.Group ?? "")}").ToList()),
                };

            case "connect":
            {
                if (!await EnsureUnlockedAsync()) return ControlResponse.Fail(L.Get("Vault.Locked"));
                return await _ui.InvokeAsync(() =>
                {
                    var server = FindServer(req.Name ?? "", out var error);
                    if (server == null) return ControlResponse.Fail(error);
                    var spec = Launcher.BuildSpec(server, pauseOnError: false);
                    Vault.Update(d =>
                    {
                        var s = d.Servers.FirstOrDefault(x => x.Id == server.Id);
                        if (s != null) s.LastConnected = DateTime.Now;
                    });
                    AfterSessionStarted(server);
                    return new ControlResponse { Ok = true, Spec = spec };
                });
            }

            case "mcp":
            {
                if (string.IsNullOrEmpty(req.Payload)) return ControlResponse.Fail("no payload");
                // the MCP server unlocks the vault itself (only when a tool is called) and answers errors in JSON-RPC
                var answer = await Mcp.HandleAsync(req.Session ?? "stdio", req.Payload);
                return new ControlResponse { Ok = true, Value = answer };
            }

            case "cron":
            {
                if (!await EnsureUnlockedAsync()) return ControlResponse.Fail(L.Get("Vault.Locked"));
                var error = "";
                var server = Vault.Read(_ => FindServer(req.Name ?? "", out error)?.Clone());
                if (server == null) return ControlResponse.Fail(error);
                if (!ServerInventoryService.Supported(server)) return ControlResponse.Fail(L.Get("Files.NoJump"));
                var jobs = await Task.Run(() => CronCollector.Collect(Ssh, server));
                Vault.UpdateFacts(server.Id, f => f.CronJobs = jobs);
                return new ControlResponse { Ok = true, Names = CronCollector.Format(jobs) };
            }

            default:
                return ControlResponse.Fail(L.F("Ipc.UnknownOp", req.Op));
        }
    }

    private ServerEntry? FindServer(string name, out string error)
    {
        error = "";
        var servers = Vault.Data.Servers;
        var exact = servers.Where(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase) ||
                                       string.Equals(s.Host, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = exact.Count > 1 ? exact
            : servers.Where(s => s.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];
        error = partial.Count == 0
            ? L.F("Ipc.ServerNotFound", name)
            : L.Get("Ipc.ServerAmbiguous") + " " + string.Join(", ", partial.Select(s => s.Name));
        return null;
    }

    // ---------- auto-lock ----------

    private void CheckIdle()
    {
        var minutes = SettingsStore.Settings.AutoLockMinutes;
        if (minutes <= 0 || !Vault.IsUnlocked) return;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return;
        var idleMs = (uint)Environment.TickCount - info.dwTime;
        if (idleMs > minutes * 60_000u) Lock();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && SettingsStore.Settings.LockOnWindowsLock)
            _ui.BeginInvoke(Lock);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO info);

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _idleTimer.Stop();
        _backupTimer.Stop();
        Health.Dispose();
        Agent.Dispose();
        Control.Dispose();
        Mcp.Dispose();
        _tray?.Dispose();
        _tray = null;
        _hotkey?.Dispose();
        _hotkey = null;
    }
}
