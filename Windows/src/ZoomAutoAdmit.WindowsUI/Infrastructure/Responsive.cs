using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ZoomAutoAdmit.WindowsUI.Infrastructure;

/// <summary>
/// Makes fixed layouts follow the window's width.
///
///   MinItemWidth (UniformGrid, set for every one by Controls.xaml): the grid keeps the columns it
///   was written with while each is at least this wide, and drops to fewer columns when it is not.
///
///   StackBelow (Grid): below this width the grid's columns are laid one under the other, in
///   reading order; above it the grid is exactly as written.
/// </summary>
public static class Responsive
{
    // ------------------------------------------------------------------ UniformGrid
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.RegisterAttached(
        "MinItemWidth", typeof(double), typeof(Responsive), new PropertyMetadata(0d, OnMinItemWidthChanged));
    public static double GetMinItemWidth(DependencyObject o) => (double)o.GetValue(MinItemWidthProperty);
    public static void SetMinItemWidth(DependencyObject o, double value) => o.SetValue(MinItemWidthProperty, value);

    private static readonly DependencyProperty WrittenColumnsProperty = DependencyProperty.RegisterAttached(
        "WrittenColumns", typeof(int), typeof(Responsive), new PropertyMetadata(-1));

    private static void OnMinItemWidthChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not UniformGrid grid) return;
        grid.SizeChanged -= OnUniformGridSized;
        grid.Loaded -= OnUniformGridLoaded;
        if ((double)e.NewValue <= 0) return;
        grid.SizeChanged += OnUniformGridSized;
        grid.Loaded += OnUniformGridLoaded;
    }

    private static void OnUniformGridLoaded(object sender, RoutedEventArgs e) => Fit((UniformGrid)sender);
    private static void OnUniformGridSized(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) Fit((UniformGrid)sender); }

    private static void Fit(UniformGrid grid)
    {
        int written = (int)grid.GetValue(WrittenColumnsProperty);
        if (written < 0) { written = grid.Columns; grid.SetValue(WrittenColumnsProperty, written); }
        // Only grids written with a column count and no fixed row count can reflow.
        if (written <= 1 || grid.Rows > 0 || grid.ActualWidth <= 0) return;
        int fits = Math.Max(1, (int)(grid.ActualWidth / GetMinItemWidth(grid)));
        int columns = Math.Min(written, fits);
        // 4 columns that do not fit become 2 rows of 2 rather than 3 + 1.
        if (columns < written && written % 2 == 0 && columns > 1 && written % columns != 0) columns = Math.Max(1, columns - 1);
        if (grid.Columns != columns) grid.Columns = columns;
    }

    // ------------------------------------------------------------------ Grid
    public static readonly DependencyProperty StackBelowProperty = DependencyProperty.RegisterAttached(
        "StackBelow", typeof(double), typeof(Responsive), new PropertyMetadata(0d, OnStackBelowChanged));
    public static double GetStackBelow(DependencyObject o) => (double)o.GetValue(StackBelowProperty);
    public static void SetStackBelow(DependencyObject o, double value) => o.SetValue(StackBelowProperty, value);

    private sealed record Written(GridLength[] Columns, GridLength[] Rows, Dictionary<UIElement, (int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin)> Cells);
    private static readonly DependencyProperty WrittenLayoutProperty = DependencyProperty.RegisterAttached(
        "WrittenLayout", typeof(Written), typeof(Responsive), new PropertyMetadata(null));
    private static readonly DependencyProperty IsStackedProperty = DependencyProperty.RegisterAttached(
        "IsStacked", typeof(bool), typeof(Responsive), new PropertyMetadata(false));

    private static void OnStackBelowChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not Grid grid) return;
        grid.SizeChanged -= OnGridSized;
        if ((double)e.NewValue > 0) grid.SizeChanged += OnGridSized;
    }

    private static void OnGridSized(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var grid = (Grid)sender;
        bool stack = grid.ActualWidth > 0 && grid.ActualWidth < GetStackBelow(grid);
        if (stack == (bool)grid.GetValue(IsStackedProperty)) return;
        if (stack) Stack(grid); else Restore(grid);
    }

    private static void Stack(Grid grid)
    {
        int columns = Math.Max(1, grid.ColumnDefinitions.Count);
        if (columns < 2) return;
        var written = (Written?)grid.GetValue(WrittenLayoutProperty);
        if (written == null)
        {
            written = new(grid.ColumnDefinitions.Select(c => c.Width).ToArray(),
                grid.RowDefinitions.Select(r => r.Height).ToArray(), []);
            foreach (UIElement child in grid.Children)
                written.Cells[child] = (Grid.GetRow(child), Grid.GetColumn(child), Grid.GetRowSpan(child), Grid.GetColumnSpan(child),
                    child is FrameworkElement f ? f.Margin : default);
            grid.SetValue(WrittenLayoutProperty, written);
        }
        int rows = Math.Max(1, written.Rows.Length);
        grid.RowDefinitions.Clear();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                // Each column laid below keeps the height its row was written with, so two panes
                // that filled a row share it once stacked. A cell nobody sits in (a spacer column)
                // takes no height at all.
                bool used = written.Cells.Values.Any(cell => cell.Row <= r && r < cell.Row + cell.RowSpan && (cell.ColumnSpan >= columns ? c == 0 : cell.Column == c));
                var height = written.Rows.Length == 0 ? new GridLength(1, GridUnitType.Star) : written.Rows[r];
                grid.RowDefinitions.Add(new RowDefinition { Height = used ? height : new GridLength(0) });
            }
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions[c].Width = c == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        foreach (UIElement child in grid.Children)
        {
            if (!written.Cells.TryGetValue(child, out var cell)) continue;
            bool full = cell.ColumnSpan >= columns;
            Grid.SetRow(child, Math.Min(grid.RowDefinitions.Count - 1, cell.Row * columns + (full ? 0 : cell.Column)));
            Grid.SetColumn(child, 0);
            Grid.SetColumnSpan(child, 1);
            Grid.SetRowSpan(child, full ? Math.Max(1, cell.RowSpan * columns) : 1);
            if (child is FrameworkElement f && !full)
            {
                double gap = Math.Max(f.Margin.Left, f.Margin.Right);
                f.Margin = new Thickness(0, cell.Column > 0 ? Math.Max(f.Margin.Top, Math.Max(gap, 12)) : f.Margin.Top, 0, f.Margin.Bottom);
            }
        }
        grid.SetValue(IsStackedProperty, true);
    }

    private static void Restore(Grid grid)
    {
        if (grid.GetValue(WrittenLayoutProperty) is not Written written) { grid.SetValue(IsStackedProperty, false); return; }
        for (int c = 0; c < grid.ColumnDefinitions.Count && c < written.Columns.Length; c++) grid.ColumnDefinitions[c].Width = written.Columns[c];
        grid.RowDefinitions.Clear();
        foreach (var height in written.Rows) grid.RowDefinitions.Add(new RowDefinition { Height = height });
        foreach (var (child, cell) in written.Cells)
        {
            Grid.SetRow(child, cell.Row); Grid.SetColumn(child, cell.Column);
            Grid.SetRowSpan(child, cell.RowSpan); Grid.SetColumnSpan(child, cell.ColumnSpan);
            if (child is FrameworkElement f) f.Margin = cell.Margin;
        }
        grid.SetValue(IsStackedProperty, false);
    }
}
