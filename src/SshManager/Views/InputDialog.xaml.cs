using System.Windows;

namespace SshManager.Views;

public partial class InputDialog : Window
{
    private readonly bool _password;

    private InputDialog(string title, string prompt, string value, bool password)
    {
        InitializeComponent();
        _password = password;
        Title = title;
        PromptText.Text = prompt;
        if (password)
        {
            TextValue.Visibility = Visibility.Collapsed;
            PasswordInput.Visibility = Visibility.Visible;
        }
        TextValue.Text = value;
        Loaded += (_, _) =>
        {
            if (password) PasswordInput.Focus();
            else
            {
                TextValue.Focus();
                TextValue.SelectAll();
            }
        };
    }

    public string Value => _password ? PasswordInput.Password : TextValue.Text;

    public static string? Ask(Window? owner, string title, string prompt, string value = "", bool password = false)
    {
        var dlg = new InputDialog(title, prompt, value, password) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
