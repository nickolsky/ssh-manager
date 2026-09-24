using System.Windows;
using SshManager.Core.Models;
using SshManager.Services;

namespace SshManager.Views;

public partial class ServerEditorWindow : Window
{
    private readonly AppHost _host;
    private readonly ServerEntry _server;

    /// <param name="server">A copy to edit; on success it holds the new values.</param>
    public ServerEditorWindow(AppHost host, ServerEntry server, bool isNew)
    {
        InitializeComponent();
        _host = host;
        _server = server;
        Title = isNew ? L.Get("Editor.NewTitle") : L.F("Editor.EditTitle", server.Name);

        // every group path and its parents ("VPN", "VPN/Europe")
        GroupBox.ItemsSource = host.Vault.Data.Servers
            .SelectMany(s =>
            {
                var parts = ViewModels.MainViewModel.SplitGroup(s.Group);
                return Enumerable.Range(1, parts.Length).Select(n => string.Join("/", parts.Take(n)));
            })
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();
        MonitorBox.Text = server.MonitorIntervalMinutes?.ToString() ?? "";
        MonitorHint.Text = L.F("Editor.MonitorHint", host.SettingsStore.Settings.MonitorIntervalMinutes);
        NameBox.Text = server.Name;
        GroupBox.Text = server.Group;
        HostBox.Text = server.Host;
        PortBox.Text = server.Port.ToString();
        UserBox.Text = server.Username;
        PasswordBox.Password = server.Password ?? "";
        JumpBox.Text = server.JumpHost ?? "";
        ExtraBox.Text = server.ExtraArgs ?? "";
        NotesBox.Text = server.Notes ?? "";
        ReloadKeys(server.KeyId);
        if (server.Auth == AuthMode.Key) AuthKey.IsChecked = true;
        else AuthPassword.IsChecked = true;

        Loaded += (_, _) => (isNew ? HostBox : NameBox).Focus();
    }

    private void ReloadKeys(Guid? select)
    {
        var keys = _host.Vault.Data.Keys.OrderBy(k => k.Name).ToList();
        KeyBox.ItemsSource = keys;
        KeyBox.SelectedItem = keys.FirstOrDefault(k => k.Id == select) ?? (keys.Count == 1 ? keys[0] : null);
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e)
    {
        var key = AuthKey.IsChecked == true;
        KeyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowPassword(object sender, RoutedEventArgs e)
    {
        if (ShowPassword.IsChecked == true)
        {
            PasswordPlain.Text = PasswordBox.Password;
            PasswordPlain.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordBox.Password = PasswordPlain.Text;
            PasswordPlain.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
        }
    }

    private void OnNewKey(object sender, RoutedEventArgs e)
    {
        var defaultName = string.IsNullOrWhiteSpace(UserBox.Text) ? "" : $"{UserBox.Text.Trim()}@{(NameBox.Text.Trim() is { Length: > 0 } n ? n : HostBox.Text.Trim())}";
        var dlg = new KeyGenerateWindow(defaultName) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _host.Vault.Update(d => d.Keys.Add(dlg.Result));
        ReloadKeys(dlg.Result.Id);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            ShowError(L.Get("Editor.NeedHost"));
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ShowError(L.Get("Editor.BadPort"));
            return;
        }
        var user = UserBox.Text.Trim();
        if (user.Length == 0)
        {
            ShowError(L.Get("Editor.NeedUser"));
            return;
        }
        var auth = AuthKey.IsChecked == true ? AuthMode.Key : AuthMode.Password;
        var key = KeyBox.SelectedItem as KeyEntry;
        if (auth == AuthMode.Key && key == null)
        {
            ShowError(L.Get("Editor.NeedKey"));
            return;
        }
        int? monitor = null;
        if (MonitorBox.Text.Trim() is { Length: > 0 } m)
        {
            if (!int.TryParse(m, out var minutes) || minutes < 0)
            {
                ShowError(L.Get("Editor.BadMonitor"));
                return;
            }
            monitor = minutes;
        }
        var password = ShowPassword.IsChecked == true ? PasswordPlain.Text : PasswordBox.Password;

        _server.Name = NameBox.Text.Trim() is { Length: > 0 } n ? n : host;
        _server.Group = string.Join("/", ViewModels.MainViewModel.SplitGroup(GroupBox.Text));
        _server.MonitorIntervalMinutes = monitor;
        _server.Host = host;
        _server.Port = port;
        _server.Username = user;
        _server.Auth = auth;
        _server.Password = string.IsNullOrEmpty(password) ? null : password;
        _server.KeyId = key?.Id ?? (auth == AuthMode.Password ? _server.KeyId : null);
        _server.JumpHost = NullIfEmpty(JumpBox.Text);
        _server.ExtraArgs = NullIfEmpty(ExtraBox.Text);
        _server.Notes = NullIfEmpty(NotesBox.Text);
        DialogResult = true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
