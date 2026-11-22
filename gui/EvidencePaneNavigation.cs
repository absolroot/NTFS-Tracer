using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JournalDesk;

internal static class EvidencePaneNavigation
{
    internal static bool MoveFocus(KeyEventArgs e, DataGrid table, FrameworkElement inspector, TabControl tabs)
    {
        if (e.Key != Key.F6 || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)
            || !inspector.IsVisible || !table.IsKeyboardFocusWithin && !inspector.IsKeyboardFocusWithin
            || table.SelectedItem == null || tabs.SelectedItem is not TabItem tab)
            return false;
        e.Handled = true;
        if (inspector.IsKeyboardFocusWithin)
            FocusRow(table);
        else
            tab.Focus();
        return true;
    }

    internal static void FocusRow(DataGrid table)
    {
        if (!table.HasItems || !table.IsVisible)
            return;
        var row = table.SelectedItem ?? table.Items[0];
        var column = table.CurrentColumn is { Visibility: Visibility.Visible } current
            ? current : table.Columns.First(c => c.Visibility == Visibility.Visible);
        table.SelectedItem = row;
        table.CurrentCell = new DataGridCellInfo(row, column);
        table.ScrollIntoView(row, column);
        table.UpdateLayout();
        if (column.GetCellContent(row)?.Parent is DataGridCell cell)
            cell.Focus();
        else
            table.Focus();
    }
}
