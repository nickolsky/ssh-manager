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
            _host.SettingsStore.Save();
        }
        if (!_forceClose && s.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _host.OnMainWindowClosed();
        if (!_forceClose) _host.Exit();
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
