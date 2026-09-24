using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SshManager.Views.Controls;

/// <summary>
/// A TreeView whose rows are laid out in resizable columns (GridViewRowPresenter), with a header row.
/// The last column stretches to fill the remaining width.
/// </summary>
public sealed class TreeListView : TreeView
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(GridViewColumnCollection), typeof(TreeListView),
        new PropertyMetadata(null, (d, _) => ((TreeListView)d).HookColumns()));

    private bool _fitting;

    public TreeListView()
    {
        SetValue(ColumnsProperty, new GridViewColumnCollection());
        SizeChanged += (_, _) => FitLastColumn();
    }

    public GridViewColumnCollection Columns
    {
        get => (GridViewColumnCollection)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override DependencyObject GetContainerForItemOverride() => new TreeListViewItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is TreeListViewItem;

    private void HookColumns()
    {
        if (Columns == null) return;
        Columns.CollectionChanged += (_, e) =>
        {
            foreach (GridViewColumn c in e.NewItems ?? Array.Empty<GridViewColumn>())
                DependencyPropertyDescriptor.FromProperty(GridViewColumn.WidthProperty, typeof(GridViewColumn))
                    .AddValueChanged(c, (_, _) => FitLastColumn());
            FitLastColumn();
        };
    }

    /// <summary>Width available for columns (minus borders and the vertical scrollbar).</summary>
    private double AvailableWidth => ActualWidth - 16 - SystemParameters.VerticalScrollBarWidth;

    public void FitLastColumn()
    {
        if (_fitting || Columns == null || Columns.Count == 0 || ActualWidth <= 0) return;
        _fitting = true;
        try
        {
            var others = Columns.Take(Columns.Count - 1).Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
            Columns[^1].Width = Math.Max(120, AvailableWidth - others);
        }
        finally
        {
            _fitting = false;
        }
    }
}

public sealed class TreeListViewItem : TreeViewItem
{
    /// <summary>Row types whose double click means "open", not "expand".</summary>
    public static Func<object?, bool>? DoubleClickOpens { get; set; }

    protected override DependencyObject GetContainerForItemOverride() => new TreeListViewItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is TreeListViewItem;

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && DoubleClickOpens?.Invoke(DataContext) == true)
        {
            Focus();
            IsSelected = true;
            e.Handled = true;
            return;
        }
        base.OnMouseLeftButtonDown(e);
    }
}
