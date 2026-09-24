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
        Title = $"{AppPaths.ProductTitle} {Core.Updates.UpdateService.Current.ToString(3)}";

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
            foreach (var t in ContentTabs.ToList())
                if (t.Content is IDisposable d) d.Dispose();
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
        var open = TerminalTabs.Count(t => ((TerminalView)t.Content).State != TerminalState.Closed) +
                   ContentTabs.Count(t => t.Content is FileManagerView { HasRunningTransfers: true } or EditorView { IsDirty: true });
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

    // ---------- tabs: built-in terminals, file managers, editors ----------

    private IEnumerable<TabItem> TerminalTabs => Tabs.Items.OfType<TabItem>().Where(t => t.Content is TerminalView);
    private IEnumerable<TabItem> ContentTabs => Tabs.Items.OfType<TabItem>().Where(t => t.Content is IDisposable);

    /// <summary>A closable tab (× button, middle click) with a status dot; returns the tab and the dot painter.</summary>
    private (TabItem Tab, Action<string> Paint, TextBlock Title) AddTab(FrameworkElement content, string title, string? icon, string tooltip)
    {
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = new TextBlock { Text = "\uE711", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 },
            Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(10, 0, -8, 0), MinWidth = 0, MinHeight = 0,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = L.Get("Term.CloseTab"),
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(dot);
        if (icon != null)
            header.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(text);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = content, ToolTip = tooltip };
        close.Click += (_, _) => CloseTab(tab);
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) CloseTab(tab);
        };
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return (tab, brush => dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush), text);
    }

    /// <summary>Opens a terminal tab for the server and switches to it.</summary>
    /// <param name="cd">Start in this remote folder (an interactive shell, not a command).</param>
    public void OpenTerminal(ServerEntry server, string? command, string? title, string? cd = null)
    {
        var entry = server.Clone();
        // leading space: most shells keep it out of the history; clear hides the typed command line
        var typed = command != null ? " clear; " + command : cd != null ? " cd " + Core.Ssh.RemoteShell.Quote(cd) + " && clear" : null;
        var view = new TerminalView(() => _host.CreateTerminalSession(entry), typed, _host.SettingsStore.Settings.TerminalAutoSuggest);
        var (tab, paint, _) = AddTab(view, title ?? entry.Name, null, L.F("Term.TabTip", entry.Name, entry.Display, ""));

        void Paint(TerminalState state) => paint(state switch
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
            if (state == TerminalState.Closed && view.EndedNormally && command == null) CloseTab(tab);
        };
        view.TitleChanged += t => tab.ToolTip = L.F("Term.TabTip", entry.Name, entry.Display, t);
        view.EditRequested += path => OpenEditor(entry, path);
        view.KeyCommand += key =>
        {
            switch (key)
            {
                case "close": CloseTab(tab); break;
                case "next": Cycle(+1); break;
                case "prev": Cycle(-1); break;
                case "duplicate": OpenTerminal(entry, null, null, view.Cwd); break;
                case "files": OpenFiles(entry, view.Cwd); break;
            }
        };
        tab.ContextMenu = TabMenu(tab, entry, () => view.Cwd, () => OpenTerminal(entry, null, null, view.Cwd));
    }

    /// <summary>The server's file manager tab (one per server): opens it or shows <paramref name="dir"/> in it.</summary>
    public void OpenFiles(ServerEntry server, string? dir)
    {
        var existing = ContentTabs.FirstOrDefault(t => t.Content is FileManagerView f && f.Server.Id == server.Id);
        if (existing is { Content: FileManagerView open })
        {
            Tabs.SelectedItem = existing;
            if (dir != null) open.Navigate(dir);
            if (!IsVisible) Show();
            Activate();
            return;
        }
        var entry = server.Clone();
        var view = new FileManagerView(_host, entry, dir);
        var (tab, paint, _) = AddTab(view, entry.Name, "\uE8B7", L.F("Files.TabTip", entry.Name, entry.Display));
        void Paint() => paint(view.IsConnected ? "SystemFillColorSuccessBrush" : view.HasError ? "SystemFillColorCriticalBrush" : "SystemFillColorAttentionBrush");
        Paint();
        view.StateChanged += Paint;
        view.EditRequested += path => OpenEditor(entry, path);
        view.TerminalRequested += path => OpenTerminal(entry, null, null, path);
        view.CloseRequested += () => CloseTab(tab);
        tab.ContextMenu = TabMenu(tab, entry, () => view.RemotePath, () => OpenTerminal(entry, null, null, view.RemotePath));
    }

    /// <summary>A remote text file in an editor tab (the same file of the same server opens once).</summary>
    public void OpenEditor(ServerEntry server, string path)
    {
        var existing = ContentTabs.FirstOrDefault(t => t.Content is EditorView e && e.Server.Id == server.Id && e.Path == path);
        if (existing != null)
        {
            Tabs.SelectedItem = existing;
            if (!IsVisible) Show();
            Activate();
            return;
        }
        var entry = server.Clone();
        var view = new EditorView(_host, entry, path);
        var (tab, paint, title) = AddTab(view, view.FileName, "\uE70F", L.F("TextEd.TabTip", path, entry.Name));
        void Paint()
        {
            paint(view.HasError ? "SystemFillColorCriticalBrush" : view.IsDirty ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
            title.Text = (view.IsDirty ? "● " : "") + view.FileName;
        }
        Paint();
        view.StateChanged += Paint;
        view.SavedAndClose += () => CloseTab(tab);
        view.KeyCommand += key =>
        {
            switch (key)
            {
                case "close": CloseTab(tab); break;
                case "next": Cycle(+1); break;
                case "prev": Cycle(-1); break;
            }
        };
        var dir = path[..Math.Max(1, path.LastIndexOf('/'))];
        tab.ContextMenu = TabMenu(tab, entry, () => dir, () => OpenTerminal(entry, null, null, dir));
    }

    /// <summary>Right click on a tab header.</summary>
    private ContextMenu TabMenu(TabItem tab, ServerEntry entry, Func<string?> dir, Action terminal)
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, Action a, string? gesture = null)
        {
            var mi = new MenuItem { Header = L.Get(key), InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Tab.NewTerminal", terminal, "Ctrl+Shift+T");
        Item("Tab.Files", () => OpenFiles(entry, dir()), "Ctrl+Shift+F");
        menu.Items.Add(new Separator());
        Item("Tab.Close", () => CloseTab(tab), "Ctrl+Shift+W");
        Item("Tab.CloseOthers", () =>
        {
            foreach (var t in ContentTabs.Where(t => t != tab).ToList()) CloseTab(t);
        });
        return menu;
    }

    /// <summary>Asks before closing a tab that would lose work (unsaved text, a running transfer).</summary>
    private void CloseTab(TabItem tab)
    {
        if (tab.Content is FileManagerView { HasRunningTransfers: true } &&
            MessageBox.Show(this, L.Get("Files.CloseWithTransfers"), AppPaths.ProductTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (tab.Content is EditorView editor && !editor.ConfirmClose()) return;
        if (tab.Content is IDisposable d) d.Dispose();
        var index = Tabs.Items.IndexOf(tab);
        if (index < 0) return;
        var wasSelected = Tabs.SelectedItem == tab;
        Tabs.Items.Remove(tab);
        // the next tab (or the previous one), the server list when none are left
        if (wasSelected) Tabs.SelectedIndex = ContentTabs.Any() ? Math.Min(index, Tabs.Items.Count - 1) : 0;
    }

    private void Cycle(int step)
    {
        var n = Tabs.Items.Count;
        if (n > 1) Tabs.SelectedIndex = ((Tabs.SelectedIndex + step) % n + n) % n;
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        Action? focus = Tabs.SelectedItem switch
        {
            TabItem { Content: TerminalView t } => t.Focus,
            TabItem { Content: FileManagerView f } => f.Focus,
            TabItem { Content: EditorView ed } => ed.Focus,
            _ => null,
        };
        if (focus != null) Dispatcher.BeginInvoke(focus, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ---------- global shortcut box (Settings) ----------

    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e) => _host.SuspendHotkey();

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        _host.ApplyHotkey();
        _vm.Settings.RefreshHotkeyStatus();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key is Key.Tab) { e.Handled = false; return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.Settings.HotkeyText = "";
            _host.SuspendHotkey();
            return;
        }
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        if (GlobalHotkey.FromKeyPress(Keyboard.Modifiers, key, win) is not { } combo) return;
        _vm.Settings.HotkeyText = combo;
        // stays off while the box has the focus; the new one works once it loses it
        _host.SuspendHotkey();
        ServersTree.Focus();
    }

    private void OnHotkeyDefault(object sender, RoutedEventArgs e) => _vm.Settings.HotkeyText = GlobalHotkey.Default;
    private void OnHotkeyClear(object sender, RoutedEventArgs e) => _vm.Settings.HotkeyText = "";

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
