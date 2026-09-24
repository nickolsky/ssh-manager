using System.Windows;
using SshManager.Core.Crypto;
using SshManager.Core.Models;

namespace SshManager.Views;

public partial class KeyGenerateWindow : Window
{
    public KeyEntry? Result { get; private set; }

    public KeyGenerateWindow(string defaultName = "")
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        CommentBox.Text = KeyService.SanitizeComment(
            string.IsNullOrWhiteSpace(defaultName) ? $"{Environment.UserName}@{Environment.MachineName}" : defaultName);
        Loaded += (_, _) => NameBox.Focus();
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        var comment = KeyService.SanitizeComment(CommentBox.Text.Trim());
        var alg = TypeBox.SelectedIndex == 1 ? KeyAlgorithm.Rsa4096 : KeyAlgorithm.Ed25519;
        OkButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        Result = await Task.Run(() => KeyService.Generate(name, comment, alg));
        DialogResult = true;
    }
}
