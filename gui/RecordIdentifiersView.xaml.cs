using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

internal sealed record RecordIdChoice(string Value, bool IsParent);

public partial class RecordIdentifiersView : UserControl
{
    internal event Action<RecordIdChoice>? SearchRequested;

    public RecordIdentifiersView() => InitializeComponent();

    internal void SetIds(IReadOnlyList<string> files, IReadOnlyList<string> parents)
    {
        FileIds.ItemsSource = files.Select(value => new RecordIdChoice(value, false));
        ParentIds.ItemsSource = parents.Select(value => new RecordIdChoice(value, true));
        MissingFile.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MissingParent.Visibility = parents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RecordIdChoice choice })
            SearchRequested?.Invoke(choice);
    }
}
