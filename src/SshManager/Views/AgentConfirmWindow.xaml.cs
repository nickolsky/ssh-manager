using System.Windows;
using System.Windows.Threading;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>
/// Asks the user whether an AI agent may do something dangerous (reboot, delete). On top of everything; "Deny" is the
/// default button, and no answer within the time limit counts as a refusal.
/// </summary>
public partial class AgentConfirmWindow : Window
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _deadline = DateTime.Now + Timeout;

    public AgentConfirmWindow(string title, string text)
    {
        InitializeComponent();
        Icon = IconFactory.CreateImage(true);
        Title = title;
        Header.Text = title;
        Body.Text = text;
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>True only when the user pressed "Allow".</summary>
    public bool Allowed { get; private set; }

    private void Tick()
    {
        var left = _deadline - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            Close();
            return;
        }
        Countdown.Text = L.F("Mcp.ConfirmCountdown", (int)left.TotalSeconds);
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e) => Close();
}
