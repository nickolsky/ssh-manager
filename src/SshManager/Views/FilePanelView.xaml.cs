using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SshManager.Core.Files;
using SshManager.ViewModels;

namespace SshManager.Views;

/// <summary>One panel of the file manager: folder listing, keyboard (F2…F8), context menu, drag and drop.</summary>
public partial class FilePanelView : UserControl
{
    private const string DragFormat = "SshManager.FileRows";
    private Point _dragStart;
    private bool _dragArmed;
    private string _typed = "";
    private DateTime _typedAt;

    public FilePanelView()
    {
        InitializeComponent();
        Grid.TextInput += OnTypeToFind;
    }

    public FilePanelModel Model { get; private set; } = null!;

    /// <summary>Copy / move the items to the other panel (target folder: null = the other panel's folder).</summary>
    public event Action<FilePanelView, IReadOnlyList<FileItem>, bool, string?>? CopyRequested;
    /// <summary>Windows files dropped on this panel: upload them into the folder.</summary>
    public event Action<IReadOnlyList<string>, string>? FilesDropped;
    public event Action<FileItem>? EditRequested;
    public event Action<string>? NewFileCreated;
    public event Action<string>? TerminalRequested;
    public event Action? SwitchRequested;
    public event Action<FilePanelView>? Activated;

    public void Attach(FilePanelModel model)
    {
        Model = model;
        DataContext = model;
        model.SelectRequested += Select;
        if (!model.IsRemote)
        {
            PermColumn.Visibility = Visibility.Collapsed;
            OwnerColumn.Visibility = Visibility.Collapsed;
            MiChmod.Visibility = Visibility.Collapsed;
            MiTerminal.Visibility = Visibility.Collapsed;
        }
        else MiExplorer.Visibility = Visibility.Collapsed;
        MiCopy.Header = L.Get(model.IsRemote ? "Files.Download" : "Files.Upload");
        MiMove.Header = L.Get(model.IsRemote ? "Files.MoveDownload" : "Files.MoveUpload");
    }

    public void SetActive(bool active) =>
        Frame.SetResourceReference(Border.BorderBrushProperty, active ? "AccentFillColorDefaultBrush" : "ControlFillColorTransparentBrush");

    public void FocusGrid()
    {
        Grid.Focus();
        if (Grid.SelectedItem is { } item && Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row) row.Focus();
        else if (Grid.Items.Count > 0) Select(null);
    }

    /// <summary>Selected rows without "..".</summary>
    public List<FileItem> SelectedItems() =>
        Grid.SelectedItems.OfType<FileRow>().Where(r => !r.IsParent).Select(r => r.Item).ToList();

    private FileRow? Current => Grid.CurrentItem as FileRow ?? Grid.SelectedItem as FileRow;

    private void Select(string? name)
    {
        if (Grid.Items.Count == 0) return;
        var row = name == null ? null : Model.Rows.FirstOrDefault(r => r.Name == name);
        row ??= Model.Rows.FirstOrDefault(r => !r.IsParent) ?? Model.Rows[0];
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
        Dispatcher.BeginInvoke(() =>
        {
            if (!Grid.IsKeyboardFocusWithin) return;
            Grid.CurrentCell = new DataGridCellInfo(row, Grid.Columns[0]);
            if (Grid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow r) r.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- navigation ----------

    private async void Open(FileRow? row)
    {
        if (row == null) return;
        if (row.IsParent)
        {
            await Model.UpAsync();
            return;
        }
        if (row.IsDirectory)
        {
            await Model.OpenAsync(row.Item.Path);
            return;
        }
        if (Model.IsRemote) EditRequested?.Invoke(row.Item);
        else
            try
            {
                Process.Start(new ProcessStartInfo(row.Item.Path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(Window.GetWindow(this), ex.Message, L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindRow(e.OriginalSource as DependencyObject) is not { } row) return;
        Open(row.Item as FileRow);
        e.Handled = true;
    }

    private async void OnPathKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await Model.OpenAsync(Model.PathEdit);
            FocusGrid();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Model.PathEdit = Model.Path;
            FocusGrid();
            e.Handled = true;
        }
    }

    private async void OnUp(object sender, RoutedEventArgs e) => await Model.UpAsync();
    private async void OnHome(object sender, RoutedEventArgs e) => await Model.OpenAsync(null);
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Model.RefreshAsync(Current?.Name);
    private async void OnRetry(object sender, RoutedEventArgs e) => await Model.RefreshAsync();

    private void OnGridFocus(object sender, KeyboardFocusChangedEventArgs e) => Activated?.Invoke(this);

    private async void OnGridKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        var none = Keyboard.Modifiers == ModifierKeys.None;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;
        switch (key)
        {
            case Key.Enter when none: Open(Current); break;
            case Key.Back when none: await Model.UpAsync(); break;
            case Key.Tab when none || shift: SwitchRequested?.Invoke(); break;
            case Key.F2 when none: Rename(); break;
            case Key.F4 when none: Edit(); break;
            case Key.F4 when shift: NewFile(); break;
            case Key.F5 when none: Copy(false); break;
            case Key.F6 when none: Copy(true); break;
            case Key.F7 when none: NewFolder(); break;
            case Key.F8 or Key.Delete when none: Delete(); break;
            case Key.R when ctrl: await Model.RefreshAsync(Current?.Name); break;
            case Key.H when ctrl: Model.ShowHidden = !Model.ShowHidden; break;
            case Key.L when ctrl:
                PathBox.Focus();
                PathBox.SelectAll();
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    /// <summary>Typing letters jumps to the first name that starts with them.</summary>
    private void OnTypeToFind(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _typed = DateTime.Now - _typedAt > TimeSpan.FromSeconds(1) ? e.Text : _typed + e.Text;
        _typedAt = DateTime.Now;
        var row = Model.Rows.FirstOrDefault(r => r.Name.StartsWith(_typed, StringComparison.OrdinalIgnoreCase));
        if (row == null) return;
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
        Grid.CurrentCell = new DataGridCellInfo(row, Grid.Columns[0]);
        e.Handled = true;
    }

    // ---------- sorting: ".." first, folders before files ----------

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
        if (CollectionViewSource.GetDefaultView(Grid.ItemsSource) is ListCollectionView view)
            view.CustomSort = new RowComparer(e.Column.SortMemberPath, dir == ListSortDirection.Descending);
    }

    private sealed class RowComparer(string member, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not FileRow a || y is not FileRow b) return 0;
            if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var r = member switch
            {
                "Item.Size" => a.Item.Size.CompareTo(b.Item.Size),
                "Item.Modified" => Nullable.Compare(a.Item.Modified, b.Item.Modified),
                _ => 0,
            };
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return descending ? -r : r;
        }
    }

    // ---------- operations ----------

    private Window? Owner => Window.GetWindow(this);

    public void Copy(bool move)
    {
        var items = SelectedItems();
        if (items.Count > 0) CopyRequested?.Invoke(this, items, move, null);
    }

    public void Edit()
    {
        if (Current is { IsDirectory: false } row)
        {
            if (Model.IsRemote) EditRequested?.Invoke(row.Item);
            else Open(row);
        }
    }

    private async void Rename()
    {
        if (Current is not { IsParent: false } row || Model.Fs is not { } fs) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.Rename"), L.Get("Files.NewName"), row.Name);
        if (name == null || name == row.Name) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dir = fs.Parent(row.Item.Path) ?? Model.Path;
        await Model.RunAsync(f => f.Rename(row.Item.Path, f.Combine(dir, name)), name);
    }

    public async void NewFolder()
    {
        if (Model.Fs is not { } fs || Model.Path.Length == 0) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.NewFolder"), L.Get("Files.FolderName"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Model.RunAsync(f => f.CreateDirectory(f.Combine(Model.Path, name)), name);
    }

    private async void NewFile()
    {
        if (Model.Fs is not { } fs || Model.Path.Length == 0) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.NewFile"), L.Get("Files.FileName"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = fs.Combine(Model.Path, name);
        var ok = await Model.RunAsync(f =>
        {
            if (f.Stat(path) != null) throw new InvalidOperationException(L.F("Files.Exists", name));
            f.Create(path).Dispose();
        }, name);
        if (ok) NewFileCreated?.Invoke(path);
    }

    public async void Delete()
    {
        var items = SelectedItems();
        if (items.Count == 0 || Model.Fs == null) return;
        var what = items.Count == 1 ? items[0].Name : L.F("Files.ItemsCount", items.Count);
        if (MessageBox.Show(Owner, L.F("Files.DeleteConfirm", what), L.Get("Files.Title"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var index = Grid.SelectedIndex;
        await Model.RunAsync(f =>
        {
            foreach (var i in items) f.Delete(i);
        });
        if (Model.Rows.Count > 0) Grid.SelectedIndex = Math.Min(index, Model.Rows.Count - 1);
    }

    private async void Chmod()
    {
        var items = SelectedItems();
        if (items.Count == 0 || Model.Fs is not SftpFileSystem) return;
        var current = items[0].Mode is { } m ? Convert.ToString(m, 8).PadLeft(3, '0') : "644";
        var text = InputDialog.Ask(Owner, L.Get("Files.Chmod"), L.F("Files.ChmodPrompt", items.Count == 1 ? items[0].Name : L.F("Files.ItemsCount", items.Count)), current);
        if (text == null) return;
        if (FileSystems.ParseMode(text) is not { } mode)
        {
            MessageBox.Show(Owner, L.Get("Files.BadMode"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Model.RunAsync(f =>
        {
            foreach (var i in items) ((SftpFileSystem)f).ChangeMode(i.Path, mode);
        }, items[0].Name);
    }

    // ---------- context menu ----------

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject)?.Item as FileRow;
        if (row != null && !Grid.SelectedItems.Contains(row)) Grid.SelectedItem = row;
        var hasItem = SelectedItems().Count > 0;
        var file = Current is { IsDirectory: false };
        MiOpen.IsEnabled = Current != null;
        MiEdit.IsEnabled = file;
        MiCopy.IsEnabled = MiMove.IsEnabled = MiDelete.IsEnabled = MiChmod.IsEnabled = MiCopyPath.IsEnabled = hasItem;
        MiRename.IsEnabled = Current is { IsParent: false };
        MiNewFile.IsEnabled = Model.Path.Length > 0;
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e) => Open(Current);
    private void OnMenuEdit(object sender, RoutedEventArgs e) => Edit();
    private void OnMenuCopy(object sender, RoutedEventArgs e) => Copy(false);
    private void OnMenuMove(object sender, RoutedEventArgs e) => Copy(true);
    private void OnMenuRename(object sender, RoutedEventArgs e) => Rename();
    private void OnMenuChmod(object sender, RoutedEventArgs e) => Chmod();
    private void OnMenuDelete(object sender, RoutedEventArgs e) => Delete();
    private void OnMenuNewFolder(object sender, RoutedEventArgs e) => NewFolder();
    private void OnMenuNewFile(object sender, RoutedEventArgs e) => NewFile();
    private void OnMenuTerminal(object sender, RoutedEventArgs e) => TerminalRequested?.Invoke(Current is { IsDirectory: true, IsParent: false } d ? d.Item.Path : Model.Path);

    private void OnMenuCopyPath(object sender, RoutedEventArgs e)
    {
        var paths = SelectedItems().Select(i => i.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private void OnMenuExplorer(object sender, RoutedEventArgs e)
    {
        var args = Current is { IsParent: false } row ? $"/select,\"{row.Item.Path}\"" : $"\"{Model.Path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    // ---------- drag and drop ----------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = FindRow(e.OriginalSource as DependencyObject) is { } row && row.IsSelected && e.ClickCount == 1;
        _dragStart = e.GetPosition(this);
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var items = SelectedItems();
        if (items.Count == 0) return;
        var data = new DataObject();
        data.SetData(DragFormat, new DragPayload(this, items));
        // local files can also go to Explorer or other apps
        if (!Model.IsRemote) data.SetFileDropList([.. items.Select(i => i.Path)]);
        DragDrop.DoDragDrop(Grid, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private sealed record DragPayload(FilePanelView Source, List<FileItem> Items);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DragFormat) is DragPayload p && p.Source != this)
            e.Effects = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
        else if (Model.IsRemote && e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // onto a folder row: into that folder
        var target = FindRow(e.OriginalSource as DependencyObject)?.Item is FileRow { IsDirectory: true, IsParent: false } r ? r.Item.Path : Model.Path;
        if (e.Data.GetData(DragFormat) is DragPayload p && p.Source != this)
            p.Source.CopyRequested?.Invoke(p.Source, p.Items, (e.KeyStates & DragDropKeyStates.ShiftKey) != 0, target);
        else if (Model.IsRemote && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            FilesDropped?.Invoke(files, target);
        e.Handled = true;
    }

    private static DataGridRow? FindRow(DependencyObject? d)
    {
        while (d != null && d is not DataGridRow)
        {
            if (d is System.Windows.Controls.Primitives.DataGridColumnHeader or System.Windows.Controls.Primitives.ScrollBar) return null;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return d as DataGridRow;
    }
}
