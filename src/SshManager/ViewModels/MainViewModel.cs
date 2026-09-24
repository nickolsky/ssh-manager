using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SshManager.Core;
using SshManager.Core.Backup;
using SshManager.Core.Crypto;
using SshManager.Core.Inventory;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Core.Scripts;
using SshManager.Core.Ssh;
using SshManager.Core.Storage;
using SshManager.Mvvm;
using SshManager.Services;
using SshManager.Views;

namespace SshManager.ViewModels;

public sealed class KeyRow(KeyEntry entry, string usedBy)
{
    public KeyEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Type => Entry.Type switch { "ssh-ed25519" => "Ed25519", "ssh-rsa" => "RSA", var t => t };
    public string Fingerprint => Entry.Fingerprint;
    public string Comment => Entry.Comment;
    public string UsedBy { get; } = usedBy;
    public string Created => Entry.Created.ToString("d", L.Culture);
}

/// <summary>Entry of a dynamically built context submenu ("Install ▸").</summary>
public sealed record MenuEntry(string Header, ICommand? Command = null, object? Parameter = null, bool Bold = false,
    IReadOnlyList<MenuEntry>? Children = null)
{
    public bool Enabled => Command != null || Children is { Count: > 0 };
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly HashSet<Guid> _expandedServers = [];
    private readonly Dictionary<Guid, ServerNode> _serverNodes = [];
    private readonly List<GroupNode> _groupNodes = [];
    private Dictionary<Guid, string> _keyNames = [];
    private Dictionary<string, ServerEntry> _serversByIp = new(StringComparer.OrdinalIgnoreCase);
    private TreeNode? _selectedNode;
    private KeyRow? _selectedKey;
    private string _search = "";
    private string _status = "";
    private bool _busy;
    private bool _building;
    private int _selectedTab;

    public MainViewModel(AppHost host)
    {
        _host = host;

        bool HasServer() => SelectedServer != null && !Busy;
        bool HasKey() => SelectedKey != null;

        ConnectCommand = new RelayCommand(Connect, HasServer);
        AddServerCommand = new RelayCommand(AddServer);
        EditServerCommand = new RelayCommand(EditServer, HasServer);
        DuplicateServerCommand = new RelayCommand(DuplicateServer, HasServer);
        DeleteServerCommand = new RelayCommand(DeleteServer, HasServer);
        CopyCommandCommand = new RelayCommand(CopyCommand, HasServer);
        SetupKeyCommand = new RelayCommand(SetupKey, () => HasServer() && !string.IsNullOrEmpty(SelectedServer!.Entry.Password));
        TestCommand = new RelayCommand(Test, HasServer);
        RefreshInfoCommand = new RelayCommand(() => _host.RefreshServer(SelectedServer!.Entry.Id), HasServer);
        CheckNowCommand = new RelayCommand(() => _host.Health.CheckNow(SelectedServer!.Entry.Id), HasServer);
        PortForwardsCommand = new RelayCommand(OpenPortForwards, HasServer);
        PortMonitorCommand = new RelayCommand(OpenPortMonitor, HasServer);
        TogglePortCommand = new RelayCommand(TogglePort, () => SelectedNode is PortNode { CanMonitor: true });
        RunScriptCommand = new RelayCommand(p => RunScript(p as ScriptEntry), _ => HasServer());
        UptimeHistoryCommand = new RelayCommand(OpenUptimeHistory, () => SelectedServer != null);
        InstallDockerCommand = new RelayCommand(InstallDocker, () => HasServer() && SelectedServer!.Entry.Facts?.DockerAvailable != true);
        CopyAttributeCommand = new RelayCommand(CopyAttribute, () => SelectedNode is AttributeNode);
        DeleteAttributeCommand = new RelayCommand(DeleteAttribute, () => SelectedNode is AttributeNode);
        ContainerLogsCommand = new RelayCommand(() => QuickAction(n => n is ContainerNode c ? ScriptRunner.DockerLogs(n.Server!.Entry, c.Info.Name) : null, "docker logs"), () => SelectedNode is ContainerNode);
        ContainerRestartCommand = new RelayCommand(() => ContainerAction(ContainerCommands.Restart, "Container.Restarting", "Container.Restarted"),
            () => SelectedNode is ContainerNode && !Busy);
        ContainerStartCommand = new RelayCommand(() => ContainerAction(ContainerCommands.Start, "Container.Starting", "Container.Started"),
            () => SelectedNode is ContainerNode { Info.IsRunning: false } && !Busy);
        ContainerStopCommand = new RelayCommand(StopContainer, () => SelectedNode is ContainerNode { Info.IsRunning: true } && !Busy);
        ContainerRemoveCommand = new RelayCommand(RemoveContainer, () => SelectedNode is ContainerNode && !Busy);
        ContainerAutostartCommand = new RelayCommand(p => SetAutostart(p as string), _ => SelectedNode is ContainerNode && !Busy);
        ServiceStatusCommand = new RelayCommand(() => QuickAction(n => n is ServiceNode s ? ScriptRunner.ServiceStatus(n.Server!.Entry, s.Info.Unit) : null, "systemctl status"), () => SelectedNode is ServiceNode);
        ServiceLogsCommand = new RelayCommand(() => QuickAction(n => n is ServiceNode s ? ScriptRunner.ServiceLogs(n.Server!.Entry, s.Info.Unit) : null, "journalctl"), () => SelectedNode is ServiceNode);
        ServiceRestartCommand = new RelayCommand(RestartService, () => SelectedNode is ServiceNode && !Busy);
        ServiceStartCommand = new RelayCommand(() => ServiceAction(ServiceCommands.Start, "Service.Starting", "Service.Started"),
            () => SelectedNode is ServiceNode { Info.IsRunning: false } && !Busy);
        ServiceStopCommand = new RelayCommand(StopService, () => SelectedNode is ServiceNode { Info.IsRunning: true } && !Busy);
        ServiceEnableCommand = new RelayCommand(() => ServiceAction(ServiceCommands.Enable, "Service.Enabling", "Service.EnabledDone"),
            () => SelectedNode is ServiceNode { Info.Autostart: false } && !Busy);
        ServiceDisableCommand = new RelayCommand(DisableService, () => SelectedNode is ServiceNode { Info.Autostart: true } && !Busy);

        ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false));
        RefreshAllCommand = new RelayCommand(() =>
        {
            _host.RefreshBackground(force: true);
            Status = L.Get("Main.RefreshingAll");
        });
        BackupNowCommand = new RelayCommand(BackupNow);
        RestoreBackupCommand = new RelayCommand(RestoreBackup);
        SetLanguageCommand = new RelayCommand(p => _host.SetLanguage(p as string is "ru" or "en" ? (string)p : null));
        AboutCommand = new RelayCommand(About);
        ExitCommand = new RelayCommand(() => _host.Exit());
        OpenScriptsSettingsCommand = new RelayCommand(() => SelectedTab = 2);

        GenerateKeyCommand = new RelayCommand(GenerateKey);
        ImportKeyCommand = new RelayCommand(ImportKey);
        CopyPublicKeyCommand = new RelayCommand(CopyPublicKey, HasKey);
        ExportPrivateKeyCommand = new RelayCommand(ExportPrivateKey, HasKey);
        RenameKeyCommand = new RelayCommand(RenameKey, HasKey);
        DeleteKeyCommand = new RelayCommand(DeleteKey, HasKey);

        LockCommand = new RelayCommand(() => _host.Lock());
        ChangePasswordCommand = new RelayCommand(() => new ChangePasswordWindow(_host.Vault) { Owner = Owner }.ShowDialog());
        OpenDataFolderCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true }));

        Settings = new SettingsViewModel(host, this);

        _host.Vault.DataChanged += OnDataChanged;
        _host.Vault.FactsChanged += OnFactsChanged;
        _host.Inventory.RunningChanged += OnInventoryRunning;
        _host.Health.HealthChanged += OnHealthChanged;
        _host.Metrics.MetricsChanged += OnMetricsChanged;
        L.LanguageChanged += OnLanguageChanged;
        Reload();
    }

    public Window? Owner { get; set; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<TreeNode> Roots { get; } = [];
    public ObservableCollection<KeyRow> Keys { get; } = [];
    public ObservableCollection<MenuEntry> InstallItems { get; } = [];

    public TreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            OnPropertyChanged(nameof(SelectedServer));
            OnPropertyChanged(nameof(SelectionKind));
            OnPropertyChanged(nameof(IsRestartNo));
            OnPropertyChanged(nameof(IsRestartUnlessStopped));
            OnPropertyChanged(nameof(IsRestartAlways));
            OnPropertyChanged(nameof(IsRestartOnFailure));
            OnPropertyChanged(nameof(IsServiceAutostart));
            OnPropertyChanged(nameof(IsServiceNoAutostart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ServerNode? SelectedServer => _selectedNode?.Server;

    /// <summary>group, server, container, service, forward, other — drives context menu visibility.</summary>
    public string SelectionKind => _selectedNode switch
    {
        ServerNode => "server",
        ContainerNode => "container",
        ServiceNode => "service",
        ForwardNode => "forward",
        PortNode { Monitored: not null } => "port-on",
        AttributeNode => "attribute",
        PortNode => "port-off",
        GroupNode => "group",
        null => "",
        _ => "other",
    };

    public KeyRow? SelectedKey
    {
        get => _selectedKey;
        set => Set(ref _selectedKey, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) BuildTree();
        }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            Set(ref _busy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string AgentStatus => _host.Agent.PipeName switch
    {
        null => L.Get("Agent.NotRunning"),
        var p when _host.Agent.UsesDefaultPipe => L.F("Agent.Default", p),
        var p => L.F("Agent.Fallback", p),
    };

    public string Summary
    {
        get
        {
            var total = _serverNodes.Count;
            var online = _serverNodes.Values.Count(n => n.Health.State == HealthState.Online);
            var offline = _serverNodes.Values.Count(n => n.Health.State == HealthState.Offline);
            return L.F("Main.Summary", _host.Vault.IsUnlocked ? _host.Vault.Data.Servers.Count : total, online, offline, Keys.Count);
        }
    }

    public string? LanguageSetting => _host.SettingsStore.Settings.Language;
    public bool IsLangSystem => LanguageSetting == null;
    public bool IsLangRu => LanguageSetting == L.Russian;
    public bool IsLangEn => LanguageSetting == L.English;

    public ICommand ConnectCommand { get; }
    public ICommand AddServerCommand { get; }
    public ICommand EditServerCommand { get; }
    public ICommand DuplicateServerCommand { get; }
    public ICommand DeleteServerCommand { get; }
    public ICommand CopyCommandCommand { get; }
    public ICommand SetupKeyCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RefreshInfoCommand { get; }
    public ICommand CheckNowCommand { get; }
    public ICommand PortForwardsCommand { get; }
    public ICommand PortMonitorCommand { get; }
    public ICommand TogglePortCommand { get; }
    public ICommand RunScriptCommand { get; }
    public ICommand UptimeHistoryCommand { get; }
    public ICommand InstallDockerCommand { get; }
    public ICommand CopyAttributeCommand { get; }
    public ICommand DeleteAttributeCommand { get; }
    public ICommand ContainerLogsCommand { get; }
    public ICommand ContainerRestartCommand { get; }
    public ICommand ContainerStartCommand { get; }
    public ICommand ContainerStopCommand { get; }
    public ICommand ContainerRemoveCommand { get; }
    public ICommand ContainerAutostartCommand { get; }

    /// <summary>Restart policy of the selected container, for the check marks in "Autostart ▸".</summary>
    public string? SelectedRestartPolicy => (SelectedNode as ContainerNode)?.Info.RestartPolicy;
    public bool IsRestartNo => SelectedRestartPolicy == "no";
    public bool IsRestartUnlessStopped => SelectedRestartPolicy == "unless-stopped";
    public bool IsRestartAlways => SelectedRestartPolicy == "always";
    public bool IsRestartOnFailure => SelectedRestartPolicy == "on-failure";
    public ICommand ServiceStatusCommand { get; }
    public ICommand ServiceLogsCommand { get; }
    public ICommand ServiceRestartCommand { get; }
    public ICommand ServiceStartCommand { get; }
    public ICommand ServiceStopCommand { get; }
    public ICommand ServiceEnableCommand { get; }
    public ICommand ServiceDisableCommand { get; }
    public bool IsServiceAutostart => (SelectedNode as ServiceNode)?.Info.Autostart == true;
    public bool IsServiceNoAutostart => SelectedNode is ServiceNode { Info.Autostart: false };
    public ICommand ExpandAllCommand { get; }
    public ICommand CollapseAllCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand SetLanguageCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OpenScriptsSettingsCommand { get; }
    public ICommand GenerateKeyCommand { get; }
    public ICommand ImportKeyCommand { get; }
    public ICommand CopyPublicKeyCommand { get; }
    public ICommand ExportPrivateKeyCommand { get; }
    public ICommand RenameKeyCommand { get; }
    public ICommand DeleteKeyCommand { get; }
    public ICommand LockCommand { get; }
    public ICommand ChangePasswordCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    // ---------- events from services (any thread) ----------

    private void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private void OnDataChanged(object? s, EventArgs e) => Ui(Reload);

    private void OnFactsChanged(object? s, Guid id) => Ui(() =>
    {
        if (!_host.Vault.IsUnlocked) return;
        var entry = _host.Vault.Data.Servers.FirstOrDefault(x => x.Id == id);
        if (entry == null) return;
        RebuildIpMap();
        if (_serverNodes.TryGetValue(id, out var node)) node.Update(entry);
        foreach (var n in _serverNodes.Values) n.RaiseForwardsChanged();
    });

    private void OnInventoryRunning(object? s, Guid id) => Ui(() =>
    {
        if (_serverNodes.TryGetValue(id, out var node)) node.SetLoading(_host.Inventory.IsRunning(id));
    });

    private void OnHealthChanged(object? s, Guid id) => Ui(() =>
    {
        if (!_serverNodes.TryGetValue(id, out var node)) return;
        node.SetHealth(_host.Health.Get(id), _host.Health.GetPorts(id));
        node.SetUptime(_host.Uptime.Stats(id, DateTime.UtcNow));
        UpdateGroupCounts();
        OnPropertyChanged(nameof(Summary));
    });

    private void OnMetricsChanged(object? s, Guid id) => Ui(() =>
    {
        if (_serverNodes.TryGetValue(id, out var node)) node.SetMetrics(_host.Metrics.Get(id));
    });

    private void OnLanguageChanged(object? s, EventArgs e) => Ui(() =>
    {
        Reload();
        OnPropertyChanged(nameof(AgentStatus));
        OnPropertyChanged(nameof(LanguageSetting));
        OnPropertyChanged(nameof(IsLangSystem));
        OnPropertyChanged(nameof(IsLangRu));
        OnPropertyChanged(nameof(IsLangEn));
        Status = "";
        Settings.RefreshTexts();
    });

    public void Dispose()
    {
        _host.Vault.DataChanged -= OnDataChanged;
        _host.Vault.FactsChanged -= OnFactsChanged;
        _host.Inventory.RunningChanged -= OnInventoryRunning;
        _host.Health.HealthChanged -= OnHealthChanged;
        _host.Metrics.MetricsChanged -= OnMetricsChanged;
        L.LanguageChanged -= OnLanguageChanged;
        Settings.Dispose();
    }

    // ---------- tree ----------

    public void Reload()
    {
        if (!_host.Vault.IsUnlocked) return;
        var data = _host.Vault.Data;
        var selectedKey = SelectedKey?.Entry.Id;
        _keyNames = data.Keys.ToDictionary(k => k.Id, k => k.Name);
        RebuildIpMap();

        Keys.Clear();
        foreach (var k in data.Keys.OrderBy(k => k.Name))
        {
            var used = data.Servers.Where(s => s.KeyId == k.Id && s.Auth == AuthMode.Key).Select(s => s.Name).ToList();
            Keys.Add(new KeyRow(k, used.Count == 0 ? "—" : string.Join(", ", used)));
        }
        SelectedKey = Keys.FirstOrDefault(r => r.Entry.Id == selectedKey);

        BuildTree();
        Settings.OnVaultChanged();
        OnPropertyChanged(nameof(AgentStatus));
    }

    private void RebuildIpMap()
    {
        var map = new Dictionary<string, ServerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _host.Vault.Data.Servers)
        {
            if (IPAddress.TryParse(s.Host, out _)) map.TryAdd(s.Host, s);
            if (s.Facts?.Geo?.Ip is { Length: > 0 } ip) map.TryAdd(ip, s);
        }
        _serversByIp = map;
    }

    public static string[] SplitGroup(string? group) =>
        (group ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void BuildTree()
    {
        if (!_host.Vault.IsUnlocked) return;
        var selectedId = SelectedServer?.Entry.Id;
        var selectedWasServer = SelectedNode is ServerNode;
        var searching = !string.IsNullOrWhiteSpace(_search);
        var collapsed = _host.SettingsStore.Settings.CollapsedGroups.ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        _building = true;
        try
        {
            Roots.Clear();
            _serverNodes.Clear();
            _groupNodes.Clear();
            var groups = new Dictionary<string, GroupNode>(StringComparer.CurrentCultureIgnoreCase);

            GroupNode? Group(string[] parts)
            {
                GroupNode? parent = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    var path = string.Join("/", parts.Take(i + 1));
                    if (!groups.TryGetValue(path, out var g))
                    {
                        g = new GroupNode(i, path, parts[i], OnGroupExpanded) { Parent = parent, Silent = true };
                        g.IsExpanded = searching || !collapsed.Contains(path);
                        g.Silent = false;
                        groups[path] = g;
                        _groupNodes.Add(g);
                        (parent?.Children ?? Roots).Add(g);
                    }
                    parent = g;
                }
                return parent;
            }

            foreach (var s in _host.Vault.Data.Servers.Where(Matches))
            {
                var parts = SplitGroup(s.Group);
                var parent = Group(parts);
                var node = new ServerNode(this, parts.Length, s, parent);
                node.SetHealth(_host.Health.Get(s.Id), _host.Health.GetPorts(s.Id));
                node.SetMetrics(_host.Metrics.Get(s.Id));
                node.SetUptime(_host.Uptime.Stats(s.Id, DateTime.UtcNow));
                node.SetLoading(_host.Inventory.IsRunning(s.Id));
                node.IsExpanded = _expandedServers.Contains(s.Id);
                _serverNodes[s.Id] = node;
                (parent?.Children ?? Roots).Add(node);
            }

            Sort(Roots);
            UpdateGroupCounts();
        }
        finally
        {
            _building = false;
        }

        if (selectedId is { } id && _serverNodes.TryGetValue(id, out var sel))
        {
            if (selectedWasServer || SelectedNode == null) sel.IsSelected = true;
        }
        else if (SelectedNode != null && !Contains(SelectedNode))
        {
            SelectedNode = null;
        }
        OnPropertyChanged(nameof(Summary));
    }

    private bool Contains(TreeNode node) => node.Server is { } s && _serverNodes.ContainsValue(s);

    private static void Sort(ObservableCollection<TreeNode> items)
    {
        var ordered = items.OrderBy(n => n is GroupNode ? 0 : 1)
            .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        items.Clear();
        foreach (var n in ordered)
        {
            items.Add(n);
            if (n is GroupNode) Sort(n.Children);
        }
    }

    private void UpdateGroupCounts()
    {
        foreach (var g in _groupNodes)
        {
            var servers = Descendants(g).OfType<ServerNode>().ToList();
            g.ServerCount = servers.Count;
            g.OfflineCount = servers.Count(s => s.Health.State == HealthState.Offline);
            g.WarnCount = servers.Count(s => s.Health.State == HealthState.Online && s.DownPorts.Count > 0);
            g.Refresh();
        }
    }

    private static IEnumerable<TreeNode> Descendants(TreeNode n)
    {
        foreach (var c in n.Children)
        {
            yield return c;
            if (c is GroupNode)
                foreach (var d in Descendants(c)) yield return d;
        }
    }

    private bool Matches(ServerEntry s)
    {
        if (string.IsNullOrWhiteSpace(_search)) return true;
        var q = _search.Trim();
        return s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               s.Host.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               s.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (s.Group ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               (s.Notes ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               (s.Facts?.OsLabel ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               (s.Facts?.Geo?.Label ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
               (s.Facts?.Containers.Any(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private void OnGroupExpanded(GroupNode g)
    {
        if (_building || !string.IsNullOrWhiteSpace(_search)) return;
        var list = _host.SettingsStore.Settings.CollapsedGroups;
        list.RemoveAll(p => string.Equals(p, g.Path, StringComparison.CurrentCultureIgnoreCase));
        if (!g.IsExpanded) list.Add(g.Path);
        _host.SettingsStore.Save();
    }

    public void OnServerExpanded(ServerNode node)
    {
        if (node.IsExpanded) _expandedServers.Add(node.Entry.Id);
        else _expandedServers.Remove(node.Entry.Id);
        if (_building || !node.IsExpanded) return;
        if (_host.Inventory.NeedsRefresh(node.Entry)) _ = _host.Inventory.RefreshAsync(node.Entry.Id, interactive: true);
    }

    private void SetAllExpanded(bool expanded)
    {
        foreach (var g in _groupNodes) g.IsExpanded = expanded;
        if (!expanded)
            foreach (var s in _serverNodes.Values) s.IsExpanded = false;
    }

    // ---------- helpers for nodes ----------

    public string KeyName(Guid? id) => id is { } k && _keyNames.TryGetValue(k, out var n) ? n : L.Get("Auth.KeyMissing");

    public string TargetName(string ip) => _serversByIp.TryGetValue(ip, out var s) ? s.Name : ip;

    public string DescribeForward(PortForward p) =>
        $"{p.Protocol} {p.ListenPort} → {TargetName(p.TargetIp)}:{p.EffectiveTargetPort}";

    public IEnumerable<(ServerEntry From, PortForward Forward)> IncomingForwards(ServerEntry target)
    {
        if (!_host.Vault.IsUnlocked) yield break;
        foreach (var s in _host.Vault.Data.Servers)
        {
            if (s.Id == target.Id || s.Facts == null) continue;
            foreach (var f in s.Facts.Forwards)
                if (_serversByIp.TryGetValue(f.TargetIp, out var t) && t.Id == target.Id)
                    yield return (s, f);
        }
    }

    public IEnumerable<string> ForwardSummary(ServerEntry s)
    {
        foreach (var f in s.Facts?.Forwards ?? []) yield return DescribeForward(f);
        foreach (var (from, f) in IncomingForwards(s)) yield return $"← {from.Name} ({f.Protocol} {f.ListenPort})";
    }

    /// <summary>Fills "Install ▸" for the selected server: scripts for its OS first, the rest under "Other".</summary>
    public void PrepareContextMenu()
    {
        InstallItems.Clear();
        var server = SelectedServer?.Entry;
        var scripts = _host.Vault.IsUnlocked ? _host.Vault.Data.Scripts.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList() : [];
        if (scripts.Count == 0)
        {
            InstallItems.Add(new MenuEntry(L.Get("Scripts.NoneMenu"), OpenScriptsSettingsCommand));
            return;
        }
        var matching = scripts.Where(s => s.Matches(server?.Facts)).ToList();
        var other = scripts.Except(matching).ToList();
        foreach (var s in matching)
            InstallItems.Add(new MenuEntry(MenuName(s), RunScriptCommand, s, Bold: !string.IsNullOrWhiteSpace(s.OsFilter)));
        if (other.Count > 0)
        {
            var children = other.Select(s => new MenuEntry($"{MenuName(s)}  ({s.OsFilter})", RunScriptCommand, s)).ToList();
            if (matching.Count == 0) foreach (var c in children) InstallItems.Add(c);
            else InstallItems.Add(new MenuEntry(L.Get("Scripts.OtherOs"), Children: children));
        }
    }

    // ---------- servers ----------

    public void Connect()
    {
        if (SelectedServer == null) return;
        var s = SelectedServer.Entry;
        try
        {
            _host.Launch(s);
            Status = L.F("Main.SessionOpened", s.Name);
        }
        catch (Exception ex)
        {
            Warn(L.Get("Main.SessionFailed"), ex.Message);
        }
    }

    private void AddServer()
    {
        var group = SelectedNode is GroupNode g ? g.Path : SelectedServer?.Entry.Group ?? "";
        var entry = new ServerEntry { Group = group };
        if (new ServerEditorWindow(_host, entry, isNew: true) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d => d.Servers.Add(entry));
        Select(entry.Id);
        _ = _host.Geo.RefreshAsync(entry.Id);
        _host.Health.CheckNow(entry.Id);
    }

    private void Select(Guid id)
    {
        if (!_serverNodes.TryGetValue(id, out var node)) return;
        for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
        node.IsSelected = true;
    }

    private void EditServer()
    {
        if (SelectedServer == null) return;
        var copy = SelectedServer.Entry.Clone();
        if (new ServerEditorWindow(_host, copy, isNew: false) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d =>
        {
            var i = d.Servers.FindIndex(s => s.Id == copy.Id);
            if (i < 0) return;
            var hostChanged = d.Servers[i].Host != copy.Host;
            copy.Facts = d.Servers[i].Facts; // collected in the meantime
            copy.Attributes = d.Servers[i].Attributes; // a script may have finished while the editor was open
            copy.ScriptRuns = d.Servers[i].ScriptRuns;
            if (hostChanged && copy.Facts != null) copy.Facts.Geo = null;
            d.Servers[i] = copy;
        });
        _ = _host.Geo.RefreshAsync(copy.Id);
        _host.Health.CheckNow(copy.Id);
    }

    private void DuplicateServer()
    {
        if (SelectedServer == null) return;
        var copy = SelectedServer.Entry.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += L.Get("Main.CopySuffix");
        copy.LastConnected = null;
        copy.Facts = null;
        copy.Attributes = []; // results belong to the original machine
        copy.ScriptRuns = [];
        if (new ServerEditorWindow(_host, copy, isNew: true) { Owner = Owner }.ShowDialog() != true) return;
        _host.Vault.Update(d => d.Servers.Add(copy));
        Select(copy.Id);
    }

    private void DeleteServer()
    {
        if (SelectedServer == null) return;
        var s = SelectedServer.Entry;
        if (!Confirm(L.F("Main.DeleteServerConfirm", s.Name, s.Display))) return;
        _expandedServers.Remove(s.Id);
        _host.Vault.Update(d => d.Servers.RemoveAll(x => x.Id == s.Id));
        _host.Uptime.Delete(s.Id);
    }

    public bool CanDelete => DeleteServerCommand.CanExecute(null) && SelectedNode is ServerNode;

    private void CopyCommand()
    {
        if (SelectedServer == null) return;
        Clipboard.SetText(_host.Launcher.CommandLine(SelectedServer.Entry));
        Status = L.Get("Main.CommandCopied");
    }

    private void SetupKey()
    {
        if (SelectedServer == null) return;
        new KeySetupWindow(_host, SelectedServer.Entry.Clone()) { Owner = Owner }.ShowDialog();
    }

    private async void Test()
    {
        if (SelectedServer == null) return;
        var s = SelectedServer.Entry.Clone();
        Busy = true;
        Status = L.F("Main.Testing", s.Name);
        try
        {
            var result = await Task.Run(() => _host.KeySetup.Test(s));
            Status = L.F("Main.TestOkStatus", s.Name);
            _host.Health.CheckNow(s.Id);
            if (s.Facts?.OsId == null && ServerInventoryService.Supported(s)) _ = _host.Inventory.RefreshAsync(s.Id);
            MessageBox.Show(Owner!, L.F("Main.TestOk", s.Display, result), L.Get("Main.TestTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Status = L.F("Main.TestFailedStatus", s.Name);
            Warn(L.Get("Main.TestTitle"), L.F("Main.TestFailed", s.Display, ex.Message));
        }
        finally
        {
            Busy = false;
        }
    }

    private void OpenPortForwards()
    {
        if (SelectedServer == null) return;
        new PortForwardWindow(_host, SelectedServer.Entry.Clone(), this) { Owner = Owner }.Show();
    }

    private void OpenPortMonitor()
    {
        if (SelectedServer == null) return;
        new PortMonitorWindow(_host, SelectedServer.Entry.Id) { Owner = Owner }.ShowDialog();
    }

    /// <summary>Switches monitoring of the selected port node on or off.</summary>
    private void TogglePort()
    {
        if (SelectedNode is not PortNode { Server: { } server } node) return;
        var id = server.Entry.Id;
        var on = node.Monitored == null;
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.MonitoredPorts.RemoveAll(p => p.Port == node.Port);
            if (on) s.MonitoredPorts.Add(new MonitoredPort { Port = node.Port, Name = node.Listening?.Process });
        });
        _host.Health.CheckNow(id);
        Status = L.F(on ? "Port.MonitorOn" : "Port.MonitorOff", node.Port, server.Entry.Name);
    }

    /// <summary>Opens the run window: parameters, live output, results saved to the server.</summary>
    private void RunScript(ScriptEntry? script)
    {
        if (script == null || SelectedServer == null) return;
        new ScriptRunWindow(_host, SelectedServer.Entry.Id, script.Clone()) { Owner = Owner }.Show();
    }

    private void InstallDocker()
    {
        if (SelectedServer == null) return;
        new ScriptRunWindow(_host, SelectedServer.Entry.Id, BuiltinScripts.DockerScript(_host.Vault.Data)) { Owner = Owner }.Show();
    }

    private static string MenuName(ScriptEntry s) => s.Kind == ScriptKind.Compose ? s.Name + "  [compose]" : s.Name;

    private void OpenUptimeHistory()
    {
        if (SelectedServer == null) return;
        new UptimeWindow(_host, SelectedServer.Entry.Clone()) { Owner = Owner }.Show();
    }

    private void CopyAttribute()
    {
        if (SelectedNode is not AttributeNode a) return;
        Clipboard.SetText(a.Attribute.Value);
        Status = L.F("Attr.Copied", a.Attribute.Label);
    }

    private void DeleteAttribute()
    {
        if (SelectedNode is not AttributeNode { Server: { } server } a) return;
        if (!Confirm(L.F("Attr.DeleteConfirm", a.Attribute.Label, server.Entry.Name))) return;
        var id = server.Entry.Id;
        var key = a.Attribute.Key;
        _host.Vault.Update(d => d.Servers.FirstOrDefault(s => s.Id == id)?.Attributes.RemoveAll(x => x.Key == key));
    }

    // ---------- containers ----------

    private void ContainerAction(Func<ContainerInfo, string> build, string doingKey, string doneKey)
    {
        if (SelectedNode is ContainerNode c) RunOnServer(c.Info.Name, build(c.Info), "Container.Title", doingKey, doneKey);
    }

    private void ServiceAction(Func<ServiceInfo, string> build, string doingKey, string doneKey)
    {
        if (SelectedNode is ServiceNode s) RunOnServer(s.Info.Title, build(s.Info), "Service.Title", doingKey, doneKey);
    }

    private void StopService()
    {
        if (SelectedNode is not ServiceNode { Server: { } node } s) return;
        var warn = ServiceCommands.IsSsh(s.Info) ? "\n\n" + L.Get("Service.SshWarning") : "";
        if (!Confirm(L.F("Service.StopConfirm", s.Info.Title, s.Info.Unit, node.Entry.Name) + warn)) return;
        ServiceAction(ServiceCommands.Stop, "Service.Stopping", "Service.Stopped");
    }

    private void RestartService()
    {
        if (SelectedNode is ServiceNode s && ServiceCommands.IsSsh(s.Info) && !Confirm(L.Get("Service.SshRestartConfirm"))) return;
        ServiceAction(ServiceCommands.Restart, "Service.Restarting", "Service.Restarted");
    }

    private void DisableService()
    {
        if (SelectedNode is not ServiceNode { Server: { } node } s) return;
        var warn = ServiceCommands.IsSsh(s.Info) ? "\n\n" + L.Get("Service.SshWarning") : "";
        if (!Confirm(L.F("Service.DisableConfirm", s.Info.Title, node.Entry.Name) + warn)) return;
        ServiceAction(ServiceCommands.Disable, "Service.Disabling", "Service.DisabledDone");
    }

    /// <summary>Runs a command on the selected node's server over SSH (sudo when needed), then re-reads the server.</summary>
    private async void RunOnServer(string name, string command, string titleKey, string doingKey, string doneKey)
    {
        if (SelectedServer is not { } node) return;
        var server = node.Entry.Clone();
        Busy = true;
        Status = L.F(doingKey, name);
        try
        {
            var r = await Task.Run(() =>
            {
                using var client = _host.Ssh.Connect(server);
                return RemoteShell.Run(client, server, command, elevated: true, TimeSpan.FromMinutes(5));
            });
            if (r.Ok) Status = L.F(doneKey, name);
            else
            {
                Status = "";
                Warn(L.Get(titleKey), L.F("Container.Failed", name, r.Combined));
            }
        }
        catch (Exception ex)
        {
            Status = "";
            Warn(L.Get(titleKey), ex.Message);
        }
        finally
        {
            Busy = false;
        }
        _ = _host.Inventory.RefreshAsync(server.Id, interactive: true);
    }

    private void StopContainer()
    {
        if (SelectedNode is not ContainerNode { Server: { } node } c) return;
        var hint = c.Info.Autostart ? "\n\n" + L.Get("Container.StopAutostartHint") : "";
        if (!Confirm(L.F("Container.StopConfirm", c.Info.Name, node.Entry.Name) + hint)) return;
        ContainerAction(ContainerCommands.Stop, "Container.Stopping", "Container.Stopped");
    }

    private void RemoveContainer()
    {
        if (SelectedNode is not ContainerNode { Server: { } node } c) return;
        if (ContainerRemoveWindow.Ask(Owner, c.Info, node.Entry.Name) is not { } removal) return;
        ContainerAction(i => ContainerCommands.Remove(i, removal), "Container.Removing", "Container.Removed");
    }

    private void SetAutostart(string? policy)
    {
        if (policy == null || !ContainerCommands.RestartPolicies.Contains(policy)) return;
        if (SelectedNode is ContainerNode { Info.ComposeProject: { } project } && !Confirm(L.F("Container.ComposePolicyNote", project))) return;
        ContainerAction(i => ContainerCommands.SetRestart(i, policy), "Container.Updating", "Container.Updated");
    }

    private void QuickAction(Func<TreeNode, string?> build, string title)
    {
        if (SelectedNode is not { Server: { } server } node || build(node) is not { } command) return;
        try
        {
            _host.Scripts.Launch(server.Entry, command, $"{title} {node.Title}");
        }
        catch (Exception ex)
        {
            Warn(L.Get("Main.SessionFailed"), ex.Message);
        }
    }

    private async void BackupNow()
    {
        if (!_host.Backup.IsConfigured)
        {
            Warn(L.Get("Backup.Title"), L.Get("Backup.NotConfigured"));
            SelectedTab = 2;
            return;
        }
        Status = L.Get("Backup.Running");
        try
        {
            var where = await _host.Backup.RunAsync();
            Status = L.F("Backup.Done", where);
        }
        catch (Exception ex)
        {
            Status = "";
            Warn(L.Get("Backup.Title"), ex.Message);
        }
        Settings.RefreshBackup();
    }

    private async void RestoreBackup()
    {
        var title = L.Get("Restore.Title");
        var backup = _host.Backup;
        (string Name, byte[] Data)? source = null;

        // 1. where from: the newest copy in the configured place, or any .zip on disk
        if (backup.IsConfigured)
        {
            var place = backup.Config.Target == BackupTarget.Folder
                ? backup.Config.Folder!
                : _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == backup.Config.ServerId)?.Name) ?? "?";
            var answer = MessageBox.Show(Owner!, L.F("Restore.ChooseSource", place), title,
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                Status = L.Get("Restore.Downloading");
                try
                {
                    source = await Task.Run(backup.DownloadLatest);
                }
                catch (Exception ex)
                {
                    Status = "";
                    Warn(title, ex.Message);
                    return;
                }
                Status = "";
                if (source == null)
                {
                    Warn(title, L.Get("Restore.NoneFound"));
                    return;
                }
            }
        }
        if (source == null)
        {
            var ofd = new OpenFileDialog { Title = title, Filter = L.Get("Restore.Filter") };
            if (backup.Config.Target == BackupTarget.Folder && Directory.Exists(backup.Config.Folder))
                ofd.InitialDirectory = backup.Config.Folder;
            if (ofd.ShowDialog(Owner) != true) return;
            source = (ofd.FileName, File.ReadAllBytes(ofd.FileName));
        }
        var (name, zip) = source.Value;

        // 2. it must be our archive and the password must open it; otherwise nothing is touched
        byte[] vaultFile;
        try
        {
            vaultFile = BackupService.ReadVault(zip);
        }
        catch (InvalidDataException ex)
        {
            Warn(title, ex.Message);
            return;
        }
        string? password;
        var prompt = L.F("Restore.PasswordPrompt", name);
        while (true)
        {
            password = InputDialog.Ask(Owner, title, prompt, password: true);
            if (string.IsNullOrEmpty(password)) return;
            Status = L.Get("Restore.Checking");
            try
            {
                await Task.Run(() => VaultService.CheckPassword(vaultFile, password));
                Status = "";
                break;
            }
            catch (WrongPasswordException)
            {
                Status = "";
                prompt = L.Get("Restore.WrongPassword") + "\n\n" + L.F("Restore.PasswordPrompt", name); // ask again right away
            }
            catch (Exception ex)
            {
                Status = "";
                Warn(title, ex.Message);
                return;
            }
        }
        if (!Confirm(L.F("Restore.Confirm", name))) return;

        // 3. replace; this window closes while the vault is relocked and reopens with the restored data
        try
        {
            var snapshot = await _host.RestoreAsync(zip, password);
            MessageBox.Show(L.F("Restore.Done", name, snapshot), title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void About()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(Owner!, L.F("About.Text", version, AppPaths.DataDir), L.Get("About.Title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- keys ----------

    private void GenerateKey()
    {
        var dlg = new KeyGenerateWindow { Owner = Owner };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var key = dlg.Result;
        _host.Vault.Update(d => d.Keys.Add(key));
        KeyService.EnsurePublicKeyFile(key);
        SelectedKey = Keys.FirstOrDefault(k => k.Entry.Id == key.Id);
        Status = L.F("Keys.CreatedStatus", key.Name);
    }

    private void ImportKey()
    {
        var ofd = new OpenFileDialog
        {
            Title = L.Get("Keys.ImportTitle"),
            Filter = L.Get("Keys.ImportFilter"),
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (ofd.ShowDialog(Owner) != true) return;
        string text;
        try
        {
            text = File.ReadAllText(ofd.FileName);
        }
        catch (Exception ex)
        {
            Warn(L.Get("Keys.ImportTitle"), ex.Message);
            return;
        }
        var name = Path.GetFileNameWithoutExtension(ofd.FileName);
        string? passphrase = null;
        while (true)
        {
            try
            {
                var key = KeyService.Import(text, name, passphrase);
                if (_host.Vault.Data.Keys.Any(k => k.Fingerprint == key.Fingerprint))
                {
                    Warn(L.Get("Keys.ImportTitle"), L.Get("Keys.AlreadyExists"));
                    return;
                }
                _host.Vault.Update(d => d.Keys.Add(key));
                KeyService.EnsurePublicKeyFile(key);
                Status = L.F("Keys.Imported", name, key.Fingerprint);
                return;
            }
            catch (EncryptedKeyException)
            {
                passphrase = InputDialog.Ask(Owner, L.Get("Keys.ImportTitle"),
                    L.Get(passphrase == null ? "Keys.AskPassphrase" : "Keys.WrongPassphrase"), password: true);
                if (passphrase == null) return;
            }
            catch (Exception ex)
            {
                if (passphrase != null && ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
                {
                    passphrase = InputDialog.Ask(Owner, L.Get("Keys.ImportTitle"), L.Get("Keys.WrongPassphrase"), password: true);
                    if (passphrase == null) return;
                    continue;
                }
                Warn(L.Get("Keys.ImportTitle"), L.Get("Keys.ReadFailed") + " " + ex.Message);
                return;
            }
        }
    }

    private void CopyPublicKey()
    {
        if (SelectedKey == null) return;
        Clipboard.SetText(SelectedKey.Entry.PublicKey);
        Status = L.Get("Keys.PublicCopied");
    }

    private void ExportPrivateKey()
    {
        if (SelectedKey == null) return;
        if (!Confirm(L.Get("Keys.ExportWarning"))) return;
        var sfd = new SaveFileDialog
        {
            Title = L.Get("Keys.ExportTitle"),
            FileName = "id_" + KeyService.SanitizeComment(SelectedKey.Name.Replace('@', '_')),
            Filter = "OpenSSH private key|*.*",
        };
        if (sfd.ShowDialog(Owner) != true) return;
        FileAcl.WritePrivate(sfd.FileName, SelectedKey.Entry.PrivateKey);
        File.WriteAllText(sfd.FileName + ".pub", SelectedKey.Entry.PublicKey + "\n");
        Status = L.F("Keys.Saved", sfd.FileName);
    }

    private void RenameKey()
    {
        if (SelectedKey == null) return;
        var id = SelectedKey.Entry.Id;
        var name = InputDialog.Ask(Owner, L.Get("Keys.RenameTitle"), L.Get("Keys.NewName"), SelectedKey.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _host.Vault.Update(d => d.Keys.First(k => k.Id == id).Name = name.Trim());
    }

    private void DeleteKey()
    {
        if (SelectedKey == null) return;
        var key = SelectedKey.Entry;
        var users = _host.Vault.Data.Servers.Where(s => s.KeyId == key.Id && s.Auth == AuthMode.Key).Select(s => s.Name).ToList();
        if (users.Count > 0)
        {
            Warn(L.Get("Keys.DeleteTitle"), L.F("Keys.InUse", string.Join("\n", users)));
            return;
        }
        if (!Confirm(L.F("Keys.DeleteConfirm", key.Name, key.Fingerprint))) return;
        _host.Vault.Update(d =>
        {
            d.Keys.RemoveAll(k => k.Id == key.Id);
            foreach (var s in d.Servers.Where(s => s.KeyId == key.Id)) s.KeyId = null;
        });
        var pub = Path.Combine(AppPaths.PublicKeyDir, key.Id.ToString("N") + ".pub");
        if (File.Exists(pub)) File.Delete(pub);
    }

    // ---------- helpers ----------

    public bool Confirm(string text) =>
        MessageBox.Show(Owner!, text, "SSH Manager", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) ==
        MessageBoxResult.Yes;

    public void Warn(string title, string text) =>
        MessageBox.Show(Owner!, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
