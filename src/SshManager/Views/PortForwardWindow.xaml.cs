using System.ComponentModel;
using System.Windows;
using SshManager.Core.Forwarding;
using SshManager.Core.Geo;
using SshManager.Core.Models;
using SshManager.Services;
using SshManager.ViewModels;

namespace SshManager.Views;

public partial class PortForwardWindow : Window
{
    private sealed record Row(PortForward Forward, string Protocol, string ListenPort, string Target, string Interface, string Owner);

    private sealed record TargetChoice(string Label, string Host);

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private readonly MainViewModel _vm;
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
        ForwardsGrid.ItemsSource = forwards.Select(f => new Row(f, f.Protocol, f.ListenPort,
            $"{_vm.TargetName(f.TargetIp)}:{f.EffectiveTargetPort}" + (_vm.TargetName(f.TargetIp) != f.TargetIp ? $"  ({f.TargetIp})" : ""),
            f.InInterface ?? "", f.Managed ? "SSH Manager" : L.Get("Fwd.External"))).ToList();
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private async Task Run(Func<Action<string>, ForwardChange?> work)
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
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
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
        AddButton.IsEnabled = RefreshButton.IsEnabled = DeleteButton.IsEnabled = PersistButton.IsEnabled = !busy;
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
        await Run(log =>
        {
            var ip = GeoIpService.ResolveAsync(targetText).GetAwaiter().GetResult() ?? ResolveAny(targetText);
            foreach (var p in protocols) IptablesCommands.Validate(p, listen, ip, targetPort);
            ForwardChange? last = null;
            foreach (var p in protocols)
            {
                log(L.F("Fwd.Adding", p, listen, ip, targetPort.Length == 0 ? listen : targetPort));
                last = _host.Forwards.Add(server, p, listen, ip, targetPort, log);
            }
            log(L.Get("Fwd.Added"));
            return last;
        });
    }

    /// <summary>Private addresses are fine as forward targets (GeoIP resolution skips them).</summary>
    private static string ResolveAny(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var ip)) return ip.ToString();
        var addr = System.Net.Dns.GetHostAddresses(host)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        return addr?.ToString() ?? throw new InvalidOperationException(L.F("Fwd.CannotResolve", host));
    }

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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
