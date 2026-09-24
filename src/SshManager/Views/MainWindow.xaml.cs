using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshManager.Core;
using SshManager.Core.Models;
using SshManager.Services;
using SshManager.ViewModels;
using SshManager.Views.Controls;

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
        Title = AppPaths.ProductTitle;

        var s = host.SettingsStore.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);

        InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
        TreeListViewItem.DoubleClickOpens = item => item is ServerNode;
        RestoreColumns(ServersTree);
        RestoreColumns(KeysGrid);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            foreach (var t in TerminalTabs.ToList()) CloseTerminal(t);
            _vm.Dispose();
        };
        Loaded += (_, _) => ServersTree.Focus();
        Tabs.SelectionChanged += OnTabChanged;
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
        SaveColumns(ServersTree);
        SaveColumns(KeysGrid);
        _host.SettingsStore.Save();
        if (!_forceClose && s.CloseToTray)
        {
            e.Cancel = true;
            Hide(); // terminals keep running in the hidden window
            return;
        }
        var open = TerminalTabs.Count(t => ((TerminalView)t.Content).State != TerminalState.Closed);
        if (!_forceClose && open > 0 &&
            MessageBox.Show(this, L.F("Term.CloseAllConfirm", open), AppPaths.ProductTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _host.OnMainWindowClosed();
        if (!_forceClose) _host.Exit();
    }

    // ---------- built-in terminal tabs ----------

    private IEnumerable<TabItem> TerminalTabs => Tabs.Items.OfType<TabItem>().Where(t => t.Content is TerminalView);

    /// <summary>Opens a terminal tab for the server and switches to it.</summary>
    public void OpenTerminal(ServerEntry server, string? command, string? title)
    {
        var entry = server.Clone();
        // leading space: most shells keep it out of the history; clear hides the typed command line
        var typed = command == null ? null : " clear; " + command;
        var view = new TerminalView(() => _host.CreateTerminalSession(entry), typed);
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = title ?? entry.Name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 },
            Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(10, 0, -8, 0), MinWidth = 0, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = L.Get("Term.CloseTab"),
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text, close } };
        var tab = new TabItem { Header = header, Content = view, ToolTip = L.F("Term.TabTip", entry.Name, entry.Display, "") };
        close.Click += (_, _) => CloseTerminal(tab);
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTerminal(tab);
        };

        void Paint(TerminalState state) => dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch
        {
            TerminalState.Connected => "SystemFillColorSuccessBrush",
            TerminalState.Connecting => "SystemFillColorAttentionBrush",
            _ => "ControlStrongFillColorDisabledBrush",
        });
        Paint(TerminalState.Connecting);
        view.StateChanged += state =>
        {
            Paint(state);
            // "exit" in an interactive session closes the tab; commands keep their output on screen
            if (state == TerminalState.Closed && view.EndedNormally && command == null) CloseTerminal(tab);
        };
        view.TitleChanged += t => tab.ToolTip = L.F("Term.TabTip", entry.Name, entry.Display, t);
        view.KeyCommand += key =>
        {
            switch (key)
            {
                case "close": CloseTerminal(tab); break;
                case "next": Cycle(+1); break;
                case "prev": Cycle(-1); break;
                case "duplicate": OpenTerminal(entry, null, null); break;
            }
        };

        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void CloseTerminal(TabItem tab)
    {
        if (tab.Content is TerminalView view) view.Dispose();
        var index = Tabs.Items.IndexOf(tab);
        if (index < 0) return;
        var wasSelected = Tabs.SelectedItem == tab;
        Tabs.Items.Remove(tab);
        // the next terminal (or the previous one), the server list when none are left
        if (wasSelected) Tabs.SelectedIndex = TerminalTabs.Any() ? Math.Min(index, Tabs.Items.Count - 1) : 0;
    }

    private void Cycle(int step)
    {
        var n = Tabs.Items.Count;
        if (n > 1) Tabs.SelectedIndex = ((Tabs.SelectedIndex + step) % n + n) % n;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedItem is TabItem { Content: TerminalView view }) Dispatcher.BeginInvoke(view.Focus, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- column widths (all but the last column, which fills) ----------

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

    private void RestoreColumns(TreeListView tree)
    {
        if (!_host.SettingsStore.Settings.ColumnWidths.TryGetValue(tree.Name, out var widths) ||
            widths.Length != tree.Columns.Count - 1)
            return;
        for (int i = 0; i < widths.Length; i++)
            if (widths[i] >= 30) tree.Columns[i].Width = widths[i];
    }

    private void SaveColumns(TreeListView tree)
    {
        var cols = tree.Columns.Take(tree.Columns.Count - 1).ToList();
        if (cols.Any(c => c.ActualWidth <= 0)) return;
        _host.SettingsStore.Settings.ColumnWidths[tree.Name] = cols.Select(c => Math.Round(c.ActualWidth)).ToArray();
    }

    // ---------- tree ----------

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as TreeNode;

    /// <summary>Right click selects the row under the mouse, so the context menu acts on it.</summary>
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindItem(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm.SelectedNode is InfoNode or SectionNode)
        {
            e.Handled = true;
            return;
        }
        _vm.PrepareContextMenu();
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInside<System.Windows.Controls.Primitives.ToggleButton>(d)) return;
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not ServerNode node) return;
        if (!ReferenceEquals(_vm.SelectedNode, node)) return;
        _vm.Connect();
        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_vm.SelectedNode is ServerNode) _vm.Connect();
            else if (_vm.SelectedNode is { } n) n.IsExpanded = !n.IsExpanded;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.CanDelete)
        {
            _vm.DeleteServerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static TreeListViewItem? FindItem(DependencyObject? d)
    {
        while (d != null && d is not TreeListViewItem)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeListViewItem;
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null && x is not TreeListViewItem; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T) return true;
        return false;
    }
}
