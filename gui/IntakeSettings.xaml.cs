using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;

namespace JournalDesk;

public partial class IntakeSettings : UserControl
{
    internal event Func<string, string, string, Task>? AnalysisRequested;
    private (string Journal, string Mft, string Log) active = ("", "", "");
    private (string? Journal, string? Mft, string? Log) activeHashes;
    private readonly HashSet<TextBox> unverifiedPaths = [];
    private bool applying;
    private bool? pickingFirstSource;
    private int suggestedIndexLimit;
    internal IndexingMode SelectedIndexingMode => IndexingChoice.SelectedValue is IndexingMode mode ? mode : IndexingMode.Auto;

    public IntakeSettings()
    {
        InitializeComponent();
        AutoIndexing.Tag = IndexingMode.Auto;
        SequentialIndexing.Tag = IndexingMode.Sequential;
        ParallelIndexing.Tag = IndexingMode.Parallel;
        RefreshIndexRecommendation();
        IndexingChoice.SelectedValue = DeskSettings.PreferredIndexingMode;
        WindowsAppearance.Tag = AppearancePreference.Windows;
        LightAppearance.Tag = AppearancePreference.Light;
        DarkAppearance.Tag = AppearancePreference.Dark;
        RefreshAppearance();
        AppearanceChoice.SelectionChanged += ChangeAppearance;
        RefreshStoragePaths();
        IndexingChoice.SelectionChanged += SaveIndexingMode;
        IndexingChoice.DropDownOpened += (_, _) => RefreshIndexRecommendation();
        Loaded += (_, _) => RefreshIndexRecommendation();
        RefreshState();
    }

    internal void RefreshLanguage()
    {
        suggestedIndexLimit = 0;
        RefreshIndexRecommendation();
        ZonePreference.Items.Refresh();
        RefreshStoragePaths();
        RefreshState();
    }

    internal void RefreshAppearance() => AppearanceChoice.SelectedValue = App.Appearance;

    private void ChangeAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (AppearanceChoice.SelectedValue is AppearancePreference appearance)
            App.SetAppearance(appearance);
    }

    private void DismissSettings(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as IntakeWindow)?.Close();

    private void RefreshIndexRecommendation()
    {
        int limit = IndexConcurrency.RecommendedLimit;
        if (limit == suggestedIndexLimit)
            return;
        suggestedIndexLimit = limit;
        AutoIndexing.Content = limit == 1 ? L.T("자동 (순차)", "Auto (sequential)") : L.T("자동 (최대 2개 병렬)", "Auto (up to 2 in parallel)");
        AutoIndexing.ToolTip = limit == 1
            ? L.T("현재 사용할 수 있는 CPU와 메모리를 기준으로 원본 하나씩 색인을 생성합니다.", "Based on the available CPU and memory, Auto indexes one source at a time.")
            : L.T("현재 사용할 수 있는 CPU와 메모리를 기준으로, 새 색인이 필요한 원본이 여러 개면 최대 2개를 함께 처리합니다.", "Based on the available CPU and memory, Auto can index up to two sources together when several need a new index.");
    }

    internal int GetIndexingLimit()
    {
        RefreshIndexRecommendation();
        var mode = SelectedIndexingMode;
        return mode == IndexingMode.Auto ? suggestedIndexLimit : IndexConcurrency.Limit(mode);
    }

    private void SaveIndexingMode(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            DeskSettings.PreferredIndexingMode = SelectedIndexingMode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(L.T("색인 생성 방식을 저장하지 못했습니다. 이번 실행에는 선택한 방식을 사용합니다.",
                "Could not save the indexing mode. Your selection applies to this session."));
        }
    }

    internal void Synchronize(string? journal, string? mft, string? log, (string? Journal, string? Mft, string? Log) hashes, bool confirmed = false, bool preserveDraft = false)
    {
        var next = (Journal: journal ?? "", Mft: mft ?? "", Log: log ?? "");
        if (active != next || (confirmed && (JournalPath.Text, MftPath.Text, LogPath.Text) != next))
        {
            if (!preserveDraft && imageSession != null && !MatchesExtraction(next.Journal, next.Mft, next.Log))
                ClearImage();
            active = next;
            if (!preserveDraft)
            {
                SetSources(next.Journal, next.Mft, next.Log);
                confirmed = true;
            }
        }
        activeHashes = hashes;
        if (confirmed)
            unverifiedPaths.Clear();
        RefreshStoragePaths();
        RefreshState();
    }

    internal void SetSources(string journal, string mft, string log)
    {
        showFileRows = true;
        foreach (var field in new[] { JournalPath, MftPath, LogPath })
        {
            unverifiedPaths.Add(field);
            AutomationProperties.SetHelpText(field, "");
        }
        SetPathText(JournalPath, journal);
        SetPathText(MftPath, mft);
        SetPathText(LogPath, log);
        RefreshState();
    }

    private static void SetPathText(TextBox field, string value)
    {
        if (field.Text == value)
            return;
        field.Text = value;
        field.CaretIndex = value.Length;
        if (value.Length > 0)
            field.Dispatcher.InvokeAsync(() =>
            {
                if (field.Text == value && field.CaretIndex == value.Length && field.SelectionLength == 0)
                    field.ScrollToHorizontalOffset(double.MaxValue);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PathEdited(object sender, TextChangedEventArgs e)
    {
        if (StartAnalysis == null)
            return;
        unverifiedPaths.Add((TextBox)sender);
        AutomationProperties.SetHelpText((TextBox)sender, "");
        ShowError("");
        RefreshState();
    }
    private void ClearSource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox box })
            return;
        box.Clear();
        if (JournalPath.Text.Length + MftPath.Text.Length + LogPath.Text.Length == 0)
        {
            showFileRows = false;
            RefreshState();
            ChooseSourcesButton.Focus();
        }
        else
            box.Focus();
    }

    private void RefreshVerifiedHashes(bool extracted = false)
    {
        bool files = pending == null && imageSession == null && showFileRows;
        var image = extracted ? ImageInputs() : default;
        static string? Verified(string? path, string activePath, string? hash) =>
            !string.IsNullOrEmpty(path) && string.Equals(path, activePath, StringComparison.Ordinal) ? hash : null;
        string? FileHash(TextBox field, string path, string? hash) =>
            files && !unverifiedPaths.Contains(field) ? Verified(field.Text, path, hash) : null;
        JournalHash.Content = FileHash(JournalPath, active.Journal, activeHashes.Journal);
        MftHash.Content = FileHash(MftPath, active.Mft, activeHashes.Mft);
        LogHash.Content = FileHash(LogPath, active.Log, activeHashes.Log);
        ImageJournalHash.Content = Verified(image.Journal, active.Journal, activeHashes.Journal);
        ImageMftHash.Content = Verified(image.Mft, active.Mft, activeHashes.Mft);
        ImageLogHash.Content = Verified(image.Log, active.Log, activeHashes.Log);
        ImageArtifactLayout.Orientation = ImageJournalHash.Content != null || ImageMftHash.Content != null || ImageLogHash.Content != null
            ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void RefreshState()
    {
        RefreshSourceRows();
        if (imageSession == null || pending != null)
            RefreshVerifiedHashes();
        bool empty = !showFileRows && imageSession == null && pending == null;
        if (pickingFirstSource != empty)
        {
            pickingFirstSource = empty;
            if (empty)
                ChooseSourcesButton.SetResourceReference(StyleProperty, "Desk.FormPrimary");
            else
                ChooseSourcesButton.SetResourceReference(StyleProperty, "Desk.FormButton");
        }
        bool assigning = pending != null;
        StartAnalysis.Visibility = assigning ? Visibility.Collapsed : Visibility.Visible;
        AnalysisOptionsPanel.IsEnabled = !applying && !assigning;
        if (assigning)
        {
            CheckAssignments();
            StartAnalysis.IsEnabled = false;
            StartHint.Text = "";
            return;
        }
        if (imageSession != null)
        {
            RefreshImageState();
            return;
        }
        StartAnalysis.ToolTip = null;
        bool hasJournal = !string.IsNullOrWhiteSpace(JournalPath.Text);
        bool hasLog = !string.IsNullOrWhiteSpace(LogPath.Text);
        bool needsLog = !hasJournal && !hasLog && !string.IsNullOrWhiteSpace(MftPath.Text);
        StartAnalysis.IsEnabled = !applying && (hasJournal || hasLog);
        bool unchanged = (hasJournal || hasLog) && (!hasJournal || JournalHash.Content != null)
            && (string.IsNullOrWhiteSpace(MftPath.Text) || MftHash.Content != null) && (!hasLog || LogHash.Content != null)
            && (JournalPath.Text, MftPath.Text, LogPath.Text) == active;
        StartAnalysis.Content = unchanged ? L.Caption("ReturnToRecords") : L.Caption("IntakeStart");
        StartHint.Text = needsLog ? L.Caption("IntakeMftNeedsLog")
            : "";
    }

    internal void ShowError(string message)
    {
        RetryRemaining.Visibility = Visibility.Collapsed;
        RetryRemaining.Tag = null;
        IntakeError.Text = message;
        IntakeError.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static readonly string[] JournalNames = ["$J", "$UsnJrnl-$J", "$UsnJrnl_$J", "$UsnJrnl%3A$J"];
    private static bool IsJournal(string name) => JournalNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal void SelectFolder(string folder)
    {
        try
        {
            var files = KnownFiles(folder);
            if (files.Length == 0)
            {
                ShowError(L.T("이 폴더에서 $J, $MFT 또는 $LogFile을 찾지 못했습니다. ‘파일 선택…’에서 파일을 직접 선택하세요.", "No $J, $MFT, or $LogFile was found in this folder. Use Select files… to choose the files directly."));
                return;
            }
            StageAssignments(files.Select(p => new Assignment(p, FileRole(p))).ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ShowError(ex.Message); }
    }
    private string? SourceDirectory() =>
        new[] { JournalPath.Text, MftPath.Text, LogPath.Text }
            .Select(p => Path.GetDirectoryName(p.Trim())).FirstOrDefault(Directory.Exists);

    private void PickInputFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = L.Caption("IntakeFromFolder"), InitialDirectory = SourceDirectory() ?? "" };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
            SelectFolder(picker.FolderName);
    }

    private void BrowseSource(object sender, RoutedEventArgs e)
    {
        var (box, title) = ((Button)sender).Tag switch
        {
            "journal" => (JournalPath, "IntakePickJournal"),
            "mft" => (MftPath, "IntakePickMft"),
            _ => (LogPath, "IntakePickLog")
        };
        var picker = new OpenFileDialog
        {
            Title = L.Caption(title),
            InitialDirectory = (File.Exists(box.Text) ? Path.GetDirectoryName(box.Text) : SourceDirectory()) ?? "",
            Filter = L.T("모든 파일|*.*", "All files|*.*"),
            CheckFileExists = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
        {
            AutomationProperties.SetHelpText(box, "");
            unverifiedPaths.Add(box);
            SetPathText(box, picker.FileName);
            RefreshState();
        }
    }

    private async void Start(object sender, RoutedEventArgs e) => await StartAsync();
    private async Task StartAsync()
    {
        if (applying || !StartAnalysis.IsEnabled || AnalysisRequested is not { } analyze)
            return;
        if (!CommitStoragePath(IndexPath) || !CommitStoragePath(OutputPath) || (imageSession != null && !CommitStoragePath(ExtractionPath)))
            return;
        applying = true;
        ShowError("");
        RefreshState();
        try
        {
            if (imageSession != null)
                await AnalyzeImage(analyze);
            else
                await analyze(JournalPath.Text.Trim(), MftPath.Text.Trim(), LogPath.Text.Trim());
        }
        catch (OperationCanceledException) { ShowError(L.T("분석을 취소했습니다.", "Analysis cancelled.")); }
        catch (IntakeSourceException ex)
        {
            ShowError(ex.Message);
            bool journalAvailable = imageSession == null ? !string.IsNullOrWhiteSpace(JournalPath.Text) : IncludeImageJournal.IsChecked == true;
            bool logAvailable = imageSession == null ? !string.IsNullOrWhiteSpace(LogPath.Text) : IncludeImageLog.IsChecked == true;
            if ((ex.Role != "$J" && journalAvailable) || (ex.Role != "$LogFile" && logAvailable))
            {
                RetryRemaining.Tag = ex.Role;
                RetryRemaining.Content = L.T("{0} 제외하고 분석", "Analyze without {0}", ex.Role);
                RetryRemaining.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { applying = false; RefreshState(); }
    }
    private async void RetryOtherSources(object sender, RoutedEventArgs e)
    {
        if (applying || RetryRemaining.Tag is not string role)
            return;
        if (imageSession != null)
        {
            if (role == "$J")
                IncludeImageJournal.IsChecked = false;
            if (role == "$MFT")
                IncludeImageMft.IsChecked = false;
            if (role == "$LogFile")
                IncludeImageLog.IsChecked = false;
        }
        else
        {
            if (role == "$J")
                JournalPath.Clear();
            if (role == "$MFT")
                MftPath.Clear();
            if (role == "$LogFile")
                LogPath.Clear();
        }
        RefreshState();
        await StartAsync();
    }
}
