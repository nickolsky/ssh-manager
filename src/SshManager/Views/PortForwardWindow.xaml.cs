using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SshManager.Core.Forwarding;
using SshManager.Core.Geo;
using SshManager.Core.Models;
using SshManager.Services;
using SshManager.ViewModels;

namespace SshManager.Views;

public partial class PortForwardWindow : Window
{
    private sealed record Row(PortForward Forward, string Protocol, string ListenPort, string Target, string Chain, string ChainTip,
        string Interface, string Owner, bool Monitored, bool CanMonitor, string MonitorTip);

    private sealed record TargetChoice(string Label, string Host);

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly MainViewModel _vm;
    private List<PortForward> _forwards = [];
    /// <summary>The forward being changed in the form, null while adding a new one.</summary>
    private PortForward? _editing;
    private bool _running;

    public PortForwardWindow(AppHost host, ServerEntry server, MainViewModel vm)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        _vm = vm;
        Header.Text = $"{server.Name}  ({server.Display})";
        TargetBox.ItemsSource = host.Vault.Data.Servers.Where(s => s.Id != server.Id)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new TargetChoice($"{s.Name}  ({s.Host})", s.Host)).ToList();
        ShowForwards(server.Facts?.Forwards ?? []);
        Closing += (_, e) => e.Cancel = _running;
        Loaded += (_, _) => OnRefresh(this, new RoutedEventArgs());
    }

    private void ShowForwards(List<PortForward> forwards)
    {
        _forwards = forwards;
        var monitored = _host.Vault.Read(d => d.Servers.FirstOrDefault(s => s.Id == _server.Id)?.MonitoredPorts.Select(p => p.Port).ToHashSet()) ?? [];
        var selected = (ForwardsGrid.SelectedItem as Row)?.Forward.Key;
        ForwardsGrid.ItemsSource = forwards.Select(f =>
        {
            var name = _vm.TargetName(f.TargetIp);
            var chain = _vm.ForwardChain(_server, f);
            var single = int.TryParse(f.ListenPort, out var port);
            var canMonitor = single && f.Protocol == "tcp";
            return new Row(f, f.Protocol, f.ListenPort,
                $"{name}:{f.EffectiveTargetPort}" + (name != f.TargetIp ? $"  ({f.TargetIp})" : ""),
                ForwardChains.Format(chain), ForwardChains.FormatLines(chain),
                f.InInterface ?? "", f.Managed ? "SSH Manager" : L.Get("Fwd.External"),
                canMonitor && monitored.Contains(port), canMonitor,
                canMonitor ? L.F("Fwd.MonitorTip", f.ListenPort) : L.Get(single ? "Fwd.MonitorUdp" : "Fwd.MonitorRange"));
        }).ToList();
        if (selected != null) ForwardsGrid.SelectedItem = ((List<Row>)ForwardsGrid.ItemsSource).FirstOrDefault(r => r.Forward.Key == selected);
        if (_editing != null && forwards.All(f => f.Key != _editing.Key)) StopEditing(); // changed or removed meanwhile
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private async Task<bool> Run(Func<Action<string>, ForwardChange?> work)
    {
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => work(Append));
            if (result != null)
            {
                ShowForwards(result.Forwards);
                PersistButton.Visibility = result.PersistedWith == null ? Visibility.Visible : Visibility.Collapsed;
            }
            return true;
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _running = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        AddButton.IsEnabled = RefreshButton.IsEnabled = DeleteButton.IsEnabled = EditButton.IsEnabled = PersistButton.IsEnabled =
            CancelEditButton.IsEnabled = !busy;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        var server = _server;
        await Run(log =>
        {
            log(L.F("Fwd.Reading", server.Display));
            var list = _host.Forwards.List(server);
            log(L.F("Fwd.Found", list.Count));
            return new ForwardChange(list, "-");
        });
    }

    // ---------- add / change ----------

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var listen = ListenBox.Text.Trim();
        var targetText = (TargetBox.SelectedItem as TargetChoice)?.Host ?? TargetBox.Text.Trim();
        var targetPort = TargetPortBox.Text.Trim();
        string[] protocols = ProtoBox.SelectedIndex switch { 1 => ["udp"], 2 => ["tcp", "udp"], _ => ["tcp"] };
        if (listen.Length == 0 || targetText.Length == 0)
        {
            Append(L.Get("Fwd.FillFields"));
            return;
        }
        var server = _server;
        var old = _editing;
        var ok = await Run(log =>
        {
            var ip = GeoIpService.ResolveAsync(targetText).GetAwaiter().GetResult() ?? ResolveAny(targetText);
            foreach (var p in protocols) IptablesCommands.Validate(p, listen, ip, targetPort);
            if (old != null)
            {
                log(L.F("Fwd.Changing", old.Protocol, old.ListenPort, string.Join("+", protocols), listen, ip, targetPort.Length == 0 ? listen : targetPort));
                var changed = _host.Forwards.Replace(server, old, protocols, listen, ip, targetPort, log);
                log(L.Get("Fwd.Changed"));
                return changed;
            }
            ForwardChange? last = null;
            foreach (var p in protocols)
            {
                log(L.F("Fwd.Adding", p, listen, ip, targetPort.Length == 0 ? listen : targetPort));
                last = _host.Forwards.Add(server, p, listen, ip, targetPort, log);
            }
            log(L.Get("Fwd.Added"));
            return last;
        });
        if (!ok) return;
        if (old != null)
        {
            MoveMonitoring(old, listen, protocols);
            StopEditing();
        }
    }

    /// <summary>A monitored listen port follows the forward when its port changes (and stops when it is no longer TCP).</summary>
    private void MoveMonitoring(PortForward old, string listen, string[] protocols)
    {
        if (!int.TryParse(old.ListenPort, out var was) || old.ListenPort == listen && protocols.Contains("tcp")) return;
        var id = _server.Id;
        var moved = false;
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == id);
            var m = s?.MonitoredPorts.FirstOrDefault(p => p.Port == was);
            if (s == null || m == null || _forwards.Any(f => f.ListenPort == old.ListenPort && f.Protocol == "tcp")) return;
            s.MonitoredPorts.Remove(m);
            if (protocols.Contains("tcp") && int.TryParse(listen, out var now) && s.MonitoredPorts.All(p => p.Port != now))
                s.MonitoredPorts.Add(new MonitoredPort { Port = now, Name = m.Name });
            moved = true;
        });
        if (!moved) return;
        _host.Health.CheckNow(id);
        ShowForwards(_forwards);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (ForwardsGrid.SelectedItem is Row row) StartEditing(row.Forward);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return;
        if (!_running && ForwardsGrid.SelectedItem is Row row) StartEditing(row.Forward);
    }

    private void StartEditing(PortForward f)
    {
        _editing = f;
        ProtoBox.SelectedIndex = f.Protocol == "udp" ? 1 : 0;
        ListenBox.Text = f.ListenPort;
        var choices = (List<TargetChoice>)TargetBox.ItemsSource;
        var server = _vm.ServerByIp(f.TargetIp);
        var choice = choices.FirstOrDefault(c => c.Host == f.TargetIp) ?? (server == null ? null : choices.FirstOrDefault(c => c.Host == server.Host));
        if (choice != null) TargetBox.SelectedItem = choice;
        else
        {
            TargetBox.SelectedItem = null;
            TargetBox.Text = f.TargetIp;
        }
        TargetPortBox.Text = f.TargetPort == f.ListenPort ? "" : f.TargetPort;
        FormTitle.Text = L.F("Fwd.EditTitle", f.Protocol, f.ListenPort, _vm.TargetName(f.TargetIp), f.EffectiveTargetPort);
        AddButton.Content = L.Get("Fwd.Save");
        CancelEditButton.Visibility = Visibility.Visible;
        ListenBox.Focus();
        ListenBox.SelectAll();
    }

    private void StopEditing()
    {
        _editing = null;
        FormTitle.Text = L.Get("Fwd.AddTitle");
        AddButton.Content = L.Get("Fwd.Add");
        CancelEditButton.Visibility = Visibility.Collapsed;
        ListenBox.Text = TargetPortBox.Text = "";
        TargetBox.SelectedItem = null;
        TargetBox.Text = "";
    }

    private void OnCancelEdit(object sender, RoutedEventArgs e) => StopEditing();

    /// <summary>Private addresses are fine as forward targets (GeoIP resolution skips them).</summary>
    private static string ResolveAny(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip)) return ip.ToString();
        var addr = System.Net.Dns.GetHostAddresses(host)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return addr?.ToString() ?? throw new InvalidOperationException(L.F("Fwd.CannotResolve", host));
    }

    // ---------- monitoring ----------

    /// <summary>
    /// Monitoring of the listen port on this server: the check connects through the forward, so it covers the whole chain
    /// up to the service at its end.
    /// </summary>
    private void OnMonitorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Row row } || !int.TryParse(row.ListenPort, out var port)) return;
        var on = !row.Monitored;
        var id = _server.Id;
        var name = "→ " + row.Target.Split("  (")[0];
        _host.Vault.Update(d =>
        {
            var s = d.Servers.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.MonitoredPorts.RemoveAll(p => p.Port == port);
            if (on) s.MonitoredPorts.Add(new MonitoredPort { Port = port, Name = name });
        });
        _host.Health.CheckNow(id);
        Append(L.F(on ? "Port.MonitorOn" : "Port.MonitorOff", port, _server.Name));
        ShowForwards(_forwards);
    }

    // ---------- delete / persistence ----------

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ForwardsGrid.SelectedItem is not Row row) return;
        var f = row.Forward;
        if (MessageBox.Show(this, L.F("Fwd.DeleteConfirm", f.Protocol, f.ListenPort, row.Target), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var server = _server;
        await Run(log =>
        {
            log(L.F("Fwd.Deleting", f.Protocol, f.ListenPort));
            var r = _host.Forwards.Remove(server, f, log);
            log(L.Get("Fwd.Deleted"));
            return r;
        });
    }

    private async void OnInstallPersistence(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.Get("Fwd.InstallPersistConfirm"), Title, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var server = _server;
        await Run(log =>
        {
            log(L.Get("Fwd.InstallingPersist"));
            return _host.Forwards.InstallPersistence(server, log);
        });
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null;
             p = p is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
