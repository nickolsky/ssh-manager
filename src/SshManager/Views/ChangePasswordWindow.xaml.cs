using System.Windows;
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
            ShowError("Новый пароль должен быть не короче 8 символов.");
            return;
        }
        if (pwd != Confirm.Password)
        {
            ShowError("Пароли не совпадают.");
            return;
        }
        OkButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => _vault.ChangePassword(cur, pwd));
            MessageBox.Show(this, "Мастер-пароль изменён.", "SSH Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            OkButton.IsEnabled = true;
            Busy.Visibility = Visibility.Hidden;
        }
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
