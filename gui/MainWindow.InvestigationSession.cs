using System.IO;
using System.Text.Json;
using System.Windows;
using EvidenceImage;
using Microsoft.Win32;

namespace JournalDesk;

public partial class MainWindow
{
    private const string InvestigationExtension = ".ntfs-tracer-case";
    private const string LastInvestigationFile = "last-investigation.txt";
    private const long MaxInvestigationBytes = 1024 * 1024;

    private sealed record SavedSource(string Path, string Sha256);
    private sealed record SavedInvestigation(int Version, SavedSource? Journal, SavedSource? Mft, SavedSource? Log,
        string View, string Search, long? Position, byte[]? FileId, int Page, int Filter, string? Lsn, bool IncludePreviousNames,
        GlobalLogView.SavedFilters? GlobalFilters, TransactionView.SavedFilters? TransactionFilters);

    private void SaveInvestigation(object sender, RoutedEventArgs e)
    {
        if (busy || !Transactions.CanReplaceSource)
            return;
        if (incompleteIntake || (source == null && Transactions.SourcePath == null))
        {
            Status.Text = L.T("분석을 마친 조사 자료를 연 뒤 저장하세요.", "Open a completed analysis before saving an investigation.");
            return;
        }
        var saved = CaptureInvestigation();
        var picker = new SaveFileDialog { Filter = InvestigationFileFilter(), DefaultExt = InvestigationExtension, AddExtension = true };
        if (picker.ShowDialog(this) != true)
            return;
        try
        {
            WriteInvestigation(picker.FileName, saved);
            RememberInvestigation(picker.FileName);
            Status.Text = L.T("조사를 저장했습니다. {0}", "Investigation saved. {0}", Path.GetFileName(picker.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Report(ex); }
    }

    private SavedInvestigation CaptureInvestigation()
    {
        string view = HistoryTab.IsSelected && source != null ? "history" :
            TransactionTab.IsSelected && Transactions.SourcePath != null ? "transactions" :
            LeadsTab.IsSelected && LogLeads.IsSelected && Transactions.SourcePath != null ? "log-leads" :
            LeadsTab.IsSelected && source != null ? "leads" : source != null ? "records" : "transactions";
        return new SavedInvestigation(1, source == null ? null : new(source.Name, digest),
            mft == null ? null : new(mft.SourcePath, mft.Hash),
            Transactions.SourcePath is { } log && Transactions.SourceHash is { } hash ? new(log, hash) : null,
            view, view == "history" ? Search.Text : view == "transactions" ? Transactions.Query.Text : GlobalView.QueryText.Text,
            view == "history" ? (ProofPicker.SelectedItem as Entry)?.Position : GlobalView.SelectedPosition,
            view == "history" ? current?.Id : null, page,
            view == "transactions" ? Transactions.Filter.SelectedIndex : ActivityFilter.SelectedIndex,
            (Transactions.Rows.SelectedItem as TxRow)?.Lsn, GlobalView.NameScope.IsChecked == true,
            view == "records" ? GlobalView.CaptureFilters() : null,
            view == "transactions" ? Transactions.CaptureFilters() : null);
    }

    private static void WriteInvestigation(string path, SavedInvestigation saved)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved);
        if (bytes.Length > MaxInvestigationBytes)
            throw InvalidInvestigation();
        string temp = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(bytes);
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    internal async void OpenInvestigation(object sender, RoutedEventArgs e)
    {
        if (busy || !Transactions.CanReplaceSource)
            return;
        var picker = new OpenFileDialog { Filter = InvestigationFileFilter() };
        if (picker.ShowDialog((Window?)startupSettings ?? this) == true)
            await OpenInvestigationAsync(picker.FileName);
    }

    internal async void ResumeInvestigation(object sender, RoutedEventArgs e)
    {
        if (busy || !Transactions.CanReplaceSource)
            return;
        try
        {
            if (DeskSettings.Read(LastInvestigationFile) is { } path && File.Exists(path))
                await OpenInvestigationAsync(path);
            else
                OpenInvestigation(sender, e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportInvestigationError(ex); }
    }

    private static string InvestigationFileFilter() => L.T("NTFS Tracer 조사 (*{0})|*{0}",
        "NTFS Tracer investigation (*{0})|*{0}", InvestigationExtension);

    private static InvalidDataException InvalidInvestigation() => new(L.T("지원하는 NTFS Tracer 조사 파일이 아닙니다.",
        "This is not a supported NTFS Tracer investigation file."));

    private static void RememberInvestigation(string path)
    {
        try
        {
            DeskSettings.Write(LastInvestigationFile, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void ReportInvestigationError(Exception ex)
    {
        if (startupSettings != null)
            Setup.ShowError(ex.Message);
        else
            Report(ex);
    }

    private async Task OpenInvestigationAsync(string manifest)
    {
        var held = new List<FileStream>();
        try
        {
            SavedInvestigation saved;
            using (var file = OpenEvidence(manifest))
            {
                if (file.Length > MaxInvestigationBytes)
                    throw InvalidInvestigation();
                saved = JsonSerializer.Deserialize<SavedInvestigation>(file) ?? throw InvalidInvestigation();
            }
            if (saved.Version != 1 || (saved.Journal == null && saved.Log == null) || saved.Search == null ||
                saved.View is not ("history" or "transactions" or "leads" or "log-leads" or "records") ||
                saved.Page < 0 || saved.FileId is { Length: not (8 or 16) } ||
                (saved.View is "transactions" or "log-leads" ? saved.Log == null : saved.Journal == null))
                throw InvalidInvestigation();
            string journalPath, mftPath, logPath;
            using (var stop = new CancellationTokenSource())
            {
                import = stop;
                SetBusy(true);
                Status.Text = L.T("저장한 조사 자료를 확인하고 있습니다…", "Verifying saved investigation sources…");
                try
                {
                    journalPath = await VerifyInvestigationSource(saved.Journal, "$J", held, stop.Token);
                    mftPath = await VerifyInvestigationSource(saved.Mft, "$MFT", held, stop.Token);
                    logPath = await VerifyInvestigationSource(saved.Log, "$LogFile", held, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                }
                finally
                {
                    import = null;
                    if (!closed)
                    {
                        SetBusy(false);
                        Status.Text = summaryText;
                    }
                }
            }
            if (closed)
                return;
            await ApplyIntakeAsync(journalPath, mftPath, logPath, allowPreview: false);
            if (closed)
                return;
            SynchronizeSettings(true);
            var tab = saved.View switch
            {
                "history" => HistoryTab,
                "transactions" => TransactionTab,
                "leads" or "log-leads" => LeadsTab,
                _ => GlobalTab
            };
            tab.IsSelected = true;
            CloseStartupSettingsForResults();
            if (tab == HistoryTab)
            {
                Search.Text = saved.Search;
                typing.Stop();
                page = saved.Page;
                await RefreshFiles(saved.FileId, saved.Position);
                ActivityFilter.SelectedIndex = Math.Clamp(saved.Filter, 0, ActivityFilter.Items.Count - 1);
            }
            else if (tab == GlobalTab)
                GlobalView.ResumeSearch(saved.Search, saved.IncludePreviousNames, saved.Position, saved.GlobalFilters);
            else if (tab == TransactionTab)
                await Transactions.ResumeSearch(saved.Search, saved.Filter, saved.Lsn, saved.TransactionFilters);
            else if (tab == LeadsTab)
                LeadSources.SelectedItem = saved.View == "log-leads" ? LogLeads : UsnLeads;
            RememberInvestigation(manifest);
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { if (!closed) ReportInvestigationError(InvalidInvestigation()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (!closed)
                ReportInvestigationError(ex);
        }
        finally
        {
            foreach (var input in held)
                input.Dispose();
        }
    }

    private async Task<string> VerifyInvestigationSource(SavedSource? item, string role, List<FileStream> held, CancellationToken token)
    {
        if (item == null)
            return "";
        if (item.Path == null || !Path.IsPathFullyQualified(item.Path) ||
            item.Sha256 is not { Length: 64 } || !item.Sha256.All(Uri.IsHexDigit))
            throw InvalidInvestigation();
        string path = item.Path;
        if (!File.Exists(path))
        {
            var picker = new OpenFileDialog
            {
                Title = L.T("이동한 {0} 원본 찾기", "Locate the moved {0} source", role),
                FileName = Path.GetFileName(path)
            };
            if (picker.ShowDialog((Window?)startupSettings ?? this) != true)
                throw new OperationCanceledException(token);
            path = picker.FileName;
        }
        var input = OpenEvidence(path);
        held.Add(input);
        string hash = await EvidenceHash.Sha256Async(input, token);
        if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("저장한 {0} 원본과 SHA-256 해시가 다릅니다.\n{1}",
                "The SHA-256 hash does not match the saved {0} source.\n{1}", role, path));
        return path;
    }
}
