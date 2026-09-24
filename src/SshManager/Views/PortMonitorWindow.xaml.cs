using System.Collections.ObjectModel;
using System.Windows;
using SshManager.Core.Models;
using SshManager.Mvvm;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>Chooses which TCP ports of a server are checked: detected listeners, forwards and manual entries.</summary>
public partial class PortMonitorWindow : Window
{
    public sealed class PortRow : ObservableObject
    {
        private bool _monitor;
        private string _name = "";

        public int Port { get; init; }
        public string Source { get; set; } = "";
        public bool CanMonitor { get; set; } = true;

        public bool Monitor
        {
            get => _monitor;
            set => Set(ref _monitor, value);
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }
    }

    private readonly AppHost _host;
    private readonly Guid _serverId;
    private readonly ObservableCollection<PortRow> _rows = [];

    public PortMonitorWindow(AppHost host, Guid serverId)
    {
        InitializeComponent();
        _host = host;
        _serverId = serverId;
        PortsGrid.ItemsSource = _rows;
        var server = Server();
        Header.Text = server == null ? "" : $"{server.Name}  ({server.Display})";
        Load(keepChoices: false);
    }

    private ServerEntry? Server() => _host.Vault.Data.Servers.FirstOrDefault(s => s.Id == _serverId);

    /// <summary>Rebuilds the list from the vault; with keepChoices the unsaved ticks and names survive.</summary>
    private void Load(bool keepChoices)
    {
        var server = Server();
        if (server == null) return;
        var previous = _rows.ToDictionary(r => r.Port);
        _rows.Clear();
        var monitored = server.MonitoredPorts.ToDictionary(p => p.Port);
        var listening = (server.Facts?.ListeningPorts ?? []).ToDictionary(p => p.Port);
        var forwards = (server.Facts?.Forwards ?? []).Where(f => f.Protocol == "tcp" && int.TryParse(f.ListenPort, out _))
            .GroupBy(f => int.Parse(f.ListenPort)).ToDictionary(g => g.Key, g => g.First());

        var ports = monitored.Keys.Union(listening.Keys).Union(forwards.Keys)
            .Union(keepChoices ? previous.Keys : []).Order();
        foreach (var port in ports)
        {
            listening.TryGetValue(port, out var lis);
            forwards.TryGetValue(port, out var fwd);
            var source = lis != null
                ? L.F(lis.LocalOnly ? "PortMon.SourceLocal" : "PortMon.SourceListening", lis.Process ?? "?")
                : fwd != null ? L.F("PortMon.SourceForward", fwd.TargetIp, fwd.EffectiveTargetPort)
                : L.Get("PortMon.SourceManual");
            var row = new PortRow
            {
                Port = port,
                Source = source,
                CanMonitor = lis?.LocalOnly != true || monitored.ContainsKey(port),
                Monitor = monitored.ContainsKey(port),
                Name = monitored.GetValueOrDefault(port)?.Name ?? lis?.Process ?? "",
            };
            if (keepChoices && previous.TryGetValue(port, out var old))
            {
                row.Monitor = old.Monitor;
                row.Name = old.Name;
            }
            _rows.Add(row);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError(L.Get("Editor.BadPort"));
            return;
        }
        Error.Visibility = Visibility.Collapsed;
        var existing = _rows.FirstOrDefault(r => r.Port == port);
        if (existing != null)
        {
            existing.Monitor = true;
            if (NewName.Text.Trim() is { Length: > 0 } n) existing.Name = n;
        }
        else
        {
            var row = new PortRow { Port = port, Source = L.Get("PortMon.SourceManual"), Monitor = true, Name = NewName.Text.Trim() };
            var index = _rows.TakeWhile(r => r.Port < port).Count();
            _rows.Insert(index, row);
        }
        NewPort.Clear();
        NewName.Clear();
    }

    private async void OnDetect(object sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _host.Inventory.RefreshAsync(_serverId, interactive: true);
            if (Server()?.Facts?.InventoryError is { } err) ShowError(err);
            Load(keepChoices: true);
        }
        finally
        {
            DetectButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        PortsGrid.CommitEdit();
        var ports = _rows.Where(r => r.Monitor)
            .Select(r => new MonitoredPort { Port = r.Port, Name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name.Trim() })
            .ToList();
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == _serverId);
            if (s != null) s.MonitoredPorts = ports;
        });
        _host.Health.CheckNow(_serverId);
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
