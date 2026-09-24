using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Agent;
using SshManager.Core.Pipes;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;
using SshManager.Views;

namespace SshManager.Services;

/// <summary>Owns all long-lived services and the app lifecycle (tray, lock/unlock, IPC).</summary>
public sealed partial class AppHost : IDisposable
{
    private static readonly TimeSpan AgentPromptCooldown = TimeSpan.FromSeconds(30);

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _idleTimer;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private Task<bool>? _unlockTask;
    private DateTime _lastUnlockCancel = DateTime.MinValue;

    public AppHost(Dispatcher ui)
    {
        _ui = ui;
        SettingsStore.Load();
        Agent = new SshAgentServer(Vault) { UnlockRequested = () => EnsureUnlockedAsync(fromAgent: true) };
        KnownHosts = new KnownHostsService();
        Launcher = new SessionLauncher(Vault, SettingsStore, Agent, KnownHosts);
        KeySetup = new KeySetupService(Vault, KnownHosts) { ConfirmHostKey = ConfirmHostKey };
        Control = new ControlServer(HandleControlAsync);

        Vault.LockStateChanged += (_, _) => _ui.BeginInvoke(OnLockStateChanged);
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public SettingsService SettingsStore { get; } = new();
    public VaultService Vault { get; } = new();
    public SshAgentServer Agent { get; }
    public KnownHostsService KnownHosts { get; }
    public SessionLauncher Launcher { get; }
    public KeySetupService KeySetup { get; }
    public ControlServer Control { get; }

    public event EventHandler? StateChanged;

    public void Start(bool startInTray)
    {
        Agent.Start();
        Control.Start();
        _tray = new TrayIcon(this);
        _idleTimer.Start();

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
            _tray.Balloon("SSH Manager", "Работает в трее и заблокирован. Щёлкните, чтобы разблокировать.");
            return;
        }
        ShowMainWindow();
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
            _main?.Close();
            _main = null;
        }
        _tray?.UpdateState();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Connect(Guid serverId) => _ui.InvokeAsync(() => ConnectAsync(serverId));

    private async Task ConnectAsync(Guid serverId)
    {
        if (!await EnsureUnlockedAsync()) return;
        var server = Vault.Data.Servers.FirstOrDefault(s => s.Id == serverId);
        if (server == null) return;
        try
        {
            Launcher.Launch(server);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Не удалось открыть сессию", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool ConfirmHostKey(HostKeyInfo info) => _ui.Invoke(() =>
    {
        var host = KnownHostsService.HostPattern(info.Host, info.Port);
        var text = info.Status == HostKeyStatus.Mismatch
            ? $"ВНИМАНИЕ: ключ сервера {host} ИЗМЕНИЛСЯ!\n\nЭто может означать переустановку сервера или атаку «человек посередине».\n\nНовый ключ: {info.KeyType}\n{info.Fingerprint}\n\nЗаменить сохранённый ключ и продолжить?"
            : $"Первое подключение к {host}.\n\nКлюч сервера: {info.KeyType}\n{info.Fingerprint}\n\nДоверять этому серверу и сохранить его ключ?";
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var icon = info.Status == HostKeyStatus.Mismatch ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var result = owner != null
            ? MessageBox.Show(owner, text, "Проверка ключа сервера", MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(text, "Проверка ключа сервера", MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    });

    public void Exit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void TryApplyAutostart(bool enabled)
    {
        try
        {
            Autostart.Apply(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _tray?.Balloon("Автозапуск", "Не удалось изменить автозапуск: " + ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
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
                return spec == null ? ControlResponse.Fail("Сессия не найдена или устарела") : new ControlResponse { Ok = true, Spec = spec };
            }

            case "askpass":
            {
                var value = req.Token == null ? null : Launcher.AnswerAskPass(req.Token);
                return value == null ? ControlResponse.Fail("Нет пароля") : new ControlResponse { Ok = true, Value = value };
            }

            case "list":
                if (!await EnsureUnlockedAsync()) return ControlResponse.Fail("Хранилище заблокировано");
                return new ControlResponse
                {
                    Ok = true,
                    Names = Vault.Read(d => d.Servers
                        .OrderBy(s => s.Group).ThenBy(s => s.Name)
                        .Select(s => $"{s.Name,-24} {s.Display,-32} {(s.Group ?? "")}").ToList()),
                };

            case "connect":
            {
                if (!await EnsureUnlockedAsync()) return ControlResponse.Fail("Хранилище заблокировано");
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
                    return new ControlResponse { Ok = true, Spec = spec };
                });
            }

            default:
                return ControlResponse.Fail("Неизвестная команда " + req.Op);
        }
    }

    private Core.Models.ServerEntry? FindServer(string name, out string error)
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
            ? $"Сервер «{name}» не найден (sshm list — список)"
            : "Подходит несколько серверов: " + string.Join(", ", partial.Select(s => s.Name));
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
        Agent.Dispose();
        Control.Dispose();
        _tray?.Dispose();
        _tray = null;
    }
}
