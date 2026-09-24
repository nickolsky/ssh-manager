using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SshManager.Services;
using SshManager.ViewModels;

namespace SshManager.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _vm = new MainViewModel(host) { Owner = this };
        DataContext = _vm;
        Icon = IconFactory.CreateImage(true);

        var s = host.SettingsStore.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);

        InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        RestoreColumns(ServersGrid);
        RestoreColumns(KeysGrid);
        Closing += OnClosing;
        Loaded += (_, _) => ServersGrid.Focus();
    }

    /// <summary>Close for real (used on lock), bypassing hide-to-tray.</summary>
    public new void Close()
    {
        _forceClose = true;
        base.Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = _host.SettingsStore.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
        SaveColumns(ServersGrid);
        SaveColumns(KeysGrid);
        _host.SettingsStore.Save();
        if (!_forceClose && s.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _host.OnMainWindowClosed();
        if (!_forceClose) _host.Exit();
    }

    /// <summary>Applies saved pixel widths to all columns but the last one, which keeps filling the rest.</summary>
    private void RestoreColumns(DataGrid grid)
    {
        if (!_host.SettingsStore.Settings.ColumnWidths.TryGetValue(grid.Name, out var widths) ||
            widths.Length != grid.Columns.Count - 1)
            return;
        for (int i = 0; i < widths.Length; i++)
        {
            var col = grid.Columns[i];
            if (widths[i] >= col.MinWidth) col.Width = new DataGridLength(widths[i]);
        }
    }

    private void SaveColumns(DataGrid grid)
    {
        // A tab that was never shown has no measured columns; keep what was saved before.
        if (grid.Columns.Take(grid.Columns.Count - 1).Any(c => c.ActualWidth <= 0)) return;
        _host.SettingsStore.Settings.ColumnWidths[grid.Name] =
            grid.Columns.Take(grid.Columns.Count - 1).Select(c => Math.Round(c.ActualWidth)).ToArray();
    }

    private void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ServerRow }) _vm.Connect();
    }

    private void OnServersKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Connect();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.DeleteServerCommand.CanExecute(null))
        {
            _vm.DeleteServerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
