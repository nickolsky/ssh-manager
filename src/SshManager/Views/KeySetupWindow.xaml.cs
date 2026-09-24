using System.ComponentModel;
using System.Windows;
using SshManager.Core.Models;
using SshManager.Services;

namespace SshManager.Views;

public partial class KeySetupWindow : Window
{
    private sealed record KeyChoice(Guid? Id, string Name);

    private readonly AppHost _host;
    private readonly ServerEntry _server;
    private bool _running;

    public KeySetupWindow(AppHost host, ServerEntry server)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Header.Text = $"{server.Name}  ({server.Display})";
        var choices = new List<KeyChoice> { new(null, L.Get("KeySetup.GenerateNew")) };
        choices.AddRange(host.Vault.Data.Keys.OrderBy(k => k.Name).Select(k => new KeyChoice(k.Id, L.Get("KeySetup.Existing") + " " + k.Name)));
        KeyBox.ItemsSource = choices;
        KeyBox.SelectedIndex = 0;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running) e.Cancel = true;
    }

    private void Append(string line) =>
        Dispatcher.Invoke(() =>
        {
            Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
            Log.ScrollToEnd();
        });

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var keyId = (KeyBox.SelectedItem as KeyChoice)?.Id;
        _running = true;
        StartButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        KeyBox.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            var server = _server;
            var key = await Task.Run(() => _host.KeySetup.SetupKeyAuth(server, keyId, Append));
            Append(L.F("KeySetup.Done", key.Name, key.Fingerprint));
            StartButton.Content = L.Get("KeySetup.DoneButton");
        }
        catch (Exception ex)
        {
            Append(L.Get("Common.Error") + " " + ex.Message);
            StartButton.IsEnabled = true;
            KeyBox.IsEnabled = true;
        }
        finally
        {
            _running = false;
            CloseButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
        }
    }
}
