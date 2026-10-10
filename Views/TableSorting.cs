using System.Collections;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
namespace EveCommandCenter.Views;

internal static class TableSorting
{
    public static void Attach(DataGrid grid)
    {
        grid.CanUserSortColumns = true;
        var descriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
        bool observing = false;
        EventHandler changed = (_, _) => Restore(grid);
        grid.Loaded += (_, _) => { if (!observing) { descriptor.AddValueChanged(grid, changed); observing = true; } Restore(grid); };
        grid.Unloaded += (_, _) => { if (observing) { descriptor.RemoveValueChanged(grid, changed); observing = false; } };
        grid.Sorting += (_, e) =>
        {
            var path = e.Column.SortMemberPath;
            if (string.IsNullOrWhiteSpace(path) && e.Column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding }) path = binding.Path?.Path;
            if (string.IsNullOrWhiteSpace(path)) return;
            e.Handled = true;
            var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            foreach (var column in grid.Columns) column.SortDirection = null;
            if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
            { view.CustomSort = new Values(path, direction); e.Column.SortDirection = direction; }
        };
    }
    private static void Restore(DataGrid grid)
    {
        var column = grid.Columns.FirstOrDefault(c => c.SortDirection.HasValue);
        if (column == null) return;
        string? path = column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(path) && column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding }) path = binding.Path?.Path;
        if (!string.IsNullOrWhiteSpace(path) && CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
            view.CustomSort = new Values(path, column.SortDirection!.Value);
    }
    internal sealed class Values(string path, ListSortDirection direction) : IComparer
    {
        private object? Read(object? row) { foreach (var key in path.Split('.')) row = row?.GetType().GetProperty(key)?.GetValue(row); return row; }
        public int Compare(object? x, object? y)
        {
            var a = Read(x); var b = Read(y);
            if (a == null || b == null) return a == null ? b == null ? 0 : 1 : -1;
            int result = a is string text ? StringComparer.CurrentCultureIgnoreCase.Compare(text, b.ToString()) : a is IComparable comparable ? comparable.CompareTo(b) : 0;
            return direction == ListSortDirection.Descending ? -result : result;
        }
    }
}
