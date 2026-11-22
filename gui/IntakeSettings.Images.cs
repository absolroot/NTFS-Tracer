using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EvidenceImage;

namespace JournalDesk;

public partial class IntakeSettings
{
    internal MainWindow? Host
    {
        private get; set;
    }
    private JournalImageSession? imageSession;
    private string? ImagePath => imageSession?.ImagePath;
    private ExtractionResult? Extraction => imageSession?.Extraction;
    internal string? GetVerifiedSha256(FileStream input) => imageSession?.GetVerifiedSha256(input);
    private static string DefaultExtractionRoot => Path.Combine(DeskSettings.DirectoryPath, "extractions");
    private sealed record VolumeChoice(ImageVolume Volume, string Title);
    internal async Task OpenImageAsync(string path)
    {
        if (Host is not { } main)
            return;
        JournalImageSession? opened = null;
        try
        {
            ShowError("");
            opened = await main.ReadImageAsync(L.T("이미지에서 볼륨과 원본을 찾고 있습니다…", "Looking for volumes and artifacts in the image…"),
                ct => JournalImageSession.OpenAsync(path, ct));
            var probe = opened.Probe;
            if (string.Equals(opened.ImagePath, ImagePath, StringComparison.OrdinalIgnoreCase) &&
                ImageVolumes.SelectedItem is VolumeChoice current && probe.Volumes.Any(v => v.Id == current.Volume.Id))
            {
                EndAssignments();
                RefreshState();
                return;
            }
            EndAssignments();
            ClearImage();
            imageSession = opened;
            opened = null;
            SetPathText(ImageFilePath, imageSession.ImagePath);
            var choices = probe.Volumes.Select(v => new VolumeChoice(v, VolumeTitle(probe.Format, v))).ToList();
            ImageVolumes.ItemsSource = choices;
            ImageVolumes.SelectedIndex = choices.Count > 0 ? 0 : -1;
            ImageWarnings.Text = string.Join(Environment.NewLine, probe.Warnings.Select(ImageMessage));
            ImageWarnings.ToolTip = string.Join(Environment.NewLine, probe.Warnings);
            ImageWarnings.Visibility = probe.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshStoragePaths();
            RefreshState();
        }
        catch (OperationCanceledException) { ShowError(L.T("이미지 열기를 취소했습니다.", "Opening the image was cancelled.")); }
        catch (Exception ex) { ShowError(L.T("‘{0}’을 열지 못했습니다. {1}", "Could not open ‘{0}’. {1}", Path.GetFileName(path), ImageMessage(ex.Message))); }
        finally { opened?.Dispose(); }
    }

    private static string ImageMessage(string message)
    {
        if (L.Code is not ("ko" or "ja"))
            return message;
        return message switch
        {
            "No readable NTFS volume was found." => L.T("이 이미지에서 읽을 수 있는 NTFS 볼륨을 찾지 못했습니다.", "No readable NTFS volume was found."),
            "No collected $J, $MFT or $LogFile entry was identified in this logical image." => L.T("수집 목록에 $J, $MFT, $LogFile이 없습니다.", "No collected $J, $MFT or $LogFile entry was identified in this logical image."),
            "The EWF segment set is damaged or incomplete." => L.T("EWF 분할 파일이 누락되었거나 손상되었습니다.", "The EWF segment set is damaged or incomplete."),
            "Unsupported or damaged image header. For segmented evidence select the first segment or descriptor." => L.T("이미지 헤더를 읽을 수 없습니다. 분할본은 첫 파일을, VMDK는 디스크 설명 파일을 선택하세요.", "Unsupported or damaged image header. For segmented evidence select the first segment or descriptor."),
            "This QCOW image requires a backing file." => L.T("부모 이미지가 필요한 QCOW입니다. 독립형 이미지를 선택하세요.", "This QCOW image requires a backing file."),
            "VMDK snapshots require a verified parent chain." => L.T("부모 디스크가 필요한 VMDK 스냅숏입니다. 독립형 이미지를 선택하세요.", "VMDK snapshots require a verified parent chain."),
            "Select the first EWF segment." => L.T("EWF의 첫 번째 분할 파일을 선택하세요.", "Select the first EWF segment."),
            _ => message
        };
    }

    private static string VolumeTitle(string format, ImageVolume volume)
    {
        var label = string.IsNullOrWhiteSpace(volume.Label) ? format : $"{format} · {volume.Label}";
        // Logical evidence has a file tree without a disk address.
        if (volume.Offset < 0)
            return label;
        string size = volume.Length >= (1L << 30) ? $"{volume.Length / (double)(1L << 30):0.##} GiB" : $"{volume.Length / (double)(1 << 20):0.##} MiB";
        return L.T("{0}   ·   {1}   ·   시작 0x{2:X}", "{0}   ·   {1}   ·   Start 0x{2:X}", label, size, volume.Offset);
    }

    internal void ReleaseImage()
    {
        imageSession?.Dispose();
        imageSession = null;
    }
    private void ClearImage()
    {
        if (imageSession == null)
            return;
        ReleaseImage();
        ImageManifest.Visibility = Visibility.Collapsed;
        ImageVolumes.ItemsSource = null;
        RefreshStoragePaths();
        RefreshState();
    }
    private void UseFiles(object sender, RoutedEventArgs e)
    {
        var copies = ImageVolumes.SelectedItem is VolumeChoice choice && choice.Volume.Id == imageSession?.ExtractedVolume ? Extraction : null;
        bool verified = copies != null && imageSession!.CanReuse(imageSession.ExtractedVolume, ExtractionRoot(), SelectedKeys());
        string journal = IncludeImageJournal.IsChecked == true ? copies?.JournalPath ?? "" : "";
        string mft = IncludeImageMft.IsChecked == true ? copies?.MftPath ?? "" : "";
        string log = IncludeImageLog.IsChecked == true ? copies?.LogFilePath ?? "" : "";
        ClearImage();
        SetSources(journal, mft, log);
        if (verified)
        {
            unverifiedPaths.Clear();
            RefreshState();
        }
        ShowError("");
        JournalPath.Focus();
    }
    private void ImageVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (imageSession == null)
            return;
        var keys = (ImageVolumes.SelectedItem as VolumeChoice)?.Volume.Artifacts ?? Array.Empty<string>();
        foreach (var (box, key) in new[] { (IncludeImageJournal, "journal"), (IncludeImageMft, "mft"), (IncludeImageLog, "logFile") })
        {
            box.Visibility = keys.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
            box.IsChecked = keys.Contains(key);
        }
        ShowError("");
        RefreshState();
    }
    private void ImageSelectionChanged(object sender, RoutedEventArgs e)
    {
        ShowError("");
        RefreshState();
    }
    private string[] SelectedKeys() => new[] { (IncludeImageJournal, "journal"), (IncludeImageMft, "mft"), (IncludeImageLog, "logFile") }
        .Where(i => i.Item1.Visibility == Visibility.Visible && i.Item1.IsChecked == true).Select(i => i.Item2).ToArray();
    private static string ExtractionRoot() => DeskSettings.ExtractionDirectory is { Length: > 0 } folder ? folder : DefaultExtractionRoot;
    private (string Journal, string Mft, string Log) ImageInputs()
    {
        var copies = Extraction;
        return (IncludeImageJournal.IsChecked == true ? copies?.JournalPath ?? "" : "",
            IncludeImageMft.IsChecked == true && (IncludeImageJournal.IsChecked == true || IncludeImageLog.IsChecked == true) ? copies?.MftPath ?? "" : "",
            IncludeImageLog.IsChecked == true ? copies?.LogFilePath ?? "" : "");
    }
    private bool MatchesExtraction(string j, string m, string l) => Extraction != null &&
        (j, m, l) == ImageInputs();

    private void RefreshImageState()
    {
        var selected = (ImageVolumes.SelectedItem as VolumeChoice)?.Volume;
        var keys = selected?.Artifacts ?? Array.Empty<string>();
        bool hasData = keys.Count > 0;
        var chosen = SelectedKeys();
        bool canAnalyze = selected != null && (chosen.Contains("journal") || chosen.Contains("logFile"));
        bool extracted = imageSession?.CanReuse(selected?.Id, ExtractionRoot(), chosen) == true;
        RefreshVerifiedHashes(extracted);
        ImageArtifacts.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        if (!hasData)
        {
            IncludeImageJournal.Visibility = IncludeImageMft.Visibility = IncludeImageLog.Visibility = Visibility.Collapsed;
        }
        var extraction = Extraction;
        ImageOutputLinks.Visibility = extraction != null && selected?.Id == imageSession?.ExtractedVolume ? Visibility.Visible : Visibility.Collapsed;
        ImageManifest.Visibility = ImageOutputLinks.Visibility;
        ImageManifest.ToolTip = extraction?.ManifestPath;
        ImageOutputFolder.ToolTip = Path.GetDirectoryName(extraction?.ManifestPath);
        StartAnalysis.Content = extracted && canAnalyze && MatchesExtraction(active.Journal, active.Mft, active.Log)
            ? L.Caption("ReturnToRecords") : canAnalyze
            ? (extracted ? L.Caption("IntakeStart") : L.T("추출 후 분석", "Extract and analyze")) : L.T("자료 추출", "Extract files");
        StartAnalysis.IsEnabled = !applying && hasData && chosen.Length > 0 && (!extracted || canAnalyze);
        StartHint.Text = selected == null
            ? ImageVolumes.Items.Count > 0 ? L.T("분석할 대상을 선택하세요.", "Select a volume or collection.") : ImageWarnings.IsVisible ? "" : L.T("추출할 수 있는 대상이 없습니다.", "No supported volume or collection was found.")
            : !hasData ? L.T("이 대상에 분석할 NTFS 원본이 없습니다.", "No NTFS artifacts were found here.")
            : chosen.Length == 0 ? L.T("가져올 자료를 선택하세요.", "Select the artifacts to extract.")
            : chosen.Contains("mft") && !canAnalyze ? L.T("$MFT는 파일로 저장합니다.", "$MFT will be saved as a file.") : "";
        StartAnalysis.ToolTip = L.Caption("ImageAllCopies");
    }

    private async Task AnalyzeImage(Func<string, string, string, Task> analyze)
    {
        if (Host is not { } main || imageSession is not { } session || ImageVolumes.SelectedItem is not VolumeChoice selected)
            return;
        string output = ExtractionRoot();
        var chosen = SelectedKeys();
        if (chosen.Length == 0)
            return;
        if (!session.CanReuse(selected.Volume.Id, output, chosen))
        {
            if (!string.IsNullOrEmpty(DeskSettings.ExtractionDirectory) && !Directory.Exists(output))
                throw new DirectoryNotFoundException(L.T("추출물을 저장할 폴더를 다시 선택하세요.", "Choose an available folder to save the extracted files."));
            await main.ExtractImageAsync(session, selected.Volume.Id, output, chosen);
            RefreshState();
        }
        var inputs = ImageInputs();
        if (inputs.Journal != "" || inputs.Log != "")
            await analyze(inputs.Journal, inputs.Mft, inputs.Log);
    }
    private void RevealImageOutput(object sender, RoutedEventArgs e)
    {
        if (Extraction is not { } extraction)
            return;
        try
        {
            SavedOutput.ShowInFolder(extraction.ManifestPath);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowManifest(object sender, RoutedEventArgs e)
    {
        if (Extraction is not { } extraction)
            return;
        try
        {
            if (!File.Exists(extraction.ManifestPath))
                throw new FileNotFoundException(L.T("추출 기록을 찾을 수 없습니다.", "Extraction manifest not found."), extraction.ManifestPath);
            var app = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            app.ArgumentList.Add(extraction.ManifestPath);
            Process.Start(app);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
