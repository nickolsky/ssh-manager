using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SshManager.Core.Scripts;
using SshManager.Services;

namespace SshManager.Views;

/// <summary>
/// A script result as a QR code to scan with a phone: VPN links, site addresses, Amnezia keys
/// (their .conf for AmneziaWG and Amnezia VPN, or the key itself for Amnezia VPN).
/// </summary>
public partial class QrWindow : Window
{
    private readonly string _label;
    private QrPayload _current;

    /// <exception cref="ArgumentException">The value cannot be shown as a QR code (see <see cref="QrPayloads.CanShow"/>).</exception>
    public QrWindow(string label, string value)
    {
        InitializeComponent();
        var payloads = QrPayloads.For(value);
        if (payloads.Count == 0) throw new ArgumentException("Not a link", nameof(value));
        _label = label;
        Title = L.F("Qr.Title", label);
        Header.Text = AmneziaKey.Description(value) ?? label;
        _current = payloads[0];
        if (payloads.Count > 1)
        {
            foreach (var p in payloads)
            {
                var choice = new RadioButton { Content = p.Title, GroupName = "qr", Tag = p, Margin = new Thickness(0, 0, 0, 4), IsChecked = p == _current };
                choice.Checked += (_, _) => Show(p);
                Choices.Children.Add(choice);
            }
        }
        else Choices.Visibility = Visibility.Collapsed;
        Show(_current);
    }

    private void Show(QrPayload p)
    {
        _current = p;
        Code.Source = QrImage.Render(p.Text);
        PayloadText.Text = p.Text;
        SaveButton.Visibility = p.FileText != null ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = "";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_current.CopyText ?? _current.Text);
        Status.Text = L.Get("ScriptRun.Copied");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_current.FileText is not { } text) return;
        var name = new string(_label.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('-');
        var dialog = new SaveFileDialog { FileName = (name.Length > 0 ? name : "client") + ".conf", Filter = "WireGuard / AmneziaWG (*.conf)|*.conf" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, text.Replace("\r\n", "\n").TrimEnd('\n') + "\n", new UTF8Encoding(false));
            Status.Text = L.F("Qr.Saved", dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = ex.Message;
        }
    }
}
