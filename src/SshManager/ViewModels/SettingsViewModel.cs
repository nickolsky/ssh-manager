using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Core.Scripts;
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
    private ScriptKind _scriptKind;
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
        AddOsCommand = new RelayCommand(AddOs);
        AddComposeCommand = new RelayCommand(AddCompose);
        RestoreBuiltinsCommand = new RelayCommand(RestoreBuiltins);
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

    public bool TerminalIntegration
    {
        get => S.TerminalIntegration;
        set
        {
            S.TerminalIntegration = value;
            Save();
        }
    }

    public bool TerminalAutoSuggest
    {
        get => S.TerminalAutoSuggest;
        set
        {
            S.TerminalAutoSuggest = value;
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
    public ICommand RestoreBackupCommand => _main.RestoreBackupCommand;

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
        set
        {
            Edit(ref _scriptOs, value);
            OnPropertyChanged(nameof(ScriptOsSummary));
        }
    }

    /// <summary>Choices of the OS drop-down: common systems plus what the servers run.</summary>
    public ObservableCollection<OsOption> OsOptions { get; } = [];

    public string ScriptOsSummary => OsOption.Summary(_scriptOs, OsOptions);

    public string NewOsId { get; set; } = "";

    public ICommand AddOsCommand { get; }

    /// <summary>Fills <see cref="OsOptions"/> and ticks the ones in the current filter.</summary>
    private void BuildOsOptions()
    {
        var selected = ScriptEntry.SplitOs(_scriptOs).Select(x => x.ToLowerInvariant()).ToHashSet();
        var facts = _host.Vault.IsUnlocked ? _host.Vault.Data.Servers.Select(s => s.Facts).OfType<ServerFacts>() : [];
        foreach (var o in OsOptions) o.PropertyChanged -= OnOsToggled;
        OsOptions.Clear();
        foreach (var o in OsOption.Build(facts, selected))
        {
            o.PropertyChanged += OnOsToggled;
            OsOptions.Add(o);
        }
        OnPropertyChanged(nameof(ScriptOsSummary));
    }

    private void OnOsToggled(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OsOption.IsChecked)) return;
        ScriptOsFilter = string.Join(",", OsOptions.Where(o => o.IsChecked).Select(o => o.Id));
    }

    private void AddOs()
    {
        var id = NewOsId.Trim().ToLowerInvariant();
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))) return;
        if (OsOptions.FirstOrDefault(o => o.Id == id) is { } existing) existing.IsChecked = true;
        else
        {
            ScriptOsFilter = string.Join(",", ScriptEntry.SplitOs(_scriptOs).Append(id).Distinct());
            BuildOsOptions();
        }
        NewOsId = "";
        OnPropertyChanged(nameof(NewOsId));
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
    public ICommand RestoreBuiltinsCommand { get; }
    public ICommand AddComposeCommand { get; }

    /// <summary>0 = bash, 1 = docker compose.</summary>
    public int ScriptKindIndex
    {
        get => (int)_scriptKind;
        set
        {
            Edit(ref _scriptKind, (ScriptKind)value);
            OnPropertyChanged(nameof(IsCompose));
        }
    }

    public bool IsCompose => _scriptKind == ScriptKind.Compose;

    private const string ComposeTemplate = """
        # @name Новый сервис
        # @project my-service
        # @description Файл кладётся в /opt/my-service, параметры — в .env рядом, затем docker compose up -d.
        # @param APP_PORT number required default=8080 label="Порт"
        # @result APP_URL label="Адрес" value="http://${SSHM_HOST}:${APP_PORT}"
        # @result APP_PORT label="Порт" value="${APP_PORT}" monitor="My service"
        services:
          app:
            image: nginx:alpine
            restart: unless-stopped
            ports:
              - "${APP_PORT}:80"

        """;

    private void AddCompose()
    {
        if (_dirty) SaveScript();
        var s = new ScriptEntry { Name = L.Get("Scripts.NewCompose"), Kind = ScriptKind.Compose, Body = ComposeTemplate };
        _host.Vault.Update(d => d.Scripts.Add(s));
        OnVaultChanged();
        SelectedScript = Scripts.FirstOrDefault(x => x.Id == s.Id);
    }

    /// <summary>Brings back deleted built-in scripts and resets edited ones to the shipped text.</summary>
    private void RestoreBuiltins()
    {
        if (!_main.Confirm(L.Get("Scripts.RestoreBuiltinsConfirm"))) return;
        if (_dirty) SaveScript();
        _host.Vault.Update(d =>
        {
            d.RemovedBuiltins.Clear();
            foreach (var b in BuiltinScripts.All)
            {
                if (d.Scripts.FirstOrDefault(s => s.BuiltinId == b.Id) is not { } s) continue;
                s.Body = b.Body;
                s.OsFilter = b.Manifest.Os ?? s.OsFilter;
                s.BuiltinHash = BuiltinScripts.Hash(b.Body);
            }
            BuiltinScripts.Sync(d); // adds the deleted ones
        });
        OnVaultChanged();
    }

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
        ScriptKindIndex = (int)(_selectedScript?.Kind ?? ScriptKind.Bash);
        BuildOsOptions();
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
        var builtin = _selectedScript.BuiltinId;
        _dirty = false;
        _host.Vault.Update(d =>
        {
            d.Scripts.RemoveAll(x => x.Id == id);
            if (builtin != null && !d.RemovedBuiltins.Contains(builtin)) d.RemovedBuiltins.Add(builtin);
        });
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
        var kind = _scriptKind;
        _dirty = false;
        _host.Vault.Update(d =>
        {
            var s = d.Scripts.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.Name = name;
            s.OsFilter = os;
            s.Body = body;
            s.UseSudo = sudo;
            s.Kind = kind;
        });
        OnPropertyChanged(nameof(ScriptDirty));
    }
}

/// <summary>One entry of the script OS drop-down: an os-release ID, optionally with a version ("debian:12").</summary>
public sealed class OsOption(string id, string label, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    private static readonly (string Id, string Name)[] Common =
    [
        ("debian", "Debian"), ("debian:12", "Debian 12"), ("debian:13", "Debian 13"),
        ("ubuntu", "Ubuntu"), ("ubuntu:22.04", "Ubuntu 22.04"), ("ubuntu:24.04", "Ubuntu 24.04"),
        ("centos", "CentOS"), ("rhel", "RHEL"), ("almalinux", "AlmaLinux"), ("rocky", "Rocky Linux"),
        ("fedora", "Fedora"), ("alpine", "Alpine"), ("arch", "Arch Linux"), ("opensuse-leap", "openSUSE Leap"),
        ("amzn", "Amazon Linux"),
    ];

    public string Id { get; } = id;
    public string Label { get; } = label;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public static List<OsOption> Build(IEnumerable<ServerFacts> servers, IReadOnlySet<string> selected)
    {
        var names = new Dictionary<string, string>();
        foreach (var (id, name) in Common) names[id] = name;
        foreach (var f in servers)
        {
            if (f.OsId is not { Length: > 0 } id) continue;
            var name = f.OsName ?? id;
            names.TryAdd(id, name);
            if (f.OsVersion is { Length: > 0 } v) names.TryAdd($"{id}:{v}", $"{name} {v}");
        }
        foreach (var id in selected) names.TryAdd(id, id);
        return names
            .OrderBy(n => n.Key.Split(':')[0], StringComparer.Ordinal).ThenBy(n => n.Key.Contains(':') ? 1 : 0).ThenBy(n => n.Key, StringComparer.Ordinal)
            .Select(n => new OsOption(n.Key, Describe(n.Key, n.Value), selected.Contains(n.Key)))
            .ToList();
    }

    /// <summary>"Debian (any version, and Ubuntu…)" for bare IDs, which also match derived systems.</summary>
    private static string Describe(string id, string name) => id.Contains(':')
        ? name
        : id switch
        {
            "debian" => L.F("Os.AnyDerived", name, "Ubuntu…"),
            "rhel" => L.F("Os.AnyDerived", name, "AlmaLinux, Rocky…"),
            _ => L.F("Os.Any", name),
        };

    public static string Summary(string filter, IEnumerable<OsOption> options)
    {
        var ids = ScriptEntry.SplitOs(filter);
        if (ids.Length == 0) return L.Get("Os.AllSystems");
        var byId = options.ToDictionary(o => o.Id, o => o.Label);
        return string.Join(", ", ids.Select(i => i.Contains(':') && byId.TryGetValue(i, out var l) ? l
            : Common.FirstOrDefault(c => c.Id == i).Name ?? i));
    }
}
