using System.Windows;
using SshManager.Core.Files;
using SshManager.ViewModels;

namespace SshManager.Views;

/// <summary>"The file already exists": overwrite, skip (this one or all), cancel the transfer.</summary>
public partial class ConflictWindow : Window
{
    private ConflictChoice _choice = ConflictChoice.Cancel;

    private ConflictWindow(FileItem source, FileItem existing)
    {
        InitializeComponent();
        Header.Text = L.F("Files.ConflictHeader", existing.Name);
        NewInfo.Text = Info(source);
        OldInfo.Text = Info(existing);
    }

    private static string Info(FileItem i) =>
        (i.IsDirectory ? L.Get("Files.Folder") : FileRow.FormatSize(i.Size)) + (i.Modified is { } m ? " · " + m.ToString("yyyy-MM-dd HH:mm:ss") : "");

    public static ConflictChoice Ask(Window? owner, FileItem source, FileItem existing)
    {
        var w = new ConflictWindow(source, existing) { Owner = owner };
        w.ShowDialog();
        return w._choice;
    }

    private void Close(ConflictChoice c)
    {
        _choice = c;
        DialogResult = true;
    }

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ConflictChoice.Overwrite);
    private void OnOverwriteAll(object sender, RoutedEventArgs e) => Close(ConflictChoice.OverwriteAll);
    private void OnSkip(object sender, RoutedEventArgs e) => Close(ConflictChoice.Skip);
    private void OnSkipAll(object sender, RoutedEventArgs e) => Close(ConflictChoice.SkipAll);
}
