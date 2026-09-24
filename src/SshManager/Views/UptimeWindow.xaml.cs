using System.Windows;
using System.Windows.Controls;
using SshManager.Core.Models;
using SshManager.Core.Monitoring;
using SshManager.Services;
using SshManager.ViewModels;

namespace SshManager.Views;

public sealed record OutageRow(string From, string To, string Duration);

/// <summary>Uptime history of one server: summary, availability and latency charts, list of outages.</summary>
public partial class UptimeWindow : Window
{
    /// <summary>Period length and number of bars.</summary>
    private static readonly (TimeSpan Length, int Bars, string Axis)[] Periods =
    [
        (TimeSpan.FromHours(24), 96, "HH:mm"),   // 15 min
        (TimeSpan.FromDays(7), 168, "dd.MM"),    // 1 h
        (TimeSpan.FromDays(30), 120, "dd.MM"),   // 6 h
        (TimeSpan.FromDays(90), 90, "dd.MM"),    // 1 day
        (TimeSpan.FromDays(365), 365, "MM.yy"),  // 1 day
    ];

    private readonly AppHost _host;
    private readonly ServerEntry _server;

    public UptimeWindow(AppHost host, ServerEntry server)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Icon = IconFactory.CreateImage(true);
        Title = L.F("Uptime.WindowTitle", server.Name);
        Header.Text = $"{server.Name}  ({server.Display})";
        _host.Uptime.Changed += OnLogChanged;
        Closed += (_, _) => _host.Uptime.Changed -= OnLogChanged;
        Period.SelectedIndex = 1;
    }

    private void OnLogChanged(object? sender, Guid id)
    {
        if (id == _server.Id) Dispatcher.BeginInvoke(Refresh);
    }

    private void OnPeriodChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (Period.SelectedIndex < 0) return;
        var (length, bars, axis) = Periods[Period.SelectedIndex];
        var to = DateTime.UtcNow;
        var from = to - length;
        var samples = _host.Uptime.Read(_server.Id, from);

        var buckets = UptimeLog.Buckets(samples, from, to, bars);
        AvailabilityChart.AxisFormat = LatencyChart.AxisFormat = axis;
        AvailabilityChart.Buckets = buckets;
        LatencyChart.Buckets = buckets;

        var percent = UptimeLog.Percent(samples, from, to);
        var outages = UptimeLog.Outages(samples, from, to);
        var latencies = samples.Where(s => s.Utc >= from && s.Up && s.LatencyMs != null).Select(s => s.LatencyMs!.Value).ToList();
        UptimeValue.Text = ServerNode.Pct(percent);
        OutagesValue.Text = outages.Count.ToString(L.Culture);
        DowntimeValue.Text = Duration(TimeSpan.FromSeconds(outages.Sum(o => o.Duration.TotalSeconds)));
        LatencyValue.Text = latencies.Count > 0 ? L.F("Uptime.Ms", latencies.Average()) : "—";

        OutagesHeader.Text = L.F("Uptime.Outages", outages.Count);
        OutagesGrid.ItemsSource = outages.Select(o => new OutageRow(
            o.FromUtc.ToLocalTime().ToString("g", L.Culture),
            o.ToUtc >= to.AddMinutes(-1) ? L.Get("Uptime.Ongoing") : o.ToUtc.ToLocalTime().ToString("g", L.Culture),
            Duration(o.Duration))).ToList();

        var observed = samples.Count(s => s.Utc >= from);
        Footer.Text = observed == 0
            ? L.Get("Uptime.Empty")
            : L.F("Uptime.Footer", observed, samples.First(s => s.Utc >= from).Utc.ToLocalTime().ToString("g", L.Culture));
    }

    public static string Duration(TimeSpan t) => t.TotalSeconds < 1 ? "0"
        : t.TotalMinutes < 1 ? L.F("Uptime.Sec", (int)t.TotalSeconds)
        : t.TotalHours < 1 ? L.F("Uptime.Min", (int)t.TotalMinutes)
        : t.TotalDays < 1 ? L.F("Uptime.HourMin", (int)t.TotalHours, t.Minutes)
        : L.F("Uptime.DayHour", (int)t.TotalDays, t.Hours);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
