using System.Windows;
using SshManager.Core.Crypto;
using SshManager.Core.Storage;

namespace SshManager.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly VaultService _vault;

    public ChangePasswordWindow(VaultService vault)
    {
        InitializeComponent();
        _vault = vault;
        Loaded += (_, _) => Current.Focus();
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        string cur = Current.Password, pwd = New.Password;
        if (pwd.Length < 8)
        {
            ShowError(L.Get("ChangePwd.TooShort"));
            return;
        }
        if (pwd != Confirm.Password)
        {
            ShowError(L.Get("Unlock.Mismatch"));
            return;
        }
        OkButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => _vault.ChangePassword(cur, pwd));
            MessageBox.Show(this, L.Get("ChangePwd.Done"), "SSH Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            OkButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
            if (ex is WrongPasswordException) Current.Clear();
            Current.Focus();
        }
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
