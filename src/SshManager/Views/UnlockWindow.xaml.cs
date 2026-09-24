using System.Windows;
using SshManager.Core.Crypto;
using SshManager.Services;

namespace SshManager.Views;

public partial class UnlockWindow : Window
{
    private readonly AppHost _host;
    private readonly bool _create;

    public UnlockWindow(AppHost host, bool create)
    {
        InitializeComponent();
        _host = host;
        _create = create;
        Icon = IconFactory.CreateImage(false);
        if (create)
        {
            Header.Text = L.Get("Unlock.CreateHeader");
            Subtitle.Text = L.Get("Unlock.CreateSubtitle");
            CreatePanel.Visibility = Visibility.Visible;
            OkButton.Content = L.Get("Unlock.Create");
        }
        Loaded += (_, _) =>
        {
            Activate();
            Password.Focus();
        };
    }


    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var pwd = Password.Password;
        if (pwd.Length == 0) return;
        if (_create)
        {
            if (pwd.Length < 8)
            {
                ShowError(L.Get("Unlock.TooShort"));
                return;
            }
            if (pwd != Confirm.Password)
            {
                ShowError(L.Get("Unlock.Mismatch"));
                return;
            }
        }

        SetBusy(true);
        try
        {
            if (_create)
            {
                await Task.Run(() => _host.Vault.Create(pwd));
                var autostart = AutostartBox.IsChecked == true;
                _host.SettingsStore.Settings.Autostart = autostart;
                _host.SettingsStore.Save();
                _host.TryApplyAutostart(autostart);
            }
            else
            {
                await Task.Run(() => _host.Vault.Unlock(pwd));
            }
            DialogResult = true;
        }
        catch (WrongPasswordException ex)
        {
            ShowError(ex.Message);
            Password.Clear();
            Password.Focus();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        OkButton.IsEnabled = !busy;
        Password.IsEnabled = !busy;
        if (busy) Error.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
