using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JournalDesk;

public partial class MainWindow : Window
{
    private static string Cache => DeskSettings.IndexStore;
    private const int PageSize = 100;
    private FileStream? source;
    private Journal? journal;
    private MftAttachment? mft;
    private JournalPathSource? historyPaths;
    private FileRow? current;
    private List<Entry> history = [];
    private List<Activity> activities = [];
    private string digest = "", summaryText = "";
    private List<EvidenceField>? selectedMftEvidence;
    private int page, queryVersion, fileVersion;
    private int listedVersion = -1, runningFileQuery = -1;
    private bool historyRefreshPending, historyPathSortPending;
    private sealed record FileSearchBookmark(byte[] Id, long? Position, int Filter, bool ShowInspector, int Anchor, int Caret);
    private FileSearchBookmark? emptySearchBookmark;
    private bool composingSearch, focusSearchResult;
    private bool busy, closed, readingJournalInfo;
    internal bool IsPreparingSources => busy;
    private CancellationTokenSource? historyExport;
    private TabItem? previousResultsTab;
    private IntakeWindow? startupSettings;
    private bool startupSettingsShown;
    private CancellationTokenSource? import;
    private Journal? globalJournal;
    private PathResolver? globalPaths;
    private int jumpVersion;
    private bool updatingFileList;
    private bool restoringActivity;
    private readonly DispatcherTimer typing = new() { Interval = TimeSpan.FromMilliseconds(200) };
    public MainWindow()
    {
        InitializeComponent();
        PropertyChangedEventManager.AddHandler(DisplayTimeZone.Current, (_, _) =>
        {
            foreach (var activity in activities)
                activity.RefreshClock();
            if (ProofPicker.SelectedItem is Entry record)
                OverviewPanel.SetTime(record, DisplayTimeZone.Current.Zone);
        }, nameof(DisplayTimeZone.Code));
        WindowLayoutPreference.Attach(this);
        Setup.Host = this;
        Setup.AnalysisRequested += AnalyzeFromSettings;
        SettingsTab.IsSelected = true;
        PropertyChangedEventManager.AddHandler(JournalPathPreference.Current, HistoryPathModeChanged, nameof(JournalPathPreference.Enabled));
        HistoryPathModeChanged(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(JournalPathPreference.Enabled)));
        HistoryNameColumn.CopyingCellClipboardContent += (_, e) =>
        {
            if (e.Item is not Activity a || !JournalPathPreference.Current.Enabled || historyPaths == null)
                return;
            string Value(Entry record) => historyPaths.CachedPath(record.Position) ?? record.RecordedName;
            if (a.PairedName is { } after)
                e.Content = Value(a.Records[0]) + " → " + Value(after);
            else if (historyPaths.CachedPath(a.Records[0].Position) is { } path)
                e.Content = path;
        };
        HistoryPathSourceColumn.CopyingCellClipboardContent += (_, e) =>
        {
            if (e.Item is not Activity a || historyPaths == null)
                return;
            string before = historyPaths.CachedSource(a.Records[0].Position) ?? "";
            e.Content = a.PairedName is { } after ? before + " → " + (historyPaths.CachedSource(after.Position) ?? "") : before;
        };
        ProofSplitter.DragCompleted += (_, e) =>
        {
            if (!e.Canceled && EvidenceRow.Height.IsAbsolute)
                preferredEvidenceHeight = EvidenceRow.Height.Value;
        };
        Inspector.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
                ProofPicker.MaxHeight = Math.Clamp(e.NewSize.Height - 114, 46, 96);
        };
        Workspace.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && Inspector.Visibility == Visibility.Visible)
                FitInspector();
        };
        Events.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && Events.SelectedItem is Activity active)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    if (Events.IsVisible && ReferenceEquals(Events.SelectedItem, active))
                        KeepActivityVisible(active);
                });
        };
        DarkTheme.IsChecked = App.IsDark;
        App.AppearanceChanged += UpdateAppearance;
        Transactions.OpenLog.IsEnabledChanged += (_, _) => OpenLogItem.IsEnabled = !busy && Transactions.OpenLog.IsEnabled;
        Transactions.Query.IsEnabledChanged += (_, _) => UpdateFindCommand();
        Transactions.ViewSource.IsEnabledChanged += (_, _) => UpdateSourceInfoCommand();
        Transactions.OpenLog.IsEnabledChanged += (_, _) => UpdateAttachMftCommand();
        Transactions.SourceChanged += () =>
        {
            if (!busy && journal == null && mft != null)
                SetPreparedMft(null, null);
            UpdateAttachMftCommand();
            BackToTimestampLeads.Visibility = Visibility.Collapsed;
            UpdateLeadSources();
        };
        UpdateFindCommand();
        GlobalView.FileRequested += JumpFromGlobal;
        TextCompositionManager.AddPreviewTextInputStartHandler(Search, (_, _) => { composingSearch = true; typing.Stop(); });
        Search.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, _) => EndSearchComposition()), true);
        Search.LostKeyboardFocus += (_, _) => { EndSearchComposition(); focusSearchResult = false; };
        typing.Tick += async (_, _) => { typing.Stop(); await RefreshFiles(); };
        Loaded += async (_, _) =>
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length < 2)
                {
                    await Dispatcher.InvokeAsync(ShowStartupSettings, DispatcherPriority.ApplicationIdle);
                    return;
                }
                string input = args[1];
                if (Directory.Exists(input))
                {
                    Setup.SelectFolder(input);
                    return;
                }
                if (args.Length > 2)
                {
                    string logPath = args.Length > 3 ? args[3] : "";
                    Setup.SetSources(input, args[2], logPath);
                    await AnalyzeFromSettings(input, args[2], logPath);
                }
                else if (!File.Exists(input))
                {
                    Setup.ShowError(L.T("파일을 찾을 수 없습니다. {0}", "File not found: {0}", input));
                }
                else
                {
                    switch (await Task.Run(() => IntakeSettings.FileRole(input)))
                    {
                        case IntakeSettings.Role.Image:
                            await Setup.OpenImageAsync(input);
                            break;
                        case IntakeSettings.Role.Log:
                            await OpenTransactionLog(input);
                            break;
                        case IntakeSettings.Role.Mft:
                            await Setup.OpenInputsAsync([input]);
                            break;
                        default:
                            HistoryTab.IsSelected = true;
                            await OpenJournal(input);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { Setup.ShowError(L.T("분석을 취소했습니다.", "Analysis cancelled.")); }
            catch (Exception ex) { if (SettingsTab.IsSelected) Setup.ShowError(ex.Message); else Report(ex); }
        };
        Closed += (_, _) => { closed = true; App.AppearanceChanged -= UpdateAppearance; historySortWork?.Cancel(); typing.Stop(); import?.Cancel(); historyExport?.Cancel(); (UsnLeads.Content as InvestigationView)?.CloseWithOwner(); (LogLeads.Content as TimestampLeadsView)?.CloseWithOwner(); source?.Dispose(); mftEvidence?.Dispose(); Setup.ReleaseImage(); Transactions.CloseWithOwner(); };
    }
    private void ShowStartupSettings()
    {
        if (closed || startupSettingsShown || !SettingsTab.IsSelected || busy)
            return;
        startupSettingsShown = true;
        SettingsTab.Content = null;
        Setup.CloseSettingsButton.Visibility = Visibility.Visible;
        IntakeWindow? dialog = null;
        try
        {
            dialog = startupSettings = new IntakeWindow(this, Setup, () => import?.Cancel());
            dialog.ShowDialog();
        }
        finally
        {
            if (ReferenceEquals(startupSettings, dialog))
                RestoreSettingsTab();
            if (!closed && SettingsTab.IsSelected)
                TransactionTab.IsSelected = true;
        }
    }

    private void RestoreSettingsTab()
    {
        startupSettings?.DetachSettings();
        startupSettings = null;
        Setup.CloseSettingsButton.Visibility = Visibility.Collapsed;
        Setup.IsEnabled = true;
        SettingsTab.Content = Setup;
    }

    private void CloseStartupSettingsForResults()
    {
        if (startupSettings is not { } dialog)
            return;
        RestoreSettingsTab();
        dialog.OpenResults();
    }
    private void Report(Exception e)
    {
        if (!closed)
            Status.Text = L.T("작업을 완료하지 못했습니다. ", "Could not complete the operation. ") + e.Message;
    }
    private async void Pick(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = L.T("USN Journal ($J) 열기", "Open USN Journal ($J)"), Filter = L.T("추출 파일|*.*", "Extracted files|*.*") };
        if (!busy && d.ShowDialog(this) == true)
        {
            if (SettingsTab.IsSelected)
                HistoryTab.IsSelected = true;
            await OpenJournal(d.FileName);
        }
    }
    private async void DropFile(object sender, DragEventArgs e)
    {
        DropPreview.Visibility = Visibility.Collapsed;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Handled = true;
        e.Effects = SourceDropEffect(e);
        if (e.Effects == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            return;
        SettingsTab.IsSelected = true;
        await Setup.OpenInputsAsync(paths);
    }
    private DragDropEffects SourceDropEffect(DragEventArgs e) =>
        !busy && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private void SourceDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropPreview.Visibility = Visibility.Collapsed;
            return;
        }
        e.Effects = SourceDropEffect(e);
        DropPreview.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private void SourceDragLeave(object sender, DragEventArgs e) => DropPreview.Visibility = Visibility.Collapsed;
    private async void OpenSourcesDialog(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        await Setup.ChooseSourcesAsync();
    }
    private async Task OpenTransactionLog(string path)
    {
        TransactionTab.IsSelected = true;
        await Transactions.OpenSourceAsync(path);
    }
    private void Cancel(object sender, RoutedEventArgs e) => import?.Cancel();
    internal async Task<T> ReadImageAsync<T>(string message, Func<CancellationToken, Task<T>> work)
    {
        if (busy)
            throw new InvalidOperationException(L.T("현재 작업이 끝난 뒤 다시 시도하세요.", "Wait for the current operation to finish."));
        using var stop = new CancellationTokenSource();
        import = stop;
        SetBusy(true);
        Status.Text = message;
        try
        {
            var result = await Task.Run(() => work(stop.Token), stop.Token);
            if (stop.IsCancellationRequested)
            {
                if (result is IDisposable lease)
                    lease.Dispose();
                stop.Token.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { import = null; Status.Text = summaryText; SetBusy(false); }
    }
    private async void PickLog(object sender, RoutedEventArgs e)
    {
        if (!busy && OpenLogItem.IsEnabled)
            await Transactions.PickSourceAsync(this, () => TransactionTab.IsSelected = true);
    }
    private void ExitApplication(object sender, RoutedEventArgs e) => Close();
    private async Task AnalyzeFromSettings(string journalPath, string mftPath, string logPath)
    {
        var (changed, previewed) = await ApplyIntakeAsync(journalPath, mftPath, logPath);
        if (closed)
            return;
        SynchronizeSettings(true);
        if (previewed)
            return;
        if (!changed && previousResultsTab?.IsEnabled == true)
            previousResultsTab.IsSelected = true;
        else if (journalPath.Length > 0)
            GlobalTab.IsSelected = true;
        else
            TransactionTab.IsSelected = true;
        CloseStartupSettingsForResults();
    }
    private void SelectWorkspace(object sender, RoutedEventArgs e)
    {
        var tab = sender == ShowHistoryItem ? HistoryTab : sender == ShowGlobalItem ? GlobalTab : sender == ShowSettingsItem ? SettingsTab :
            sender == ShowLeadsItem ? LeadsTab : TransactionTab;
        if ((!busy || showingIndexResults) && tab.IsEnabled)
        {
            tab.IsSelected = true;
            tab.Focus();
        }
    }
    private void FindInView(object sender, RoutedEventArgs e)
    {
        if (!FindItem.IsEnabled)
            return;
        if (TransactionTab.IsSelected)
        {
            Transactions.Query.Focus();
            Transactions.Query.SelectAll();
        }
        else if (GlobalTab.IsSelected)
            GlobalView.FocusSearch();
        else
        {
            Search.Focus();
            Search.SelectAll();
        }
    }
    private void UpdateFindCommand() => FindItem.IsEnabled = (!busy || showingIndexResults) && SettingsTab?.IsSelected != true && LeadsTab?.IsSelected != true && (TransactionTab.IsSelected ? Transactions.Query.IsEnabled : journal != null);
    private void OpenGuide(object sender, RoutedEventArgs e)
    {
        if (OwnedWindows.OfType<GuideWindow>().FirstOrDefault() is { } guide)
        {
            if (guide.WindowState == WindowState.Minimized)
                guide.WindowState = WindowState.Normal;
            guide.Activate();
            return;
        }
        new GuideWindow { Owner = this }.Show();
    }
    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        var description = L.T("NTFS Tracer {0}\nNTFS 파일 조사", "NTFS Tracer {0}\nNTFS file investigation", version);
        var credit = L.Caption("AboutAuthor").Replace("{0}", "absolroot", StringComparison.Ordinal);
        MessageBox.Show(this, $"{description}\n\n{credit}", L.Caption("AboutJournaldesk"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void OpenCarving(object sender, RoutedEventArgs e) => new CarvingWindow(source?.Name) { Owner = this }.Show();
    private void OpenLogCarving(object sender, RoutedEventArgs e) => new LogCarveWindow(Transactions.SourcePath) { Owner = this }.Show();
    private void ChangeTheme(object sender, RoutedEventArgs e) =>
        App.SetAppearance(DarkTheme.IsChecked ? AppearancePreference.Dark : AppearancePreference.Light);

    private void UpdateAppearance(object? sender, EventArgs e)
    {
        DarkTheme.IsChecked = App.IsDark;
        Setup.RefreshAppearance();
    }
    private void UpdateLeadSources()
    {
        UsnLeads.IsEnabled = journal != null;
        LogLeads.IsEnabled = Transactions.SourcePath != null;
        LeadsTab.IsEnabled = UsnLeads.IsEnabled || LogLeads.IsEnabled;
        if (LeadSources.SelectedItem is not TabItem { IsEnabled: true })
            LeadSources.SelectedItem = UsnLeads.IsEnabled ? UsnLeads : LogLeads;
        if (LogLeads.Content is TimestampLeadsView view)
            view.SetSource(Transactions.LeadQuery, Transactions.SourcePath, Transactions.SourceHash);
        if (LeadsTab.IsSelected)
            ShowInvestigation();
    }

    private void ShowInvestigation()
    {
        if (LogLeads.IsEnabled && LogLeads.Content == null)
        {
            var view = new TimestampLeadsView(async lsn =>
            {
                TransactionTab.IsSelected = true;
                BackToTimestampLeads.Visibility = Visibility.Visible;
                await Transactions.OpenLeadLsnAsync(lsn);
            });
            LogLeads.Content = view;
            view.SetSource(Transactions.LeadQuery, Transactions.SourcePath, Transactions.SourceHash);
        }
        if (UsnLeads.Content != null || journal is not { } j || source == null)
            return;
        UsnLeads.Content = new InvestigationView(j, source.Name, async (id, pos, openHex) =>
        {
            if (j == journal && !closed)
                await GoToRecord(id, pos, false, fromLeads: true, openHex: openHex);
        });
    }
    private void PickTrackerResult(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = L.T("기존 NTFS Log Tracker 결과 열기", "Open NTFS Log Tracker results"), Filter = L.T("Tracker 결과|*.db;*.sqlite;*.sqlite3;*.csv|모든 파일|*.*", "Tracker results|*.db;*.sqlite;*.sqlite3;*.csv|All files|*.*") };
        if (picker.ShowDialog(this) == true)
            new TrackerResultsWindow(picker.FileName) { Owner = this }.Show();
    }
    private void Shortcuts(object sender, KeyEventArgs e)
    {
        if (Search.IsKeyboardFocused && (composingSearch || e.Key == Key.ImeProcessed))
            return;
        if (HistoryTab.IsSelected && EvidencePaneNavigation.MoveFocus(e, Events, Inspector, EvidenceTabs))
            return;
        if ((e.Key == Key.Left || e.Key == Key.System && e.SystemKey == Key.Left) && Keyboard.Modifiers == ModifierKeys.Alt && HistoryTab.IsSelected && (!busy || showingIndexResults))
        {
            if (BackToLeads.Visibility == Visibility.Visible)
            {
                BackToLeadsClicked(sender, e);
                e.Handled = true;
                return;
            }
            if (BackToLog.Visibility == Visibility.Visible)
            {
                BackToLogClicked(sender, e);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            OpenGuide(sender, e);
            return;
        }
        if (e.Key == Key.Escape && MainMenu.IsKeyboardFocusWithin)
            return;
        if (e.Key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true } or ComboBoxItem or MenuItem)
            return;
        if (e.Key == Key.Escape && Search.IsKeyboardFocused && Search.Text.Length > 0)
        {
            Search.Clear();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && HistoryTab.IsSelected && Inspector.IsVisible)
        {
            if (Hex.IsKeyboardFocusWithin && Hex.SelectionLength > 1)
                return;
            HideInspector(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (e.Key == Key.O)
        {
            e.Handled = true;
            OpenSourcesDialog(sender, e);
            return;
        }
        if (busy && !showingIndexResults)
            return;
        if (e.Key == Key.OemComma)
        {
            if (SettingsTab.IsEnabled)
            {
                SettingsTab.IsSelected = true;
                SettingsTab.Focus();
            }
            e.Handled = true;
            return;
        }
        if (e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
        {
            var tab = e.Key switch
            {
                Key.D1 => GlobalTab,
                Key.D2 => HistoryTab,
                Key.D3 => TransactionTab,
                _ => LeadsTab
            };
            if (tab.IsEnabled)
            {
                tab.IsSelected = true;
                tab.Focus();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.F && FindItem.IsEnabled)
        {
            e.Handled = true;
            FindInView(sender, e);
        }
    }
    private void FitWorkspaceTabs(object sender, SizeChangedEventArgs e)
    {
        if (SettingsTab == null || Views.Template?.FindName("WorkspaceFullPaths", Views) is not CheckBox option)
            return;
        double available = Views.ActualWidth - option.DesiredSize.Width - SettingsTab.ActualWidth;
        double limit = Math.Max(76, Math.Floor(available / 4));
        foreach (var tab in new[] { GlobalTab, HistoryTab, TransactionTab, LeadsTab })
        {
            tab.MinWidth = Math.Min(102, limit);
            tab.MaxWidth = limit;
            tab.Padding = new Thickness(Views.ActualWidth < 880 ? 9 : 16, 4, Views.ActualWidth < 880 ? 9 : 16, 4);
        }
    }

    private void ChangeMode(object sender, SelectionChangedEventArgs e)
    {
        if (TransactionTab == null || !ReferenceEquals(e.OriginalSource, Views))
            return;
        if (SettingsTab?.IsSelected == true)
        {
            previousResultsTab = e.RemovedItems.OfType<TabItem>().FirstOrDefault();
            if (!showingIndexResults)
                SynchronizeSettings();
        }
        if (GlobalTab.IsSelected)
            BackToLog.Visibility = Visibility.Collapsed;
        if (LeadsTab?.IsSelected == true)
        {
            BackToLeads.Visibility = Visibility.Collapsed;
            ShowInvestigation();
        }
        UpdateJournalContext();
        UpdateGlobalSource();
        if (!HistoryTab.IsSelected && typing.IsEnabled)
        {
            typing.Stop();
            historyRefreshPending = true;
        }
        if (HistoryTab.IsSelected)
        {
            if (historyPathSortPending && !historyRefreshPending)
                _ = ApplyHistoryOrder();
            historyPathSortPending = false;
            _ = RefreshVisibleHistoryAsync();
        }
        else if (TransactionTab.IsSelected)
            _ = Transactions.RefreshPreparedSourceAsync();
    }

    private Task RefreshVisibleHistoryAsync()
    {
        if (!historyRefreshPending || !HistoryTab.IsSelected || journal == null || closed)
            return Task.CompletedTask;
        historyRefreshPending = false;
        return listedVersion != queryVersion || FileList.ItemsSource == null ? RefreshFiles() : LoadFile(true);
    }

    private void UpdateJournalContext()
    {
        bool show = !TransactionTab.IsSelected && LeadsTab?.IsSelected != true;
        UpdateFindCommand();
        UpdateSourceInfoCommand();
        JournalStatus.Visibility = (show && SettingsTab?.IsSelected != true) || busy ? Visibility.Visible : Visibility.Collapsed;
        PairNoticeBorder.Visibility = show && SettingsTab?.IsSelected != true && mft?.PairWarning != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetHistoryPathSource()
    {
        historyPaths = journal == null ? null : new JournalPathSource(journal, mft?.Paths, mft?.PairWarning, false);
        JournalPathText.SetSource(Workspace, historyPaths);
    }
    private void HistoryPathModeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (HistoryPathSourceColumn == null)
            return;
        bool full = JournalPathPreference.Current.Enabled;
        HistoryPathSourceColumn.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        if (!full && historySortColumn == HistoryPathSourceColumn)
            historySortColumn = null;
        if (Events.ItemsSource != null)
        {
            if (HistoryTab.IsSelected)
                _ = ApplyHistoryOrder();
            else
                historyPathSortPending = true;
        }
    }
    private void UpdateGlobalSource()
    {
        if (GlobalView == null)
            return;
        bool changed = !ReferenceEquals(journal, globalJournal) || !ReferenceEquals(mft?.Paths, globalPaths);
        if (!changed)
            return;
        if (GlobalTab.IsSelected)
        {
            globalJournal = journal;
            globalPaths = mft?.Paths;
            GlobalView.SetSource(journal, globalPaths, source?.Name, mft?.PairWarning, mft?.Hash, mft?.SourcePath, historyPaths, digest);
        }
        else if (globalJournal != null && !ReferenceEquals(journal, globalJournal))
        {
            globalJournal = null;
            globalPaths = null;
            GlobalView.SetSource(null, null, null);
        }
    }
    private async void JumpFromGlobal(byte[] id) => await GoToRecord(id, GlobalView.SelectedPosition, true);
    private void BackToLogClicked(object sender, RoutedEventArgs e)
    {
        GlobalTab.IsSelected = true;
        GlobalView.FocusSelectedRecord();
    }
    private void BackToTimestampLeadsClicked(object sender, RoutedEventArgs e)
    {
        LeadsTab.IsSelected = LogLeads.IsSelected = true;
        BackToTimestampLeads.Visibility = Visibility.Collapsed;
        (LogLeads.Content as TimestampLeadsView)?.FocusEvidence();
    }

    private void BackToLeadsClicked(object sender, RoutedEventArgs e)
    {
        LeadsTab.IsSelected = true;
        UsnLeads.IsSelected = true;
        (UsnLeads.Content as InvestigationView)?.FocusEvidence();
    }

    private async Task GoToRecord(byte[] id, long? position, bool fromGlobal, bool fromLeads = false, bool openHex = false)
    {
        if (busy && !showingIndexResults || journal is not { } j)
            return;
        int ticket = ++jumpVersion;
        try
        {
            var listed = listedVersion == queryVersion
                ? FileList.Items.OfType<FileRow>().FirstOrDefault(row => row.Id.AsSpan().SequenceEqual(id)) : null;
            var linked = mft;
            int? target = page;
            List<FileRow>? pageRows = null;
            FileHistoryData? fileData = null;
            if (listed == null)
            {
                (target, pageRows, fileData) = await Task.Run(() =>
                {
                    var destination = j.FilePage(id, PageSize);
                    var rows = destination is int number ? j.Files("", number, PageSize) : null;
                    var file = rows?.Find(row => row.Id.AsSpan().SequenceEqual(id));
                    return (destination, rows, file == null ? null : ReadFileHistory(j, linked, file));
                });
            }
            if (closed || ticket != jumpVersion || j != journal || linked != mft || (fromGlobal && (!GlobalTab.IsSelected || position != GlobalView.SelectedPosition)) ||
                (fromLeads && !LeadsTab.IsSelected) || busy && !showingIndexResults)
                return;
            if (target == null)
            {
                Status.Text = L.T("선택한 기록에 해당하는 파일을 목록에서 찾을 수 없습니다.", "The file for the selected record could not be found in the list.");
                return;
            }
            int destinationTab = fromGlobal ? GlobalView.Evidence.SelectedIndex : EvidenceTabs.SelectedIndex;
            var raw = GlobalView.Raw;
            int anchor = fromGlobal && GlobalView.OriginalBytesTab.IsSelected && raw.BaseOffset == position ? raw.SelectionAnchor : -1;
            int caret = anchor >= 0 ? raw.SelectedOffset : -1;
            bool reloadSelection = historyRefreshPending;
            historyRefreshPending = false;
            HistoryTab.IsSelected = true;
            BackToLog.Visibility = fromGlobal ? Visibility.Visible : Visibility.Collapsed;
            BackToLeads.Visibility = fromLeads ? Visibility.Visible : Visibility.Collapsed;
            if (openHex)
                OriginalBytesTab.IsSelected = true;
            else if (fromGlobal && destinationTab >= 0)
            {
                EvidenceTabs.SelectedIndex = destinationTab;
            }
            if (listed != null)
            {
                if (reloadSelection || current != listed || position is not long pos || !SelectHistoryRecord(pos))
                {
                    updatingFileList = true;
                    try
                    {
                        FileList.SelectedItem = listed;
                        FileList.ScrollIntoView(listed);
                    }
                    finally { updatingFileList = false; }
                    await LoadFile(false, position);
                }
            }
            else
            {
                Search.Clear();
                typing.Stop();
                page = target.Value;
                await RefreshFiles(id, position, pageRows, fileData);
            }
            if (!closed && ticket == jumpVersion && HistoryTab.IsSelected)
            {
                if (openHex && Hex.BaseOffset == position)
                    Hex.FocusHexColumn();
                else
                    Events.Focus();
                if (anchor >= 0 && caret >= 0 && OriginalBytesTab.IsSelected && Hex.BaseOffset == position)
                    Hex.SelectRange(anchor, caret);
            }
        }
        catch (Exception ex) { if (!closed && j == journal) Report(ex); }
    }
    private async Task OpenJournal(string path, string? explicitMft = null)
    {
        if (busy)
            return;
        if (TransactionTab.IsSelected)
            HistoryTab.IsSelected = true;
        using var stop = new CancellationTokenSource();
        import = stop;
        SetBusy(true);
        FileStream? input = null;
        try
        {
            input = OpenEvidence(path);
            var prepared = await PrepareJournalAsync(input, text => Status.Text = text, stop.Token);
            SetPreparedJournal(input, prepared);
            input = null;
            string? linkNotice = null;
            try
            {
                if (explicitMft != null)
                    await Attach(explicitMft, null, stop.Token);
                else if (MftAttachment.ReadLink(LinkFile, source!.Name) is { } link)
                {
                    if (File.Exists(link.MftPath))
                        await Attach(link.MftPath, link.Hash, stop.Token);
                    else
                        linkNotice = L.T("이전에 연결한 $MFT를 찾을 수 없습니다. ‘MFT 연결’에서 다시 선택해 주세요.", "The previously attached $MFT could not be found. Use Attach $MFT to select it again.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { linkNotice = L.T("MFT를 연결하지 못했습니다. ", "Could not attach $MFT. ") + ex.Message; }
            await RefreshVisibleHistoryAsync();
            if (linkNotice != null)
                Status.Text = linkNotice + "   ·   " + summaryText;
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = L.T("분석을 취소했습니다. ", "Analysis cancelled. ") + summaryText; }
        catch (Exception ex) { Report(ex); }
        finally { input?.Dispose(); import = null; SetBusy(false); }
    }
    private string LinkFile => Path.Combine(Cache, digest + ".mft-link.json");
    private void SetBusy(bool value)
    {
        busy = value;
        if (value)
            DropPreview.Visibility = Visibility.Collapsed;
        OpenJournalItem.IsEnabled = !value;
        OpenLogItem.IsEnabled = !value && Transactions.OpenLog.IsEnabled;
        UpdateAttachMftCommand();
        ViewMenu.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Views.IsEnabled = !value || showingIndexResults;
        if (startupSettings != null)
            Setup.IsEnabled = !value;
        if (!value)
            SettingsTab.IsEnabled = true;
        UpdateJournalContext();
    }
    private async void OpenSourceInfo(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        if (TransactionTab.IsSelected)
        {
            Transactions.ShowSourceInfo();
            return;
        }
        if (source == null || journal == null || readingJournalInfo)
            return;
        var path = source.Name;
        var index = journal;
        var hash = digest;
        var attachment = mft;
        readingJournalInfo = true;
        UpdateSourceInfoCommand();
        try
        {
            var items = await Task.Run(() =>
            {
                var result = new List<SourceInfo> { SourceInfo.Read(path, index.DbPath, hash, false) };
                if (attachment != null)
                    result.Add(SourceInfo.Read(attachment.SourcePath, attachment.Database, attachment.Hash, true));
                return result;
            });
            if (!closed && !TransactionTab.IsSelected && journal == index && attachment == mft)
                new SourceInfoWindow(items, attachment?.PairWarning) { Owner = this }.Show();
        }
        catch (Exception ex) { Report(ex); }
        finally { readingJournalInfo = false; if (!closed) UpdateSourceInfoCommand(); }
    }
    private void UpdateSourceInfoCommand()
    {
        bool transaction = TransactionTab.IsSelected;
        JournalInfoItem.IsEnabled = !busy && SettingsTab?.IsSelected != true && (transaction ? Transactions.CanShowSourceInfo : source != null && !readingJournalInfo);
        JournalInfoItem.ToolTip = transaction ? L.T("$LogFile 원본 정보", "$LogFile source information") : mft == null ? L.T("USN Journal ($J) 원본 정보", "USN Journal ($J) source information") : L.T("USN Journal ($J) 및 $MFT 원본 정보", "USN Journal ($J) and $MFT source information");
    }
    private void UpdateSourceLabel()
    {
        PairNotice.Text = mft?.PairWarning ?? "";
        UpdateJournalContext();
        AttachMftItem.Header = mft == null ? L.T("$MFT 연결…", "Attach $MFT…") : L.T("$MFT 변경…", "Change $MFT…");
        AttachMftItem.ToolTip = mft == null ? L.T("같은 볼륨의 파일 목록($MFT) 연결", "Attach the $MFT from the same volume") : L.T("연결한 파일 목록 변경\n{0}", "Change the attached $MFT\n{0}", mft.SourcePath);
        UpdateAttachMftCommand();
    }
    private void UpdateAttachMftCommand() =>
        AttachMftItem.IsEnabled = !busy && Transactions.CanReplaceSource && (journal != null || Transactions.SourcePath != null);

    private async void PickMft(object sender, RoutedEventArgs e) => await ChooseMft();
    private async Task ChooseMft(string? suggested = null)
    {
        string? openLog = source?.Name ?? Transactions.SourcePath;
        if (busy || openLog == null || !Transactions.CanReplaceSource)
            return;
        var dir = Path.GetDirectoryName(openLog)!;
        var sibling = Path.Combine(dir, "$MFT");
        string defaultName = suggested ?? mft?.SourcePath ?? (File.Exists(sibling) ? sibling : "");
        var picker = new OpenFileDialog
        {
            Title = L.T("같은 볼륨에서 추출한 파일 테이블($MFT) 연결", "Attach the $MFT extracted from the same volume"),
            Filter = L.T("추출 파일|*.*", "Extracted files|*.*"),
            InitialDirectory = dir,
            FileName = defaultName,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true)
            return;
        SetBusy(true);
        using var stop = new CancellationTokenSource();
        import = stop;
        try
        {
            await Attach(picker.FileName, null, stop.Token);
        }
        catch (OperationCanceledException) { if (!closed) Status.Text = L.T("MFT 연결을 취소했습니다. ", "MFT attachment cancelled. ") + summaryText; }
        catch (Exception ex) { Report(ex); }
        finally { import = null; SetBusy(false); }
    }
    private async Task Attach(string filename, string? expected, CancellationToken token)
    {
        var selected = journal;
        if (selected == null && Transactions.SourcePath == null)
            return;
        FileStream? input = OpenEvidence(filename);
        try
        {
            var index = await PrepareMftIndexAsync(input, expected, text => Status.Text = text, token);
            var attachment = await AttachMftIndexAsync(input, selected, index, token);
            SetPreparedMft(input, attachment);
            input = null;
            UpdateAttachmentStatus(expected == null);
            await RefreshVisibleHistoryAsync();
        }
        finally { input?.Dispose(); }
    }
    private static Task BuildIndex(string path, string output, bool isMft, Action<string>? progress, CancellationToken token)
    {
        return EngineProcess.RunAsync(isMft ? "mft" : null, path, output, token, progress == null ? null : line =>
        {
            var parts = line.Split(' ');
            if (parts.Length == 4 && parts[0] == "read" && double.TryParse(parts[1], out var done) && double.TryParse(parts[2], out var total))
                progress((isMft ? L.T("MFT 파일·폴더 항목을 읽고 있습니다…", "Reading MFT file and folder entries…") : L.T("변경 기록을 읽고 있습니다…", "Reading change records…")) + L.T("   {0:P0}   ·   {1}건", "   {0:P0}   ·   Records: {1}", done / Math.Max(total, 1), parts[3]));
            if (line == "index")
                progress(isMft ? L.T("폴더 경로 색인을 만들고 있습니다…", "Indexing folder paths…") : L.T("파일별 이력과 이전 이름 검색 색인을 만들고 있습니다…", "Indexing file histories and previous names…"));
        });
    }
    private void ResetSelection()
    {
        historySortWork?.Cancel();
        selectedMftEvidence = null;
        fileVersion++;
        current = null;
        history = [];
        activities = [];
        Events.ItemsSource = null;
        SetInspector(false);
        EmptyActivities.Visibility = Visibility.Collapsed;
        FileTitle.Text = L.T("파일을 선택하세요", "Select a file");
        FileTitle.ToolTip = null;
        Observed.Text = "";
        Observed.ToolTip = null;
        SnapshotPanel.Visibility = Visibility.Collapsed;
        SnapshotPath.Text = "";
        SelectedIcon.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = false;
    }
    private async Task RefreshFiles(byte[]? targetId = null, long? position = null, List<FileRow>? pageRows = null, FileHistoryData? fileData = null)
    {
        if (journal == null || closed)
            return;
        int version = ++queryVersion, requested = page;
        var j = journal;
        string query = Search.Text.Trim();
        var bookmark = targetId == null ? emptySearchBookmark : null;
        runningFileQuery = version;
        BeginFileQuery();
        try
        {
            if (bookmark != null && query.Length == 0)
            {
                requested = await Task.Run(() => j.FilePage(bookmark.Id, PageSize) ?? 0);
                if (closed || version != queryVersion || j != journal)
                    return;
                page = requested;
            }
            var rows = pageRows ?? await Task.Run(() => j.Files(query, requested, PageSize));
            if (closed || version != queryVersion || j != journal)
                return;
            if (!HistoryTab.IsSelected)
            {
                historyRefreshPending = true;
                return;
            }
            listedVersion = version;
            bool more = rows.Count > PageSize;
            if (more)
                rows.RemoveAt(PageSize);
            foreach (var duplicate in rows.GroupBy(row => (row.Name, row.Subtitle)).Where(group => group.Count() > 1).SelectMany(group => group))
                duplicate.Disambiguator = L.T("파일 식별 정보  ·  ", "File reference  ·  ") + Journal.Ref(duplicate.Id);
            var sought = targetId ?? current?.Id ?? bookmark?.Id;
            var selected = sought == null ? null : rows.Find(r => r.Id.AsSpan().SequenceEqual(sought));
            bool retainHistory = targetId == null && current != null && selected != null;
            bool restoreBookmark = bookmark != null && selected != null;
            if (targetId == null)
                selected ??= rows.FirstOrDefault();
            updatingFileList = true;
            try
            {
                FileList.ItemsSource = rows;
                FileList.SelectedItem = selected;
                if (selected != null)
                    FileList.ScrollIntoView(selected);
            }
            finally { updatingFileList = false; }
            FileList.IsEnabled = rows.Count > 0;
            FileSearchEmpty.Text = query.Length > 0 ? L.T("일치하는 파일이 없습니다.", "No matching files.") : L.T("표시할 파일이 없습니다.", "No files to display.");
            FileSearchEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Prev.IsEnabled = requested > 0;
            Next.IsEnabled = more;
            PageInfo.Text = requested == 0 && !more ? L.T("항목 {0:N0}개", "Items: {0:N0}", rows.Count) : L.T("{0:N0}–{1:N0}번째", "Items {0:N0}–{1:N0}", requested * PageSize + 1, requested * PageSize + rows.Count);
            if (focusSearchResult && Search.IsKeyboardFocused && HistoryTab.IsSelected)
                FocusFileResult();
            focusSearchResult = false;
            if (selected != null)
                emptySearchBookmark = null;
            if (retainHistory)
                current = selected;
            else if (selected != null)
                await LoadFile(false, position, restoreBookmark ? bookmark : null, fileData);
            else
            {
                if (query.Length > 0 && current != null)
                    emptySearchBookmark = new(current.Id, (ProofPicker.SelectedItem as Entry)?.Position, ActivityFilter.SelectedIndex,
                        Inspector.Visibility == Visibility.Visible, Hex.SelectionAnchor, Hex.SelectedOffset);
                ResetSelection();
                if (targetId != null)
                    Status.Text = L.T("요청한 파일을 현재 목록에서 찾을 수 없습니다.", "The requested file could not be found in this list.");
            }
        }
        catch (Exception ex) { if (version == queryVersion) { PageInfo.Text = L.T("조회 실패", "Query failed"); focusSearchResult = false; Report(ex); } }
        finally { if (runningFileQuery == version) runningFileQuery = -1; }
    }
    private void BeginFileQuery()
    {
        Prev.IsEnabled = Next.IsEnabled = FileList.IsEnabled = false;
        FileSearchEmpty.Visibility = Visibility.Collapsed;
        PageInfo.Text = L.T("조회 중…", "Searching…");
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (journal == null)
            return;
        page = 0;
        queryVersion++;
        focusSearchResult = false;
        BeginFileQuery();
        typing.Stop();
        if (!composingSearch)
            typing.Start();
    }
    private void EndSearchComposition()
    {
        if (!composingSearch)
            return;
        composingSearch = false;
        if (journal != null && listedVersion != queryVersion)
        {
            typing.Stop();
            typing.Start();
        }
    }
    private async void FileSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (composingSearch || e.Key is not (Key.Enter or Key.Down) || Keyboard.Modifiers != ModifierKeys.None || busy && !showingIndexResults || journal == null)
            return;
        e.Handled = true;
        if (listedVersion == queryVersion)
        {
            FocusFileResult();
            return;
        }
        focusSearchResult = true;
        if (runningFileQuery != queryVersion)
        {
            typing.Stop();
            await RefreshFiles();
        }
    }
    private void FocusFileResult()
    {
        if (FileList.SelectedItem is not FileRow result || !FileList.IsEnabled)
            return;
        FileList.ScrollIntoView(result);
        FileList.UpdateLayout();
        if (FileList.ItemContainerGenerator.ContainerFromItem(result) is ListBoxItem item)
            item.Focus();
    }
    private void ClearFileSearch(object sender, RoutedEventArgs e)
    {
        Search.Clear();
        Search.Focus();
    }
    private async void Previous(object sender, RoutedEventArgs e)
    {
        if (page > 0)
        {
            page--;
            await RefreshFiles();
        }
    }
    private async void Following(object sender, RoutedEventArgs e)
    {
        page++;
        await RefreshFiles();
    }
    private async void SelectFile(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingFileList)
            await LoadFile();
    }
    private sealed record FileHistoryData(List<Entry> Entries, List<Activity> Activities, PathResult? Snapshot, List<EvidenceField>? MftFields);

    private static FileHistoryData ReadFileHistory(Journal index, MftAttachment? linked, FileRow file)
    {
        var entries = index.History(file.Id);
        var activities = linked == null ? index.Activities(entries) : Activity.From(entries);
        return new(entries, activities, linked?.Paths.Snapshot(file.Id), linked?.NameEvidence(file.Id, file.Name));
    }

    private async Task LoadFile(bool preserveSelection = false, long? position = null, FileSearchBookmark? bookmark = null, FileHistoryData? data = null)
    {
        if (closed)
            return;
        int savedFilter = bookmark?.Filter ?? (preserveSelection ? ActivityFilter.SelectedIndex : 0);
        long? savedRecord = position ?? bookmark?.Position ?? (preserveSelection ? (ProofPicker.SelectedItem as Entry)?.Position : null);
        ResetSelection();
        if (FileList.SelectedItem is not FileRow f || journal is not { } j)
            return;
        var linked = mft;
        var version = fileVersion;
        current = f;
        FileTitle.Text = f.Name;
        FileTitle.ToolTip = f.Name;
        SelectedIcon.Source = f.Icon;
        SelectedIcon.Visibility = SelectedIcon.Source == null ? Visibility.Collapsed : Visibility.Visible;
        Observed.Text = L.T("이력을 읽고 있습니다…", "Loading file history…");
        try
        {
            var (entries, grouped, snapshot, mftEvidence) = data ?? await Task.Run(() => ReadFileHistory(j, linked, f));
            if (closed || version != fileVersion)
                return;
            selectedMftEvidence = mftEvidence;
            if (snapshot != null && snapshot.HasKnownPath)
            {
                SnapshotPanel.Visibility = Visibility.Visible;
                int candidates = snapshot.Display.Count(c => c == '\n') + 1;
                bool several = candidates > 1, simple = snapshot.IsMftSnapshot;
                SnapshotPath.Visibility = several ? Visibility.Collapsed : Visibility.Visible;
                SnapshotPath.Text = snapshot.Display;
                SnapshotPath.ToolTip = snapshot.Display;
                SnapshotBasis.Text = snapshot.DisplayBasis;
                SnapshotBasis.Visibility = simple || several ? Visibility.Collapsed : Visibility.Visible;
                SnapshotMore.IsExpanded = false;
                SnapshotMore.Header = several ? L.T("경로 후보 {0}개 보기", "Show candidate paths ({0})", candidates) : L.Caption("PathEvidence");
                SnapshotMore.Visibility = several || snapshot.Display.Length > 150 || !simple ? Visibility.Visible : Visibility.Collapsed;
                SnapshotFull.Text = snapshot.Display;
                SnapshotFullReason.Text = snapshot.Explanation;
            }
            history = entries;
            activities = grouped;
            ActivityFilter.SelectedIndex = savedFilter;
            ApplyFilter();
            if (entries.Count == 0)
                return;
            var term = Search.Text.Trim();
            int hit = term.Length == 0 ? 0 : activities.FindIndex(a => a.Records.Any(item => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (!(savedRecord is long pos && SelectHistoryRecord(pos)) && Events.Items.Contains(activities[Math.Max(0, hit)]))
                Events.ScrollIntoView(activities[Math.Max(0, hit)]);
            if (bookmark is { Caret: >= 0, Anchor: >= 0 } && OriginalBytesTab.IsSelected && Hex.BaseOffset == bookmark.Position)
                Hex.SelectRange(bookmark.Anchor, bookmark.Caret);
            if (bookmark is { ShowInspector: false })
                SetInspector(false);
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex) { if (version == fileVersion) Report(ex); }
    }
    private bool SelectHistoryRecord(long position)
    {
        var activity = activities.Find(item => item.Records.Any(record => record.Position == position));
        if (activity == null)
            return false;
        if (!Events.Items.Contains(activity))
            ActivityFilter.SelectedIndex = 0;
        Events.SelectedItem = activity;
        ProofPicker.SelectedItem = activity.Records.First(record => record.Position == position);
        SetInspector(true);
        KeepActivityVisible(activity);
        return true;
    }
    private void ApplyFilter()
    {
        if (Events == null || EmptyActivities == null)
            return;
        var shown = ActivityFilter.SelectedIndex switch
        {
            1 => activities.Where(a => a.NameOrLocation).ToList(),
            2 => activities.Where(a => a.Lifecycle).ToList(),
            3 => activities.Where(a => a.Records.Any(r => r.Version == 4 || (r.Reason & 0x77) != 0)).ToList(),
            _ => activities
        };
        var previous = Events.SelectedItem as Activity;
        restoringActivity = previous != null && shown.Contains(previous);
        try
        {
            Events.ItemsSource = shown;
            if (restoringActivity)
            {
                Events.SelectedItem = previous;
                Events.ScrollIntoView(previous);
            }
        }
        finally { restoringActivity = false; }
        _ = ApplyHistoryOrder();
        if (current != null)
        {
            var count = ReferenceEquals(shown, activities) ? history.Count : shown.Sum(activity => activity.Records.Count);
            Observed.Text = L.T("활동 {0:N0}개   ·   원본 {1:N0}건", "Activities: {0:N0}   ·   Source records: {1:N0}", shown.Count, count);
            Observed.ToolTip = Observed.Text;
        }
        EmptyActivities.Visibility = shown.Count == 0 && activities.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private double? preferredEvidenceHeight;
    private void KeepActivityVisible(object activity)
    {
        if (Events.ItemContainerGenerator.ContainerFromItem(activity) is DataGridRow row && row.IsVisible)
        {
            double top = row.TranslatePoint(new Point(0, 0), Events).Y;
            if (top >= 24 && top + row.ActualHeight <= Events.ActualHeight)
                return;
        }
        Events.ScrollIntoView(activity);
    }
    private Entry? displayedRaw;
    private byte[]? rawData;
    private void FitInspector()
    {
        double available = Math.Max(90, Workspace.ActualHeight - Workspace.RowDefinitions[0].MinHeight - ProofSplitter.Height);
        EvidenceRow.MaxHeight = available;
        double height = preferredEvidenceHeight ?? Math.Clamp(Workspace.ActualHeight * 0.48, 264, 352);
        EvidenceRow.Height = new GridLength(Math.Min(height, available));
    }
    private void EvidenceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, EvidenceTabs) || Inspector?.IsVisible != true)
            return;
        if (OriginalBytesTab.IsSelected)
            LoadEvidence();
        else if (JsonTab.IsSelected)
            ShowRecordJson();
    }
    private void ShowRecordJson()
    {
        if (RecordJson.Text.Length == 0 && ProofPicker.SelectedItem is Entry entry && current is { } file && source is { } original)
            RecordJson.Text = UsnEvidence.RecordJson(entry, file.Id, original.Name, digest, selectedMftEvidence);
    }

    private void LoadEvidence()
    {
        if (ProofPicker.SelectedItem is not Entry entry || source is not { } file)
            return;
        if (ReferenceEquals(entry, displayedRaw) && rawData != null)
            return;
        try
        {
            rawData = UsnEvidence.Read(file, entry, current?.Id);
            UsnEvidence.Display(Hex, rawData, entry);
            displayedRaw = entry;
            DetachHistory.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            rawData = null;
            displayedRaw = null;
            DetachHistory.IsEnabled = false;
            var message = L.T("이 위치에서 원본 바이트를 읽을 수 없습니다. ", "Could not read source bytes at this offset. ") + ex.Message;
            Hex.ClearView(message);
        }
    }
    private void DetachEvidence(object sender, RoutedEventArgs e)
    {
        LoadEvidence();
        if (rawData != null && displayedRaw != null && source != null)
            new EvidenceWindow(source.Name, displayedRaw, rawData, Hex.SelectedOffset, Hex.SelectionAnchor) { Owner = this }.Show();
    }
    private void SetInspector(bool visible)
    {
        if (visible && Inspector.Visibility == Visibility.Visible)
            return;
        Inspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProofSplitter.Visibility = Inspector.Visibility;
        EvidenceRow.MinHeight = visible ? 90 : 0;
        if (visible)
            FitInspector();
        else
        {
            EvidenceRow.MaxHeight = double.PositiveInfinity;
            EvidenceRow.Height = new GridLength(0);
        }
    }
    private async void OpenComparison(object sender, RoutedEventArgs e)
    {
        if (Events.SelectedItem is not Activity activity || !ChangeComparisonWindow.CanCompare(activity)
            || source is not { } original || current is not { } file || journal is not { } index)
            return;
        int version = fileVersion;
        CompareChanges.IsEnabled = false;
        try
        {
            if (historyPaths is not { } paths)
                return;
            var before = await paths.ReadAsync(activity.Records[0], file.Id, CancellationToken.None);
            var after = await paths.ReadAsync(activity.Records[1], file.Id, CancellationToken.None);
            if (closed || version != fileVersion || !ReferenceEquals(activity, Events.SelectedItem))
                return;
            new ChangeComparisonWindow(original, activity, before, after, file.Id) { Owner = this }.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(ex); }
        finally { if (!closed) CompareChanges.IsEnabled = true; }
    }
    private void OpenNeighborhood(object sender, RoutedEventArgs e)
    {
        if (source == null || journal == null || current == null || ProofPicker.SelectedItem is not Entry entry)
            return;
        try
        {
            new NeighborhoodWindow(journal, source.Name, entry, DisplayTimeZone.Current.Zone, current.Id) { Owner = this }.Show();
        }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenActivityEvidence(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Activity activity })
            return;
        Events.SelectedItem = activity;
        SetInspector(true);
        OriginalBytesTab.IsSelected = true;
    }
    private void HideInspector(object sender, RoutedEventArgs e)
    {
        SetInspector(false);
        EvidencePaneNavigation.FocusRow(Events);
    }
    private void ActivityKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Events.SelectedItem is Activity && !Inspector.IsVisible)
        {
            SetInspector(true);
            e.Handled = true;
        }
    }
    private void ReopenActivity(object sender, MouseButtonEventArgs e)
    {
        if (!Inspector.IsVisible && e.OriginalSource is DependencyObject target &&
            ItemsControl.ContainerFromElement(Events, target) is DataGridRow { IsSelected: true })
            SetInspector(true);
    }
    private void SelectEvent(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Events) || restoringActivity)
            return;
        var active = Events.SelectedItem as Activity;
        NearbyHistory.IsEnabled = active != null;
        CompareChanges.Visibility = ChangeComparisonWindow.CanCompare(active) ? Visibility.Visible : Visibility.Collapsed;
        if (active is not { } activity)
        {
            ProofPicker.ItemsSource = null;
            SetInspector(false);
            return;
        }
        SetInspector(true);
        if (EvidenceTabs.SelectedIndex < 0)
            EvidenceTabs.SelectedIndex = 0;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(Events.SelectedItem, activity))
                KeepActivityVisible(activity);
        }), DispatcherPriority.Loaded);
        bool multiple = activity.Records.Count > 1;
        ActionName.Text = multiple ? L.T("원본 기록 {0}건", "Source records ({0})", activity.Records.Count) : L.Caption("SourceRecord");
        ActionName.ToolTip = activity.ClockDetail;
        ProofPicker.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        ProofPicker.ItemsSource = activity.Records;
        ProofPicker.SelectedIndex = 0;
    }
    private void OpenRecordBytes(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject target && ItemsControl.ContainerFromElement(ProofPicker, target) is ListBoxItem { IsSelected: true })
            OriginalBytesTab.IsSelected = true;
    }
    private void RecordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ProofPicker.SelectedItem is Entry)
        {
            OriginalBytesTab.IsSelected = true;
            e.Handled = true;
        }
    }
    private void SizeProofColumns(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || ProofOffsetColumn == null)
            return;
        double width = e.NewSize.Width;
        ProofReasonColumn.Width = width < 525 ? 120 : Math.Min(200, width * .23);
        ProofOffsetColumn.Width = width < 550 ? 0 : 110;
        ProofNameColumn.Width = Math.Max(60, width - ProofReasonColumn.Width - ProofUsnColumn.Width - ProofOffsetColumn.Width - 12);
    }
    private async void ProofChanged(object sender, SelectionChangedEventArgs e) => await RefreshHistoryProof(true);

    private async Task RefreshHistoryProof(bool newSelection)
    {
        if (newSelection)
        {
            displayedRaw = null;
            rawData = null;
            DetachHistory.IsEnabled = false;
            Hex.ClearView();
            RecordJson.Clear();
            Technical.SetFields([]);
        }
        if (ProofPicker.SelectedItem is not Entry entry || current is not { } file || source is not { } original)
            return;
        int version = fileVersion;
        try
        {
            string reasons = entry.FlagsText;
            if (!ChangeComparisonWindow.CanCompare(Events.SelectedItem as Activity))
            {
                reasons += (entry.Reason & 0x3000) switch
                {
                    0x1000 => L.T(" · 대응하는 새 이름 기록 미확인", " · Matching new-name record not found"),
                    0x2000 => L.T(" · 대응하는 이전 이름 기록 미확인", " · Matching old-name record not found"),
                    _ => ""
                };
            }
            var notes = new List<string>();
            if (entry.Version == 4)
                notes.Add(entry.ExtentSummary);
            if (entry.Resynced)
                notes.Add(L.T("손상 구간 뒤에서 형식을 다시 확인한 첫 기록입니다.", "First valid record after a damaged region."));
            if (entry.Escaped)
                notes.Add(L.T("문자로 해석할 수 없는 파일명 부분은 원래 UTF-16 코드값으로 표시합니다.", "Undecodable name characters are displayed as their original UTF-16 code units."));
            OverviewPanel.SetRecord(entry, DisplayTimeZone.Current.Zone, reasons, Path.GetFileName(original.Name) + "   ·   " + entry.RawLocation, string.Join(Environment.NewLine, notes));
            var fields = UsnEvidence.RecordFields(entry, file.Id);
            fields.Add(new(L.T("원본 파일", "Source file"), original.Name));
            fields.Add(new("SHA-256", digest));
            if (selectedMftEvidence != null)
                fields.AddRange(selectedMftEvidence);
            Technical.SetFields(fields);
            if (JsonTab.IsSelected)
                ShowRecordJson();
            if (OriginalBytesTab.IsSelected)
                LoadEvidence();
            if (historyPaths is not { } paths)
            {
                OverviewPanel.SetPath(L.T("경로를 확인할 수 없습니다.", "Path unavailable."), "");
                return;
            }
            var result = await paths.ReadAsync(entry, file.Id, CancellationToken.None);
            if (!closed && version == fileVersion && ReferenceEquals(paths, historyPaths) && ReferenceEquals(entry, ProofPicker.SelectedItem))
                OverviewPanel.SetPath(result.Display, result.Explanation, entry.Name);
        }
        catch (Exception ex)
        {
            if (!closed && version == fileVersion && ReferenceEquals(entry, ProofPicker.SelectedItem))
            {
                OverviewPanel.SetPath(L.T("경로를 확인할 수 없습니다.", "Could not resolve the path."), ex.Message);
                Report(ex);
            }
        }
    }
    private static string DefaultHistoryExportName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = string.Concat(name.Take(100).Select(c => invalid.Contains(c) ? '_' : c)).Trim();
        if (safe.Length > 0 && char.IsHighSurrogate(safe[^1]))
            safe = safe[..^1];
        return safe.Length == 0 ? L.T("파일 이력.csv", "File history.csv") : L.T("파일 이력 - {0}.csv", "File history - {0}.csv", safe);
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (historyExport != null || current is not { } file || source == null || history.Count == 0 || journal is not { } index)
            return;
        var data = history;
        var src = source.Name;
        var hash = digest;
        var linked = mft;
        var d = new SaveFileDialog { InitialDirectory = DeskSettings.InitialExportDirectory, Filter = L.T("CSV 파일|*.csv", "CSV files|*.csv"), FileName = DefaultHistoryExportName(file.Name), Title = L.T("선택한 파일의 기록 {0:N0}건 내보내기", "Export selected file history ({0:N0} records)", data.Count) };
        if (d.ShowDialog(this) != true)
            return;
        using var stop = new CancellationTokenSource();
        historyExport = stop;
        ExportHistoryCsv.IsEnabled = false;
        Status.Text = L.T("파일 이력을 저장하고 있습니다…", "Saving file history…");
        try
        {
            await Task.Run(() => Journal.Export(d.FileName, file, data, src, hash, linked?.Paths, linked?.Hash, linked?.PairWarning, linked?.SourcePath, index, stop.Token), stop.Token);
            if (!closed)
                SavedOutput.Show(Status, d.FileName, L.T("기록 {0:N0}건 저장", "{0:N0} records saved", data.Count));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { if (!closed) Status.Text = L.T("CSV 저장을 취소했습니다.", "CSV export cancelled."); }
        catch (Exception ex) { Report(ex); }
        finally { historyExport = null; if (!closed) ExportHistoryCsv.IsEnabled = true; }
    }
}
