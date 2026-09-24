using System.Collections.ObjectModel;
using System.Windows;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Mvvm;

namespace SshManager.ViewModels;

/// <summary>Row of the server tree. Every node fills the same columns so one set of cell templates serves all.</summary>
public abstract class TreeNode(int level) : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public int Level { get; } = level;
    public Thickness Indent => new(Level * 18, 0, 0, 0);
    public TreeNode? Parent { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = [];
    public virtual bool CanExpand => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value)) OnExpandedChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    protected virtual void OnExpandedChanged()
    {
    }

    public abstract string Title { get; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public virtual string Icon => "";
    /// <summary>Status dot: ok, bad, warn, off, busy or empty (no dot).</summary>
    public virtual string Dot => "";
    public virtual string? Tip => null;
    public virtual bool IsMuted => false;
    public virtual bool IsBold => false;
    /// <summary>Small counter next to the title (groups).</summary>
    public virtual string Badge => "";
    public virtual string Address => "";
    public virtual string Os => "";
    public virtual string? OsTip => null;
    public virtual string Region => "";
    public virtual string CountryCode => "";
    public virtual string? RegionTip => null;
    public virtual string Forwards => "";
    public virtual string? ForwardsTip => null;
    public virtual string Auth => "";
    /// <summary>0..100 for the small usage bars; null hides the bar.</summary>
    public virtual double? CpuPercent => null;
    public virtual string Cpu => "";
    public virtual double? MemPercent => null;
    public virtual string Mem => "";
    public virtual string? UsageTip => null;
    /// <summary>Monitored ports shown as small chips in the server row.</summary>
    public virtual IReadOnlyList<PortChip> PortChips => [];
    public virtual string LastConnected => "";
    public virtual string Notes => "";

    /// <summary>The server this node belongs to (itself for a server node).</summary>
    public ServerNode? Server => this as ServerNode ?? Parent?.Server;

    protected void RaiseAll() => OnPropertyChanged(string.Empty);
}

public sealed record PortChip(string Label, string Dot, string? Tip);

public sealed class GroupNode(int level, string path, string name, Action<GroupNode> expandedChanged) : TreeNode(level)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public int ServerCount { get; set; }
    public int OfflineCount { get; set; }
    /// <summary>Servers that are up but have a monitored port down.</summary>
    public int WarnCount { get; set; }

    public override string Title => Name;
    public override string Badge => ServerCount.ToString();
    public override string Icon => IsExpanded ? "" : ""; // FolderOpen / Folder
    public override bool IsBold => true;
    public override string Address => OfflineCount > 0 ? L.F("Tree.GroupOffline", OfflineCount) : "";
    public override string Dot => OfflineCount > 0 ? "bad" : WarnCount > 0 ? "warn" : "";

    /// <summary>Suppresses persisting while the tree is built or a search forces groups open.</summary>
    public bool Silent { get; set; }

    protected override void OnExpandedChanged()
    {
        OnPropertyChanged(nameof(Icon));
        if (!Silent) expandedChanged(this);
    }

    public void Refresh() => RaiseAll();
}

public sealed class ServerNode : TreeNode
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<string, bool> _sectionState = [];

    public ServerNode(MainViewModel vm, int level, ServerEntry entry, TreeNode? parent) : base(level)
    {
        _vm = vm;
        Parent = parent;
        Entry = entry;
        BuildChildren();
    }

    public ServerEntry Entry { get; private set; }
    public ServerHealth Health { get; private set; } = ServerHealth.Unknown;
    public ServerMetrics? Metrics { get; private set; }
    public bool Loading { get; private set; }

    public override bool CanExpand => true;
    public override string Title => Entry.Name;
    public override string Icon => ""; // server-ish (PC)
    public override string Address => Entry.Display;
    public override string Notes => Entry.Notes?.ReplaceLineEndings(" ") ?? "";

    public override string Auth => Entry.Auth == AuthMode.Key ? "🔑 " + _vm.KeyName(Entry.KeyId) : L.Get("Auth.Password");

    public override string LastConnected => Entry.LastConnected?.ToString("g", L.Culture) ?? "—";

    public override string Os => Entry.Facts?.OsLabel ?? (Loading ? "…" : "");

    public override string? OsTip
    {
        get
        {
            var f = Entry.Facts;
            if (f == null) return null;
            var lines = new List<string>();
            if (f.OsPrettyName != null) lines.Add(f.OsPrettyName);
            if (f.Kernel != null) lines.Add(f.Kernel);
            if (f.InventoryUpdated is { } u) lines.Add(L.F("Tree.Updated", u.ToString("g", L.Culture)));
            if (f.InventoryError != null) lines.Add(L.Get("Tree.Error") + " " + f.InventoryError);
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    public override string Region
    {
        get
        {
            var g = Entry.Facts?.Geo;
            if (g == null) return "";
            return string.Join(", ", new[] { g.Country, g.City }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        }
    }

    public override string CountryCode => Entry.Facts?.Geo?.CountryCode ?? "";

    public override string? RegionTip
    {
        get
        {
            var g = Entry.Facts?.Geo;
            if (g == null) return null;
            var lines = new List<string> { g.Label, "IP: " + g.Ip };
            if (g.Isp != null) lines.Add(L.Get("Tree.Isp") + " " + g.Isp);
            return string.Join("\n", lines);
        }
    }

    public override string Forwards => string.Join(",  ", _vm.ForwardSummary(Entry));
    public override string? ForwardsTip => Forwards.Length == 0 ? null : string.Join("\n", _vm.ForwardSummary(Entry));

    public override string Dot => Health.State switch
    {
        HealthState.Online => Health.Error == null && DownPorts.Count == 0 ? "ok" : "warn",
        HealthState.Offline => "bad",
        HealthState.Checking => "busy",
        _ => "off",
    };

    public override string? Tip => Health.State switch
    {
        HealthState.Online => L.F("Health.OnlineTip", Health.LatencyMs, Health.Checked?.ToString("T", L.Culture)) +
                              (Health.Error != null ? "\n" + Health.Error : "") +
                              (DownPorts.Count > 0 ? "\n" + L.F("Health.PortsDown", string.Join(", ", DownPorts)) : ""),
        HealthState.Offline => L.F("Health.OfflineTip", Health.Checked?.ToString("T", L.Culture), Health.Error),
        HealthState.Checking => L.Get("Health.Checking"),
        HealthState.Disabled => L.Get("Health.Disabled"),
        HealthState.NotChecked => L.Get("Health.JumpHost"),
        _ => L.Get("Health.Unknown"),
    };

    public void RaiseForwardsChanged()
    {
        OnPropertyChanged(nameof(Forwards));
        OnPropertyChanged(nameof(ForwardsTip));
    }

    public IReadOnlyDictionary<int, ServerHealth> PortHealth { get; private set; } = new Dictionary<int, ServerHealth>();

    /// <summary>Monitored ports that did not answer on the last check.</summary>
    public List<int> DownPorts => Entry.MonitoredPorts.Select(p => p.Port)
        .Where(p => PortHealth.TryGetValue(p, out var h) && h.State == HealthState.Offline).Order().ToList();

    public string PortDot(int port) => PortHealth.TryGetValue(port, out var h)
        ? h.State switch { HealthState.Online => "ok", HealthState.Offline => "bad", _ => "off" }
        : "off";

    public string? PortTip(int port) => PortHealth.TryGetValue(port, out var h)
        ? h.State == HealthState.Online
            ? L.F("Port.OpenTip", port, h.LatencyMs, h.Checked?.ToString("T", L.Culture))
            : L.F("Port.ClosedTip", port, h.Checked?.ToString("T", L.Culture), h.Error)
        : L.F("Port.NotCheckedTip", port);

    public override IReadOnlyList<PortChip> PortChips => Entry.MonitoredPorts.OrderBy(p => p.Port)
        .Select(p => new PortChip(p.Port.ToString(), PortDot(p.Port), (p.Name is { Length: > 0 } n ? n + "\n" : "") + PortTip(p.Port)))
        .ToList();

    public void SetHealth(ServerHealth health, IReadOnlyDictionary<int, ServerHealth> ports)
    {
        Health = health;
        PortHealth = ports;
        OnPropertyChanged(nameof(Dot));
        OnPropertyChanged(nameof(Tip));
        OnPropertyChanged(nameof(PortChips));
        foreach (var n in Children.OfType<SectionNode>().Where(c => c.Key == "ports").SelectMany(c => c.Children).OfType<PortNode>())
            n.Refresh();
    }

    public override double? CpuPercent => Metrics?.CpuPercent;
    public override string Cpu => Metrics?.CpuPercent is { } c ? $"{c:0}%" : "";
    public override double? MemPercent => Metrics?.MemPercent;

    public override string Mem => Metrics is { MemTotalKb: { } total, MemAvailableKb: { } avail } ? Pair(total - avail, total) : "";

    /// <summary>"1.2 / 3.8 GB" or "830 / 977 MB" — one unit keeps the column narrow.</summary>
    private static string Pair(long usedKb, long totalKb) => totalKb >= 1024 * 1024
        ? $"{(usedKb / 1048576.0).ToString("0.#", L.Culture)} / {(totalKb / 1048576.0).ToString("0.#", L.Culture)} GB"
        : $"{usedKb / 1024} / {totalKb / 1024} MB";

    public override string? UsageTip
    {
        get
        {
            var m = Metrics;
            if (m == null) return null;
            var lines = new List<string>();
            if (m.CpuPercent is { } cpu) lines.Add(L.F("Metrics.Cpu", cpu, m.Load1, m.Cores));
            if (m.MemPercent is { } mem) lines.Add(L.F("Metrics.Mem", Gb(m.MemTotalKb!.Value - m.MemAvailableKb!.Value), Gb(m.MemTotalKb.Value), mem));
            if (m.DiskPercent is { } disk) lines.Add(L.F("Metrics.Disk", Gb(m.DiskUsedKb!.Value), Gb(m.DiskTotalKb!.Value), disk));
            if (m.Uptime is { } up) lines.Add(L.F("Metrics.Uptime", (int)up.TotalDays, up.Hours, up.Minutes));
            lines.Add(L.F("Metrics.Collected", m.Collected.ToString("T", L.Culture)));
            if (m.Error != null) lines.Add(L.Get("Tree.Error") + " " + m.Error);
            return string.Join("\n", lines);
        }
    }

    private static string Gb(long kb) => kb >= 1024 * 1024
        ? (kb / 1024.0 / 1024.0).ToString("0.#", L.Culture) + " GB"
        : (kb / 1024.0).ToString("0", L.Culture) + " MB";

    public void SetMetrics(ServerMetrics? metrics)
    {
        Metrics = metrics;
        OnPropertyChanged(nameof(CpuPercent));
        OnPropertyChanged(nameof(Cpu));
        OnPropertyChanged(nameof(MemPercent));
        OnPropertyChanged(nameof(Mem));
        OnPropertyChanged(nameof(UsageTip));
    }

    public void SetLoading(bool loading)
    {
        if (Loading == loading) return;
        Loading = loading;
        OnPropertyChanged(nameof(Os));
        if (IsExpanded) BuildChildren();
    }

    public void Update(ServerEntry entry)
    {
        Entry = entry;
        BuildChildren();
        RaiseAll();
    }

    protected override void OnExpandedChanged()
    {
        _vm.OnServerExpanded(this);
    }

    private void BuildChildren()
    {
        foreach (var c in Children.OfType<SectionNode>()) _sectionState[c.Key] = c.IsExpanded;
        Children.Clear();
        var f = Entry.Facts;
        var level = Level + 1;
        if (f?.InventoryUpdated == null)
        {
            Children.Add(new InfoNode(level, Loading ? L.Get("Tree.Loading") : f?.InventoryError ?? L.Get("Tree.NoData"), this));
            AddPorts(f);
            return;
        }
        if (f.InventoryError != null) Children.Add(new InfoNode(level, L.Get("Tree.Error") + " " + f.InventoryError, this));

        if (f.DockerAvailable)
        {
            var docker = Section("docker", L.F("Tree.Docker", f.Containers.Count(c => c.IsRunning), f.Containers.Count), "");
            foreach (var c in f.Containers) docker.Children.Add(new ContainerNode(level + 1, c, docker));
            if (f.Containers.Count == 0) docker.Children.Add(new InfoNode(level + 1, L.Get("Tree.NoContainers"), docker));
        }

        if (f.Services.Count > 0)
        {
            var services = Section("services", L.F("Tree.Services", f.Services.Count(s => s.IsRunning), f.Services.Count), "");
            foreach (var s in f.Services) services.Children.Add(new ServiceNode(level + 1, s, services));
        }

        var incoming = _vm.IncomingForwards(Entry).ToList();
        if (f.Forwards.Count > 0 || incoming.Count > 0)
        {
            var fw = Section("forwards", L.F("Tree.ForwardsSection", f.Forwards.Count + incoming.Count), "");
            foreach (var p in f.Forwards) fw.Children.Add(new ForwardNode(level + 1, _vm.DescribeForward(p), p, fw));
            foreach (var (from, p) in incoming)
                fw.Children.Add(new ForwardNode(level + 1, L.F("Tree.Incoming", from.Name, p.Protocol, p.ListenPort, p.EffectiveTargetPort), null, fw));
        }

        AddPorts(f);
    }

    /// <summary>Monitored ports plus the ones the server listens on (so they can be switched on for monitoring).</summary>
    private void AddPorts(ServerFacts? f)
    {
        var listening = (f?.ListeningPorts ?? []).ToDictionary(p => p.Port);
        var monitored = Entry.MonitoredPorts.DistinctBy(p => p.Port).ToDictionary(p => p.Port);
        var all = listening.Keys.Union(monitored.Keys).Order().ToList();
        if (all.Count == 0) return;
        var section = Section("ports", L.F("Tree.PortsSection", monitored.Count, all.Count), "\uE839");
        foreach (var port in all.OrderBy(p => monitored.ContainsKey(p) ? 0 : 1).ThenBy(p => p))
            section.Children.Add(new PortNode(Level + 2, this, port, monitored.GetValueOrDefault(port),
                listening.GetValueOrDefault(port), section));
    }

    private SectionNode Section(string key, string title, string icon)
    {
        var s = new SectionNode(Level + 1, key, title, icon, this);
        s.IsExpanded = _sectionState.TryGetValue(key, out var e) ? e : key != "services";
        Children.Add(s);
        return s;
    }
}

public sealed class SectionNode : TreeNode
{
    private readonly string _title;
    private readonly string _icon;

    public SectionNode(int level, string key, string title, string icon, TreeNode parent) : base(level)
    {
        Key = key;
        _title = title;
        _icon = icon;
        Parent = parent;
    }

    public string Key { get; }
    public override string Title => _title;
    public override string Icon => _icon;
    public override bool IsBold => true;
}

public sealed class InfoNode : TreeNode
{
    private readonly string _text;

    public InfoNode(int level, string text, TreeNode parent) : base(level)
    {
        _text = text;
        Parent = parent;
    }

    public override string Title => _text;
    public override string? Tip => _text;
    public override bool IsMuted => true;
}

public sealed class ContainerNode : TreeNode
{
    public ContainerNode(int level, ContainerInfo info, TreeNode parent) : base(level)
    {
        Info = info;
        Parent = parent;
    }

    public ContainerInfo Info { get; }
    public override string Title => Info.Name;
    public override string? Tip => $"{Info.Name}\n{Info.Image}\n{Info.Status}";
    public override string Icon => "";
    public override string Dot => Info.IsRunning ? "ok" : Info.State is "restarting" ? "warn" : "off";
    public override bool IsMuted => !Info.IsRunning;
    public override string Address => Info.Image;
    public override string Os => Info.Status;
    public override string Forwards => Info.Ports;
    public override string? ForwardsTip => Info.Ports.Length == 0 ? null : Info.Ports.Replace(", ", "\n");
}

public sealed class ServiceNode : TreeNode
{
    public ServiceNode(int level, ServiceInfo info, TreeNode parent) : base(level)
    {
        Info = info;
        Parent = parent;
    }

    public ServiceInfo Info { get; }
    public override string Title => Info.Title;
    public override string Icon => "";
    public override string Dot => Info.Active switch { "active" => "ok", "failed" => "bad", "activating" or "reloading" => "warn", _ => "off" };
    public override bool IsMuted => !Info.IsRunning;
    public override string Address => Info.Unit;
    public override string Os => $"{Info.Active} ({Info.Sub})";
}

public sealed class ForwardNode : TreeNode
{
    private readonly string _text;

    public ForwardNode(int level, string text, PortForward? forward, TreeNode parent) : base(level)
    {
        _text = text;
        Forward = forward;
        Parent = parent;
    }

    /// <summary>Null for incoming forwards (defined on another server).</summary>
    public PortForward? Forward { get; }
    public override string Title => _text;
    public override string? Tip => _text;
    public override string Icon => Forward == null ? "" : ""; // back / forward arrows
    public override string Address => Forward == null ? "" : Forward.Managed ? "SSH Manager" : L.Get("Fwd.External");
    public override bool IsMuted => Forward == null;
}

public sealed class PortNode : TreeNode
{
    private readonly ServerNode _server;

    public PortNode(int level, ServerNode server, int port, MonitoredPort? monitored, ListeningPort? listening, TreeNode parent)
        : base(level)
    {
        _server = server;
        Port = port;
        Monitored = monitored;
        Listening = listening;
        Parent = parent;
    }

    public int Port { get; }
    public MonitoredPort? Monitored { get; }
    public ListeningPort? Listening { get; }
    public bool CanMonitor => Monitored != null || Listening?.LocalOnly != true;

    public override string Title => Monitored?.Name is { Length: > 0 } n ? $"{Port}  {n}"
        : Listening?.Process is { Length: > 0 } p ? $"{Port}  {p}" : Port.ToString();

    public override string Icon => "\uE839";
    public override string Dot => Monitored == null ? "" : _server.PortDot(Port);
    public override string? Tip => Monitored == null ? null : _server.PortTip(Port);
    public override bool IsMuted => Monitored == null;
    public override string Address => Listening?.Addresses ?? "";

    public override string Os => Monitored != null
        ? _server.PortDot(Port) switch { "ok" => L.Get("Port.Open"), "bad" => L.Get("Port.Closed"), _ => L.Get("Port.Monitored") }
        : Listening?.LocalOnly == true ? L.Get("Port.LocalOnly") : L.Get("Port.NotMonitored");

    public void Refresh() => RaiseAll();
}
