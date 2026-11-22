using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace JournalDesk;

public partial class MainWindow
{
    private DataGridColumn? historySortColumn;
    private ListSortDirection historyDirection;
    private CancellationTokenSource? historySortWork;
    private (IReadOnlyList<Activity> Rows, JournalPathSource? Source, Dictionary<Activity, HistorySortValues> Values)? cachedHistoryValues;

    private readonly record struct HistorySortValues((string First, string Second) Path, (string First, string Second) Basis);

    private async void SortHistory(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = SortCycle.Next(e.Column == historySortColumn, historyDirection == ListSortDirection.Descending);
        historyDirection = direction == true ? ListSortDirection.Descending : ListSortDirection.Ascending;
        historySortColumn = direction.HasValue ? e.Column : null;
        await ApplyHistoryOrder();
    }

    private async Task ApplyHistoryOrder()
    {
        historySortWork?.Cancel();
        if (Events.ItemsSource is not IReadOnlyList<Activity> rows ||
            CollectionViewSource.GetDefaultView(rows) is not ListCollectionView view)
        {
            cachedHistoryValues = null;
            return;
        }
        var column = historySortColumn;
        var direction = historyDirection;
        var file = current;
        var index = journal;
        var linked = mft;
        bool fullPaths = JournalPathPreference.Current.Enabled;
        var context = historyPaths;
        var prior = cachedHistoryValues;
        if (prior is { } cache && (!ReferenceEquals(cache.Rows, rows) || !ReferenceEquals(cache.Source, context)))
            cachedHistoryValues = prior = null;
        using var work = new CancellationTokenSource();
        historySortWork = work;
        try
        {
            System.Collections.IComparer? order = null;
            if (column != null && file != null && index != null)
            {
                string member = column.SortMemberPath;
                Dictionary<Activity, HistorySortValues>? resolved = fullPaths ? prior?.Values : null;
                var ranks = await Task.Run(() =>
                {
                    HistorySortValues Values(Activity activity)
                    {
                        var first = activity.Records[0];
                        var next = activity.PairedName;
                        string initial = next == null ? activity.Description : first.Name;
                        if (!fullPaths)
                            return new((initial, next?.Name ?? ""), ("", ""));
                        var before = index.ResolveListValues(first, file.Id, initial, linked?.Paths, linked?.PairWarning);
                        (string Path, string Source) after = next == null ? ("", "") :
                            index.ResolveListValues(next, file.Id, next.Name, linked?.Paths, linked?.PairWarning);
                        return new((before.Path, after.Path), (before.Source, after.Source));
                    }

                    if (fullPaths && (member is "Summary" or "Basis") && resolved == null)
                    {
                        resolved = new Dictionary<Activity, HistorySortValues>(rows.Count);
                        foreach (var row in rows)
                        {
                            work.Token.ThrowIfCancellationRequested();
                            resolved.Add(row, Values(row));
                        }
                    }

                    var checkedRows = rows.Select(a => { work.Token.ThrowIfCancellationRequested(); return a; });
                    IOrderedEnumerable<Activity> Sort<T>(Func<Activity, T> get) => direction == ListSortDirection.Ascending
                        ? checkedRows.OrderBy(get) : checkedRows.OrderByDescending(get);
                    var sorted = member switch
                    {
                        "Time" => Sort(a => a.Records[0].Timestamp),
                        "Count" => Sort(a => a.Records.Count),
                        "Label" => Sort(a => a.Label),
                        "Summary" => Sort(a => (resolved?.GetValueOrDefault(a) ?? Values(a)).Path),
                        "Basis" => Sort(a => (resolved?.GetValueOrDefault(a) ?? Values(a)).Basis),
                        _ => throw new ArgumentOutOfRangeException(nameof(member))
                    };
                    return sorted.Select((a, i) => (a, i)).ToDictionary(row => row.a, row => row.i);
                }, work.Token);
                work.Token.ThrowIfCancellationRequested();
                if (resolved != null && fullPaths && ReferenceEquals(Events.ItemsSource, rows) && ReferenceEquals(historyPaths, context))
                    cachedHistoryValues = (rows, context, resolved);
                order = Comparer<Activity>.Create((a, b) => ranks[a].CompareTo(ranks[b]));
            }
            if (closed || !ReferenceEquals(Events.ItemsSource, rows))
                return;
            var selected = Events.SelectedItem;
            restoringActivity = true;
            try
            {
                view.CustomSort = order;
                Events.SelectedItem = selected;
                if (selected != null)
                    KeepActivityVisible(selected);
            }
            finally { restoringActivity = false; }
            foreach (var item in Events.Columns)
                item.SortDirection = item == column ? direction : null;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception ex) { Report(ex); }
        finally { if (ReferenceEquals(historySortWork, work)) historySortWork = null; }
    }
}
