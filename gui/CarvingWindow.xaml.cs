using System.ComponentModel;
using System.IO;
using EvidenceImage;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace JournalDesk;

public partial class CarvingWindow : Window
{
    private sealed class Result(string source, string name, string summary, string? db, string? hash) : INotifyPropertyChanged
    {
        public string Source { get; } = source;
        public string Name { get; } = name;
        public string Summary { get; } = summary;
        public string? Db { get; } = db;
        public string? Hash { get; } = hash;
        private string folder = "";
        public string Folder
        {
            get => folder;
            set
            {
                if (folder != value)
                {
                    folder = value;
                    PropertyChanged?.Invoke(this, new(nameof(Folder)));
                    PropertyChanged?.Invoke(this, new(nameof(ShowFolder)));
                }
            }
        }
        public bool ShowFolder => Folder.Length != 0;
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static string Cache => DeskSettings.IndexStore;
    private readonly CancellationToken closing;
    private readonly string? loadedJournal;
    private CancellationTokenSource? scan, selection;
    private FileStream? selectedFile;
    public CarvingWindow(string? loadedJournal = null)
    {
        InitializeComponent();
        this.loadedJournal = loadedJournal;
        ScanOpenSource.Visibility = loadedJournal == null ? Visibility.Collapsed : Visibility.Visible;
        ScanOpenSource.ToolTip = loadedJournal;
        closing = WindowLifetime.UntilClosed(this);
        PickFolder.ToolTip = L.T("선택한 폴더 바로 아래의 파일을 검사합니다.", "Scan files directly inside the selected folder.");
        SourceMetadata.ToolTip = L.Caption("ScanSourcePathHash");
        Closed += (_, _) => selectedFile?.Dispose();
    }
    private async void ScanLoaded(object sender, RoutedEventArgs e)
    {
        if (loadedJournal != null)
            await Inspect([loadedJournal]);
    }

    private async void AddFiles(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Multiselect = true, Title = L.T("USN 흔적을 검색할 원본 선택", "Select source files to scan for USN records") };
        if (d.ShowDialog(this) == true)
            await Inspect(d.FileNames);
    }
    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = L.T("검사할 파일이 들어 있는 폴더 선택 (바로 아래 파일만 검색)", "Select a folder to scan (top-level files only)") };
        if (d.ShowDialog(this) != true)
            return;
        try
        {
            await Inspect(Directory.GetFiles(d.FolderName));
        }
        catch (Exception ex) { State.Text = L.T("폴더 목록을 열 수 없습니다. ", "Could not list the folder. ") + ex.Message; }
    }
    private void StopScan(object sender, RoutedEventArgs e) => scan?.Cancel();
    private static FileStream Read(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static void DistinguishFolders(List<Result> sameName)
    {
        var folders = sameName.Select(x => (Path.GetDirectoryName(x.Source) ?? "").Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        for (int depth = 1; depth <= folders.Max(x => x.Length); depth++)
        {
            var labels = folders.Select(x => string.Join(Path.DirectorySeparatorChar, x.TakeLast(depth))).ToArray();
            if (labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Length)
                continue;
            for (int i = 0; i < labels.Length; i++)
                sameName[i].Folder = labels[i];
            return;
        }
    }
    private async Task Inspect(string[] paths)
    {
        if (scan != null)
            return;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(closing);
        scan = running;
        ScanOpenSource.IsEnabled = PickFiles.IsEnabled = PickFolder.IsEnabled = false;
        Stop.Visibility = Visibility.Visible;
        int done = 0;
        try
        {
            Directory.CreateDirectory(Cache);
            foreach (var path in paths)
            {
                running.Token.ThrowIfCancellationRequested();
                string leaf = Path.GetFileName(path);
                State.Text = L.T("{0:N0} / {1:N0}   ·   {2} 검사 중", "{0:N0} / {1:N0}   ·   Scanning {2}", done + 1, paths.Length, leaf);
                Result item;
                try
                {
                    using var file = Read(path);
                    string hash = await EvidenceHash.Sha256Async(file, running.Token);
                    string db = Path.Combine(Cache, hash + ".carve2.sqlite");
                    var info = await IndexCache.OpenAsync(db, temp => EngineProcess.RunAsync("carve", path, temp, running.Token),
                        temp => new Journal(temp).Summary(carved: true), stats =>
                        {
                            if (stats.Hash != hash)
                                throw new InvalidDataException(L.T("원본과 색인의 해시가 다릅니다.", "The source and index hashes differ."));
                        }, running.Token);
                    item = new(path, leaf, L.T("후보 {0:N0}건", "Candidates: {0:N0}", info.Records), db, hash);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item = new(path, leaf, L.T("검사 실패 · ", "Scan failed · ") + ex.Message, null, null);
                }
                var prior = Sources.Items.OfType<Result>().FirstOrDefault(s => StringComparer.OrdinalIgnoreCase.Equals(s.Source, path));
                bool reselect = prior != null && ReferenceEquals(Sources.SelectedItem, prior);
                if (prior != null)
                    Sources.Items.Remove(prior);
                var duplicates = Sources.Items.OfType<Result>().Where(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, leaf)).ToList();
                if (duplicates.Count > 0)
                {
                    duplicates.Add(item);
                    DistinguishFolders(duplicates);
                }
                Sources.Items.Add(item);
                if (reselect || Sources.SelectedItem == null)
                    Sources.SelectedItem = item;
                done++;
            }
            State.Text = L.T("검사한 자료 {0:N0}개", "Sources scanned: {0:N0}", done);
        }
        catch (OperationCanceledException) { State.Text = L.T("검사를 중단했습니다. 완료한 자료 {0:N0}개의 결과를 볼 수 있습니다.", "Scan stopped. Results from {0:N0} completed sources are available.", done); }
        catch (Exception ex) { State.Text = L.T("검사를 진행할 수 없습니다. ", "Could not scan. ") + ex.Message; }
        finally { scan = null; ScanOpenSource.IsEnabled = PickFiles.IsEnabled = PickFolder.IsEnabled = true; Stop.Visibility = Visibility.Collapsed; }
    }
    private async void Selected(object sender, SelectionChangedEventArgs e)
    {
        selection?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(closing);
        selection = request;
        FileStream? file = null;
        try
        {
            Results.SetSource(null, null);
            Results.Visibility = Visibility.Collapsed;
            SourceMetadata.Visibility = Visibility.Collapsed;
            SourceMetadata.IsExpanded = false;
            selectedFile?.Dispose();
            selectedFile = null;
            ResultNotice.Visibility = Visibility.Visible;
            ResultNotice.Text = "";
            if (Sources.SelectedItem is not Result r)
                return;
            MetadataValue.Text = r.Source + (r.Hash == null ? "" : "\nSHA-256  " + r.Hash + "\n" +
                L.Caption("ScanUsnSourceOffsetNote"));
            SourceMetadata.Visibility = Visibility.Visible;
            if (r.Db == null)
            {
                ResultNotice.Text = r.Summary;
                return;
            }
            ResultNotice.Text = L.T("선택한 원본과 색인의 해시를 대조하고 있습니다.", "Verifying the source hash against the index.");
            file = Read(r.Source);
            var digest = await EvidenceHash.Sha256Async(file, request.Token);
            if (!StringComparer.OrdinalIgnoreCase.Equals(digest, r.Hash))
                throw new InvalidDataException(L.T("마지막 검사 이후 원본이 변경됐습니다. 다시 검사하세요.", "The source changed after the last scan. Scan again."));
            request.Token.ThrowIfCancellationRequested();
            selectedFile = file;
            file = null;
            ResultNotice.Visibility = Visibility.Collapsed;
            Results.Visibility = Visibility.Visible;
            Results.SetSource(new Journal(r.Db), null, r.Source);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) ResultNotice.Text = ex.Message; }
        finally { file?.Dispose(); if (selection == request) selection = null; }
    }
}
