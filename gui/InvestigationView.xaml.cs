using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace JournalDesk;

public partial class InvestigationView : UserControl
{
    private const int PageSize = 100;
    private readonly Journal journal;
    private readonly string source;
    private readonly Action<byte[], long, bool> navigate;
    private CancellationTokenSource? operation;
    private InvestigationQuery? results;
    private long start;
    private LeadOrder order;
    private LeadKind? displayedKind;
    private bool explicitSort;
    private bool closed, updatingOptions;
    private bool started;

    private sealed record Option(LeadRule Rule, long? Count)
    {
        public string Display => Count.HasValue ? $"{Rule.Label}  ({Count:N0})" : Rule.Label;
    }
    private Option? Selected => Rules.SelectedItem as Option;

    public InvestigationView(Journal journal, string originalPath, Action<byte[], long, bool> navigate)
    {
        InitializeComponent();
        UpdateHexLabel();
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, (_, _) =>
        {
            Leads.Items.Refresh();
            Evidence.Items.Refresh();
        }, nameof(DisplayTimeZone.Code));
        this.journal = journal;
        source = originalPath;
        this.navigate = navigate;
        SourceLabel.Text = Path.GetFileName(source);
        SourceLabel.ToolTip = source;
        SetOptions();
        Loaded += async (_, _) =>
        {
            if (started)
                return;
            started = true;
            await Scan();
        };
    }

    internal void CloseWithOwner()
    {
        closed = true;
        operation?.Cancel();
        if (operation == null)
            results?.Dispose();
    }

    internal void FocusEvidence()
    {
        if (Evidence.SelectedItem is LeadEvidence selected)
        {
            Evidence.ScrollIntoView(selected);
            Evidence.Focus();
        }
    }

    internal void RefreshLanguage()
    {
        UpdateHexLabel();
        SetOptions();
        Leads.Items.Refresh();
        Evidence.Items.Refresh();
        UpdateEvidenceTitle(Leads.SelectedItem as InvestigationLead);
        UpdatePageCounter();
        if (results != null && operation == null)
            ShowScanSummary();
    }

    private void UpdateHexLabel() => GoHex.Content = L.T("원본 Hex 열기", "Open original Hex");

    private void SetOptions(bool selectFirstMatch = false)
    {
        var previous = Selected?.Rule.Kind ?? LeadKind.DocumentDelete;
        var options = LeadRule.All.Select(r => new Option(r, results?.Count(r.Kind))).ToArray();
        if (selectFirstMatch && options[(int)previous].Count == 0 &&
            options.FirstOrDefault(option => option.Count > 0) is { } firstMatch)
            previous = firstMatch.Rule.Kind;
        updatingOptions = true;
        try
        {
            Rules.ItemsSource = options;
            Rules.SelectedIndex = (int)previous;
            UpdateRule(options[(int)previous].Rule);
        }
        finally { updatingOptions = false; }
    }

    private void Controls()
    {
        if (closed)
            return;
        bool idle = operation == null;
        bool available = idle && results != null;
        long count = Selected?.Count ?? 0;
        Rules.IsEnabled = available;
        Leads.IsEnabled = Evidence.IsEnabled = available;
        Retry.Visibility = idle && results == null ? Visibility.Visible : Visibility.Collapsed;
        Stop.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        Prev.IsEnabled = available && start > 0;
        Next.IsEnabled = available && start + PageSize < count;
        ExportButton.IsEnabled = available && count > 0;
        GoHex.IsEnabled = Go.IsEnabled = available && Evidence.SelectedItem is LeadEvidence;
    }

    private async Task Work(Func<CancellationToken, Task> action)
    {
        if (operation != null || closed)
            return;
        using var running = new CancellationTokenSource();
        operation = running;
        Controls();
        try
        {
            await action(running.Token);
        }
        catch (Exception) when (running.IsCancellationRequested) { Status.Text = L.T("작업을 취소했습니다.", "Operation cancelled."); }
        catch (Exception e) { Status.Text = L.T("작업을 완료할 수 없습니다. ", "Could not complete the operation. ") + e.Message; }
        finally
        {
            operation = null;
            if (closed)
                results?.Dispose();
            else
                Controls();
        }
    }

    private async Task Scan()
    {
        await Work(async token =>
        {
            Status.Text = L.T("원본 순서로 저널 레코드를 검사하고 있습니다.", "Scanning journal records in source order.");
            Empty.Text = L.T("조사 후보를 찾고 있습니다.", "Finding investigation candidates.");
            var progress = new Progress<long>(n =>
            {
                if (!closed && !token.IsCancellationRequested && results == null)
                    Status.Text = L.T("저널 레코드 {0:N0}건을 검사했습니다.", "Scanned {0:N0} journal records.", n);
            });
            try
            {
                results = await Task.Run(() => InvestigationQuery.Scan(journal, token, progress), token);
            }
            finally { Empty.Text = results == null ? L.T("후보를 확인하려면 다시 검색하세요.", "Run the search again to see candidates.") : ""; }
            if (closed)
                return;
            SetOptions(selectFirstMatch: true);
            ShowScanSummary();
            await ShowPage(0, token);
        });
    }

    private async Task ShowPage(long offset, CancellationToken token, LeadOrder? newOrder = null,
        long? candidateNumber = null, long? evidencePosition = null)
    {
        if (Selected is not Option option || results == null)
            return;
        var requestedOrder = newOrder ?? order;
        var page = await Task.Run(() =>
        {
            long first = candidateNumber is long id
                ? results.Locate(option.Rule.Kind, id, requestedOrder, token) / PageSize * PageSize : offset;
            return (first, rows: results.Page(option.Rule.Kind, first, PageSize, token, requestedOrder));
        }, token);
        token.ThrowIfCancellationRequested();
        var rows = page.rows;
        start = page.first;
        order = requestedOrder;
        Leads.ItemsSource = rows;
        Leads.SelectedItem = rows.FirstOrDefault(r => r.Number == candidateNumber) ?? rows.FirstOrDefault();
        if (Leads.SelectedItem is InvestigationLead selected)
        {
            Leads.ScrollIntoView(selected);
            if (evidencePosition is long pos && selected.Evidence.FirstOrDefault(x => x.Record.Position == pos) is LeadEvidence item)
                Evidence.SelectedItem = item;
        }
        ShowSortDirection();
        UpdatePageCounter();
    }

    private void ShowScanSummary() =>
        Status.Text = L.T("USN 기록 {0:N0}건 검사 완료", "Scanned {0:N0} USN records", results!.Total);

    private void UpdatePageCounter()
    {
        if (results == null || Selected is not Option option)
            return;
        int visible = Leads.Items.Count;
        Empty.Text = visible == 0 ? L.T("이 유형에 해당하는 후보가 없습니다.", "No leads match this type.") : "";
        PageCounter.Text = visible == 0 ? L.T("0건", "0 leads") :
            L.T("{0:N0}–{1:N0} / {2:N0}건", "{0:N0}–{1:N0} of {2:N0}", start + 1, start + visible, option.Count);
    }

    private void ShowSortDirection()
    {
        foreach (var column in Leads.Columns)
            column.SortDirection = explicitSort && column.SortMemberPath == order.Field.ToString()
                ? order.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
    }

    private async void SortLeads(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (operation != null || results == null || !Enum.TryParse<LeadSortField>(e.Column.SortMemberPath, out var field))
            return;
        var direction = SortCycle.Next(explicitSort && order.Field == field, order.Descending);
        var nextOrder = direction.HasValue ? new LeadOrder(field, direction.Value) : default;
        long? selected = (Leads.SelectedItem as InvestigationLead)?.Number;
        long? evidence = (Evidence.SelectedItem as LeadEvidence)?.Record.Position;
        await Work(async token =>
        {
            await ShowPage(0, token, nextOrder, selected, evidence);
            explicitSort = direction.HasValue;
        });
        ShowSortDirection();
    }

    private async void RuleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingOptions || Selected is not Option option)
            return;
        UpdateRule(option.Rule);
        Leads.ItemsSource = null;
        PageCounter.Text = "";
        start = 0;
        order = default;
        explicitSort = false;
        ShowSortDirection();
        if (results != null && operation == null)
            await Work(token => ShowPage(0, token));
    }

    private void UpdateRule(LeadRule rule)
    {
        Criterion.Content = rule.Criterion;
        ObservationColumn.Header = rule.Kind switch
        {
            LeadKind.DocumentDelete => L.T("확장자", "Extension"),
            LeadKind.ClockBack => L.T("시각 차이", "Time difference"),
            _ => L.T("후보로 잡힌 이유", "Why this was flagged")
        };
        if (displayedKind == rule.Kind)
            return;
        displayedKind = rule.Kind;
        ObservationColumn.Width = rule.Kind switch
        {
            LeadKind.DocumentDelete => new DataGridLength(150),
            LeadKind.ClockBack => new DataGridLength(185),
            _ => new DataGridLength(1.2, DataGridLengthUnitType.Star)
        };
    }

    private void LeadChanged(object sender, SelectionChangedEventArgs e)
    {
        var lead = Leads.SelectedItem as InvestigationLead;
        Evidence.ItemsSource = lead?.Evidence;
        Evidence.SelectedIndex = (lead?.Evidence.Count ?? 0) - 1;
        UpdateEvidenceTitle(lead);
        Controls();
    }

    private void UpdateEvidenceTitle(InvestigationLead? lead) =>
        EvidenceTitle.Text = lead == null ? L.T("근거 레코드", "Evidence records") : L.T("후보 {0:N0}의 근거 레코드 {1}건", lead.Evidence.Count == 1 ? "Candidate {0:N0} · {1} evidence record" : "Candidate {0:N0} · {1} evidence records", lead.Number, lead.Evidence.Count);

    private void OpenFromGrid(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || operation != null)
            return;
        var record = (sender as DataGrid)?.SelectedItem switch
        {
            InvestigationLead lead => lead.Anchor,
            LeadEvidence evidence => evidence.Record,
            _ => null
        };
        if (record != null)
        {
            e.Handled = true;
            Jump(record, openHex: ReferenceEquals(sender, Evidence));
        }
    }
    private void EvidenceChanged(object sender, SelectionChangedEventArgs e) => Controls();
    private void Jump(LeadObservation row, bool openHex = false)
    {
        navigate(row.FileId, row.Position, openHex);
    }
    private void GoClick(object sender, RoutedEventArgs e)
    {
        if (Evidence.SelectedItem is LeadEvidence a)
            Jump(a.Record);
    }
    private void GoHexClick(object sender, RoutedEventArgs e)
    {
        if (Evidence.SelectedItem is LeadEvidence item)
            Jump(item.Record, openHex: true);
    }

    private void LeadDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (operation == null && e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(Leads, element) is DataGridRow { Item: InvestigationLead lead })
            Jump(lead.Anchor);
    }
    private void EvidenceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (operation == null && e.OriginalSource is DependencyObject element && ItemsControl.ContainerFromElement(Evidence, element) is DataGridRow { Item: LeadEvidence item })
            Jump(item.Record, openHex: true);
    }
    private async void PrevClick(object sender, RoutedEventArgs e) => await Work(token => ShowPage(Math.Max(0, start - PageSize), token));
    private async void NextClick(object sender, RoutedEventArgs e) => await Work(token => ShowPage(start + PageSize, token));
    private void StopClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private async void RetryClick(object sender, RoutedEventArgs e) => await Scan();

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not Option option || results == null)
            return;
        var dialog = new SaveFileDialog
        {
            InitialDirectory = DeskSettings.InitialExportDirectory,
            Title = L.T("선택한 유형의 모든 후보와 근거 레코드 저장", "Save all candidates and evidence records for the selected type"),
            DefaultExt = ".csv",
            Filter = L.T("CSV 파일 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"),
            FileName = option.Rule.Label + ".csv",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        await Work(async token =>
        {
            Status.Text = L.T("‘{0}’의 모든 후보와 근거 레코드를 저장하고 있습니다.", "Saving all candidates and evidence records for “{0}”.", option.Rule.Label);
            await Task.Run(() => results.Export(option.Rule, source, dialog.FileName, token, order), token);
            if (!closed)
                SavedOutput.Show(Status, dialog.FileName, L.T("후보 {0:N0}건의 근거 저장", "Evidence for {0:N0} candidates saved", option.Count));
        });
    }
}
