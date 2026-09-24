using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Ssh;
using SshManager.Mvvm;
using SshManager.Services;

namespace SshManager.ViewModels;

/// <summary>Settings tab; every change is saved immediately.</summary>
public sealed class SettingsViewModel(AppHost host) : ObservableObject
{
    private AppSettings S => host.SettingsStore.Settings;

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        host.SettingsStore.Save();
        OnPropertyChanged(name);
    }

    public bool Autostart
    {
        get => S.Autostart;
        set
        {
            S.Autostart = value;
            host.TryApplyAutostart(value);
            Save();
        }
    }

    public int AutoLockMinutes
    {
        get => S.AutoLockMinutes;
        set
        {
            S.AutoLockMinutes = Math.Max(0, value);
            Save();
        }
    }

    public bool LockOnWindowsLock
    {
        get => S.LockOnWindowsLock;
        set
        {
            S.LockOnWindowsLock = value;
            Save();
        }
    }

    public bool UnlockOnAgentRequest
    {
        get => S.UnlockOnAgentRequest;
        set
        {
            S.UnlockOnAgentRequest = value;
            Save();
        }
    }

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set
        {
            S.CloseToTray = value;
            Save();
        }
    }

    public int TerminalIndex
    {
        get => (int)S.Terminal;
        set
        {
            S.Terminal = (TerminalMode)value;
            Save();
        }
    }

    public string WindowsTerminalProfile
    {
        get => S.WindowsTerminalProfile ?? "";
        set
        {
            S.WindowsTerminalProfile = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    public string SshPath
    {
        get => S.SshPath ?? "";
        set
        {
            S.SshPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
            OnPropertyChanged(nameof(EffectiveSshPath));
        }
    }

    public int ServerAliveInterval
    {
        get => S.ServerAliveInterval;
        set
        {
            S.ServerAliveInterval = Math.Max(0, value);
            Save();
        }
    }

    public string EffectiveSshPath => "Используется: " + host.Launcher.SshPath;
    public string TerminalInfo => SessionLauncher.FindWindowsTerminal() is { } wt
        ? "Windows Terminal найден: " + wt
        : "Windows Terminal не найден — сессии будут открываться в обычном окне консоли";
    public string DataDir => AppPaths.DataDir;
    public string HelperInfo => $"Из любого терминала: \"{AppPaths.HelperExe}\" <имя сервера>  (добавьте папку в PATH, чтобы писать просто sshm <имя>)";
}
