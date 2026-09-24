using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Ssh;
using SshManager.Mvvm;
using SshManager.Services;

namespace SshManager.ViewModels;

public sealed record ServerChoice(Guid Id, string Name);

/// <summary>Settings tab; every change is saved immediately (scripts: on "Save").</summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly MainViewModel _main;
    private ScriptEntry? _selectedScript;
    private string _scriptName = "";
    private string _scriptOs = "";
    private string _scriptBody = "";
    private bool _scriptSudo = true;
    private bool _dirty;
    private bool _loadingScript;

    public SettingsViewModel(AppHost host, MainViewModel main)
    {
        _host = host;
        _main = main;
        BrowseBackupFolderCommand = new RelayCommand(BrowseBackupFolder);
        AddScriptCommand = new RelayCommand(AddScript);
        DuplicateScriptCommand = new RelayCommand(DuplicateScript, () => SelectedScript != null);
        DeleteScriptCommand = new RelayCommand(DeleteScript, () => SelectedScript != null);
        SaveScriptCommand = new RelayCommand(() => SaveScript(), () => SelectedScript != null && _dirty);
    }

    private AppSettings S => _host.SettingsStore.Settings;
    private BackupSettings B => S.Backup;

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _host.SettingsStore.Save();
        OnPropertyChanged(name);
    }

    public void RefreshTexts() => OnPropertyChanged(string.Empty);

    public void Dispose()
    {
        if (_dirty) SaveScript();
    }

    // ---------- interface ----------

    /// <summary>0 = Windows language, 1 = Russian, 2 = English.</summary>
    public int LanguageIndex
    {
        get => S.Language switch { L.Russian => 1, L.English => 2, _ => 0 };
        set => _host.SetLanguage(value switch { 1 => L.Russian, 2 => L.English, _ => null });
    }

    public bool Autostart
    {
        get => S.Autostart;
        set
        {
            S.Autostart = value;
            _host.TryApplyAutostart(value);
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

    public string EffectiveSshPath => L.Get("Settings.SshUsed") + " " + _host.Launcher.SshPath;
    public string TerminalInfo => SessionLauncher.FindWindowsTerminal() is { } wt
        ? L.Get("Settings.WtFound") + " " + wt
        : L.Get("Settings.WtMissing");
    public string DataDir => AppPaths.DataDir;
    public string HelperInfo => L.F("Settings.HelperInfo", AppPaths.HelperExe);

    // ---------- monitoring ----------

    public int MonitorIntervalMinutes
    {
        get => S.MonitorIntervalMinutes;
        set
        {
            S.MonitorIntervalMinutes = Math.Max(0, value);
            Save();
            _host.Health.Reschedule();
        }
    }

    public bool NotifyOnServerDown
    {
        get => S.NotifyOnServerDown;
        set
        {
            S.NotifyOnServerDown = value;
            Save();
        }
    }

    public bool CollectMetrics
    {
        get => S.CollectMetrics;
        set
        {
            S.CollectMetrics = value;
            Save();
        }
    }

    public bool GeoIpEnabled
    {
        get => S.GeoIpEnabled;
        set
        {
            S.GeoIpEnabled = value;
            Save();
            if (value) _host.RefreshBackground(force: false);
        }
    }

    public bool AutoInventory
    {
        get => S.AutoInventory;
        set
        {
            S.AutoInventory = value;
            Save();
        }
    }

    // ---------- backup ----------

    public bool BackupToFolder
    {
        get => B.Target == BackupTarget.Folder;
        set
        {
            if (!value) return;
            B.Target = BackupTarget.Folder;
            Save();
            OnPropertyChanged(nameof(BackupToSsh));
        }
    }

    public bool BackupToSsh
    {
        get => B.Target == BackupTarget.Ssh;
        set
        {
            if (!value) return;
            B.Target = BackupTarget.Ssh;
            Save();
            OnPropertyChanged(nameof(BackupToFolder));
        }
    }

    public string BackupFolder
    {
        get => B.Folder ?? "";
        set
        {
            B.Folder = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
        }
    }

    public ObservableCollection<ServerChoice> BackupServers { get; } = [];

    public Guid? BackupServerId
    {
        get => B.ServerId;
        set
        {
            B.ServerId = value;
            Save();
        }
    }

    public string BackupRemotePath
    {
        get => B.RemotePath;
        set
        {
            B.RemotePath = string.IsNullOrWhiteSpace(value) ? "sshmanager-backups" : value.Trim();
            Save();
        }
    }

    public int BackupKeep
    {
        get => B.Keep;
        set
        {
            B.Keep = Math.Max(0, value);
            Save();
        }
    }

    public int BackupAutoHours
    {
        get => B.AutoHours;
        set
        {
            B.AutoHours = Math.Max(0, value);
            Save();
        }
    }

    public string BackupLastResult => B.LastResult ?? L.Get("Backup.Never");

    public ICommand BrowseBackupFolderCommand { get; }
    public ICommand BackupNowCommand => _main.BackupNowCommand;

    public void RefreshBackup() => OnPropertyChanged(nameof(BackupLastResult));

    private void BrowseBackupFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.Get("Backup.ChooseFolder") };
        if (!string.IsNullOrWhiteSpace(B.Folder)) dlg.InitialDirectory = B.Folder;
        if (dlg.ShowDialog(_main.Owner) == true)
        {
            BackupFolder = dlg.FolderName;
            OnPropertyChanged(nameof(BackupFolder));
        }
    }

    // ---------- scripts ----------

    public ObservableCollection<ScriptEntry> Scripts { get; } = [];

    public ScriptEntry? SelectedScript
    {
        get => _selectedScript;
        set
        {
            if (_selectedScript == value) return;
            if (_dirty) SaveScript();
            _selectedScript = value;
            OnPropertyChanged();
            LoadEditor();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasScript => _selectedScript != null;

    public string ScriptName
    {
        get => _scriptName;
        set => Edit(ref _scriptName, value);
    }

    public string ScriptOsFilter
    {
        get => _scriptOs;
        set => Edit(ref _scriptOs, value);
    }

    public string ScriptBody
    {
        get => _scriptBody;
        set => Edit(ref _scriptBody, value);
    }

    public bool ScriptUseSudo
    {
        get => _scriptSudo;
        set => Edit(ref _scriptSudo, value);
    }

    public bool ScriptDirty => _dirty;

    public ICommand AddScriptCommand { get; }
    public ICommand DuplicateScriptCommand { get; }
    public ICommand DeleteScriptCommand { get; }
    public ICommand SaveScriptCommand { get; }

    private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name) || _loadingScript) return;
        _dirty = true;
        OnPropertyChanged(nameof(ScriptDirty));
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadEditor()
    {
        _loadingScript = true;
        ScriptName = _selectedScript?.Name ?? "";
        ScriptOsFilter = _selectedScript?.OsFilter ?? "";
        ScriptBody = _selectedScript?.Body ?? "";
        ScriptUseSudo = _selectedScript?.UseSudo ?? true;
        _loadingScript = false;
        _dirty = false;
        OnPropertyChanged(nameof(ScriptDirty));
        OnPropertyChanged(nameof(HasScript));
    }

    /// <summary>Called by the main view model after any vault change.</summary>
    public void OnVaultChanged()
    {
        var data = _host.Vault.Data;
        var backupId = B.ServerId;
        BackupServers.Clear();
        foreach (var s in data.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            BackupServers.Add(new ServerChoice(s.Id, $"{s.Name}  ({s.Display})"));
        // clearing the list makes the bound ComboBox write null back
        if (B.ServerId != backupId)
        {
            B.ServerId = backupId;
            _host.SettingsStore.Save();
        }
        OnPropertyChanged(nameof(BackupServerId));

        var selected = _selectedScript?.Id;
        var dirty = _dirty;
        _selectedScript = null;
        Scripts.Clear();
        foreach (var s in data.Scripts.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) Scripts.Add(s.Clone());
        _selectedScript = Scripts.FirstOrDefault(s => s.Id == selected) ?? (dirty ? null : Scripts.FirstOrDefault());
        OnPropertyChanged(nameof(SelectedScript));
        if (!dirty) LoadEditor();
        else OnPropertyChanged(nameof(HasScript));
    }

    private void AddScript()
    {
        if (_dirty) SaveScript();
        var s = new ScriptEntry { Name = L.Get("Scripts.NewName"), Body = "#!/bin/bash\nset -e\n\n" };
        _host.Vault.Update(d => d.Scripts.Add(s));
        OnVaultChanged();
        SelectedScript = Scripts.FirstOrDefault(x => x.Id == s.Id);
    }

    private void DuplicateScript()
    {
        if (_selectedScript == null) return;
        if (_dirty) SaveScript();
        var copy = _selectedScript.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += L.Get("Main.CopySuffix");
        _host.Vault.Update(d => d.Scripts.Add(copy));
        OnVaultChanged();
        SelectedScript = Scripts.FirstOrDefault(x => x.Id == copy.Id);
    }

    private void DeleteScript()
    {
        if (_selectedScript == null) return;
        if (!_main.Confirm(L.F("Scripts.DeleteConfirm", _selectedScript.Name))) return;
        var id = _selectedScript.Id;
        _dirty = false;
        _host.Vault.Update(d => d.Scripts.RemoveAll(x => x.Id == id));
        OnVaultChanged();
        SelectedScript = Scripts.FirstOrDefault();
    }

    private void SaveScript()
    {
        if (_selectedScript == null) return;
        var id = _selectedScript.Id;
        var name = string.IsNullOrWhiteSpace(_scriptName) ? L.Get("Scripts.NewName") : _scriptName.Trim();
        var os = string.Join(",", _scriptOs.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()));
        var body = _scriptBody;
        var sudo = _scriptSudo;
        _dirty = false;
        _host.Vault.Update(d =>
        {
            var s = d.Scripts.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.Name = name;
            s.OsFilter = os;
            s.Body = body;
            s.UseSudo = sudo;
        });
        OnPropertyChanged(nameof(ScriptDirty));
    }
}
