using System.IO;
using EvidenceImage;
using System.Windows;

namespace JournalDesk;

internal sealed class IntakeSourceException(string role, string message, Exception? inner = null) : IOException(message, inner)
{
    internal string Role { get; } = role;
}

public partial class MainWindow
{
    private FileStream? mftEvidence;
    private bool showingIndexResults, incompleteIntake;
    private sealed record PreparedJournal(Journal Index, string Hash, long Files, long Records, long Warnings);
    private PreparedJournal? activeJournalIndex;
    private sealed record PreparedMftIndex(string Database, string Hash, long Records, long Skipped);
    private static FileStream OpenEvidence(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private void SynchronizeSettings(bool confirmed = false)
    {
        if (Setup == null || Transactions == null)
            return;
        string? journalHash = source != null && (confirmed || MatchesSource(source, source.Name)) ? digest : null;
        string? mftHash = mft != null && (confirmed || MatchesSource(mftEvidence, mft.SourcePath)) ? mft.Hash : null;
        string? logHash = confirmed || Transactions.MatchesSource(Transactions.SourcePath) ? Transactions.SourceHash : null;
        Setup.Synchronize(source?.Name, mft?.SourcePath, Transactions.SourcePath, (journalHash, mftHash, logHash), confirmed, incompleteIntake && !confirmed);
    }

    internal static bool MatchesSource(FileStream? held, string? path)
    {
        if (held == null || path == null)
            return held == null && path == null;
        if (!string.Equals(held.Name, path, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var candidate = OpenEvidence(path);
            return SourceIdentity.SameFile(held, candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? InputPath(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string path = Path.GetFullPath(value);
        if (!File.Exists(path))
            throw new IntakeSourceException(role, L.T("{0} 파일을 찾을 수 없습니다.\n{1}", "The {0} file could not be found.\n{1}", role, path));
        return path;
    }

    private async Task<(bool Changed, bool Previewed)> ApplyIntakeAsync(string journal, string mft, string logFile, bool allowPreview = true)
    {
        if (busy || !Transactions.CanReplaceSource)
            throw new InvalidOperationException(L.T("진행 중인 작업을 마친 뒤 다시 시도해 주세요.", "Wait for the current operation to finish, then try again."));
        string? journalPath = InputPath(journal, "$J"), mftPath = InputPath(mft, "$MFT"), logPath = InputPath(logFile, "$LogFile");
        if (journalPath == null && logPath == null && mftPath != null)
            throw new InvalidDataException(L.Caption("IntakeMftNeedsLog"));
        if (journalPath == null && logPath == null)
            throw new InvalidDataException(L.T("$J 또는 $LogFile을 선택해 주세요.", "Select a $J or $LogFile."));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { journalPath, mftPath, logPath })
            if (path != null && !paths.Add(path))
                throw new InvalidDataException(L.T("같은 파일을 여러 종류의 자료로 선택했습니다. 각 자료의 파일을 확인해 주세요.", "The same file was selected for more than one source. Check the file for each source."));

        bool changeJournal = !MatchesSource(source, journalPath);
        bool changeMft = changeJournal || !MatchesSource(mftEvidence, mftPath);
        bool changeLog = !Transactions.MatchesSource(logPath);
        if (!changeJournal && !changeMft && !changeLog)
        {
            incompleteIntake = false;
            return (false, false);
        }
        using var stop = new CancellationTokenSource();
        import = stop;
        SetBusy(true);
        FileStream? newJournal = null, newMft = null, newLog = null;
        bool previewed = false;
        try
        {
            PreparedJournal? preparedJournal = null;
            PreparedMftIndex? mftIndex = null;
            MftAttachment? preparedMft = null;
            TransactionView.PreparedSource? preparedLog = null;
            int limit = Setup.GetIndexingLimit();
            var jobs = new List<(string Role, Func<Task> Prepare)>();
            Action<string>? progress = text => Status.Text = text;
            if (changeJournal && journalPath != null)
                jobs.Add(("$J", async () =>
                {
                    newJournal = OpenEvidence(journalPath);
                    preparedJournal = await PrepareJournalAsync(newJournal, progress, stop.Token);
                    if (!allowPreview || closed || jobs.Count < 2 || !SettingsTab.IsSelected)
                        return;
                    stop.Token.ThrowIfCancellationRequested();
                    SetPreparedJournal(newJournal, preparedJournal);
                    newJournal = null;
                    if (changeLog)
                        Transactions.SetPreparedSource(null, null);
                    showingIndexResults = incompleteIntake = previewed = true;
                    SetBusy(true);
                    GlobalTab.IsSelected = true;
                    CloseStartupSettingsForResults();
                    SettingsTab.IsEnabled = false;
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, stop.Token);
                }
                ));
            if (changeMft && mftPath != null)
                jobs.Add(("$MFT", async () =>
                {
                    newMft = OpenEvidence(mftPath);
                    mftIndex = await PrepareMftIndexAsync(newMft, null, progress, stop.Token);
                }
                ));
            if (changeLog && logPath != null)
                jobs.Add(("$LogFile", async () =>
                {
                    newLog = OpenEvidence(logPath);
                    preparedLog = await TransactionView.PrepareSourceAsync(newLog, progress, stop.Token, Setup.GetVerifiedSha256(newLog));
                }
                ));
            bool concurrent = limit > 1 && jobs.Count > 1;
            if (jobs.Count > 1)
                progress = null;
            await PrepareIndexesAsync(jobs, limit, stop, roles =>
            {
                if ((concurrent || showingIndexResults) && roles.Count > 0)
                    Status.Text = L.T("색인 준비 중 · {0}", "Preparing indexes · {0}", string.Join(" · ", roles));
            });
            if (mftIndex != null)
            {
                try
                {
                    preparedMft = await AttachMftIndexAsync(newMft!, changeJournal ? preparedJournal?.Index : this.journal, mftIndex, stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw SourceError("$MFT", ex); }
            }
            stop.Token.ThrowIfCancellationRequested();
            if (changeJournal && !previewed)
            {
                SetPreparedJournal(newJournal, preparedJournal);
                newJournal = null;
            }
            if (changeLog)
            {
                Transactions.SetPreparedSource(newLog, preparedLog);
                newLog = null;
            }
            if (changeMft)
            {
                SetPreparedMft(newMft, preparedMft);
                newMft = null;
                UpdateAttachmentStatus(true);
            }
            else if (changeLog)
                RefreshTransactionMft();
            await RefreshVisibleHistoryAsync();
            if (TransactionTab.IsSelected)
                await Transactions.RefreshPreparedSourceAsync();
            if (!changeMft)
                Status.Text = summaryText;
            incompleteIntake = false;
            return (true, previewed);
        }
        catch (OperationCanceledException)
        {
            Status.Text = previewed
                ? summaryText + L.T("   ·   나머지 색인 작업 취소", "   ·   Remaining indexes cancelled")
                : summaryText;
            throw;
        }
        catch (IntakeSourceException ex)
        {
            Status.Text = previewed ? summaryText + "   ·   " + ex.Message : summaryText;
            throw;
        }
        finally
        {
            newJournal?.Dispose();
            newMft?.Dispose();
            newLog?.Dispose();
            import = null;
            showingIndexResults = false;
            SetBusy(false);
        }
    }

    private static IntakeSourceException SourceError(string role, Exception ex) =>
        new(role, L.T("{0} 파일을 열지 못했습니다. {1}", "Could not open {0}. {1}", role, ex.Message), ex);

    private static async Task PrepareIndexesAsync(List<(string Role, Func<Task> Prepare)> jobs, int limit,
        CancellationTokenSource stop, Action<List<string>> changed)
    {
        using var slots = new SemaphoreSlim(limit);
        var active = new List<string>(limit);
        async Task RunAsync((string Role, Func<Task> Prepare) job)
        {
            await slots.WaitAsync(stop.Token);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                active.Add(job.Role);
                changed(active);
                await job.Prepare();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stop.Cancel();
                throw SourceError(job.Role, ex);
            }
            finally
            {
                active.Remove(job.Role);
                changed(active);
                slots.Release();
            }
        }
        await Task.WhenAll(jobs.Select(RunAsync));
    }

    private async Task<PreparedJournal> PrepareJournalAsync(FileStream input, Action<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Cache);
        progress?.Invoke(L.T("원본 파일의 SHA-256 해시를 확인하고 있습니다…", "Verifying the source file’s SHA-256 hash…"));
        string hash = Setup.GetVerifiedSha256(input) ?? await EvidenceHash.Sha256Async(input, token);
        string db = Path.Combine(Cache, hash + ".engine3.sqlite");
        var info = await IndexCache.OpenAsync(db, path => BuildIndex(input.Name, path, false, progress, token),
            path => new Journal(path).Summary(), stats =>
            {
                if (stats.Hash != hash)
                    throw new InvalidDataException(L.T("저장된 분석 결과의 원본 해시가 일치하지 않습니다.", "The index hash does not match the selected source."));
            }, token);
        var index = new Journal(db);
        token.ThrowIfCancellationRequested();
        return new(index, hash, info.Files, info.Records, info.Warnings);
    }

    private void RefreshJournalSummary()
    {
        if (activeJournalIndex is not { } prepared)
        {
            summaryText = "";
            return;
        }
        string summary = L.T("저널에 기록된 파일·폴더 {0:N0}개   ·   변경 기록 {1:N0}건", "Files and folders: {0:N0}   ·   Change records: {1:N0}", prepared.Files, prepared.Records);
        if (prepared.Warnings > 0)
            summary += L.T("   ·   손상 또는 미지원 구간 {0:N0}개", "   ·   Damaged or unsupported regions: {0:N0}", prepared.Warnings);
        if (prepared.Records == 0)
            summary += L.T("   ·   읽을 수 있는 V2·V3·V4 변경 기록이 없습니다.", "   ·   No readable V2, V3, or V4 change records.");
        summaryText = summary;
    }

    private void SetPreparedJournal(FileStream? input, PreparedJournal? prepared)
    {
        (UsnLeads.Content as InvestigationView)?.CloseWithOwner();
        UsnLeads.Content = null;
        BackToLeads.Visibility = Visibility.Collapsed;
        source?.Dispose();
        incompleteIntake = false;
        source = input;
        journal = prepared?.Index;
        digest = prepared?.Hash ?? "";
        mftEvidence?.Dispose();
        mftEvidence = null;
        mft = null;
        RefreshTransactionMft();
        activeJournalIndex = prepared;
        RefreshJournalSummary();
        Status.Text = summaryText;
        jumpVersion++;
        queryVersion++;
        historyRefreshPending = journal != null;
        listedVersion = -1;
        emptySearchBookmark = null;
        focusSearchResult = false;
        BackToLog.Visibility = Visibility.Collapsed;
        FileList.ItemsSource = null;
        ResetSelection();
        Search.Clear();
        typing.Stop();
        page = 0;
        GlobalTab.IsEnabled = journal != null;
        UpdateLeadSources();
        Welcome.Visibility = journal == null ? Visibility.Visible : Visibility.Collapsed;
        Transactions.SetDirectory(input == null ? null : Path.GetDirectoryName(input.Name));
        SetHistoryPathSource();
        UpdateSourceLabel();
        UpdateGlobalSource();
    }

    private async Task<PreparedMftIndex> PrepareMftIndexAsync(FileStream input, string? expected, Action<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Cache);
        progress?.Invoke(L.T("연결할 MFT의 SHA-256 해시를 확인하고 있습니다…", "Verifying the SHA-256 hash of the selected MFT…"));
        string hash = Setup.GetVerifiedSha256(input) ?? await EvidenceHash.Sha256Async(input, token);
        if (expected != null && expected != hash)
            throw new InvalidDataException(L.T("이전 MFT와 파일 내용이 달라졌습니다. 같은 볼륨의 자료인지 확인한 뒤 다시 선택해 주세요.", "This $MFT differs from the previous source. Verify that it came from the same volume, then select it again."));
        string db = Path.Combine(Cache, hash + ".mft1.sqlite");
        var info = await IndexCache.OpenAsync(db, path =>
        {
            progress?.Invoke(L.T("파일 및 폴더 이름을 읽고 있습니다…", "Reading file and folder names…"));
            return BuildIndex(input.Name, path, true, progress, token);
        }, MftAttachment.Summary, stats =>
        {
            if (stats.Hash != hash)
                throw new InvalidDataException(L.T("MFT 색인이 선택한 원본과 일치하지 않습니다.", "The MFT index does not match the selected source."));
        }, token);
        return new(db, hash, info.Records, info.Skipped);
    }

    private static async Task<MftAttachment> AttachMftIndexAsync(FileStream input, Journal? selected, PreparedMftIndex index, CancellationToken token)
    {
        var (paths, warning) = await Task.Run(() => (new PathResolver(selected?.DbPath, index.Database), selected?.CheckPair(index.Database)), token);
        token.ThrowIfCancellationRequested();
        return new(input.Name, index.Hash, index.Records, index.Skipped, paths, warning, index.Database);
    }

    private void SetPreparedMft(FileStream? input, MftAttachment? attachment)
    {
        mftEvidence?.Dispose();
        mftEvidence = input;
        mft = attachment;
        RefreshTransactionMft();
        historyRefreshPending = journal != null;
        fileVersion++;
        SetHistoryPathSource();
        UpdateSourceLabel();
        UpdateGlobalSource();
    }

    private void RefreshTransactionMft() =>
        Transactions.SetMftPaths(mft?.Paths, mft?.SourcePath, mft?.Hash);
    private void UpdateAttachmentStatus(bool remember)
    {
        string note = mft?.Skipped > 0 ? L.T("{0}   ·   읽지 못한 MFT 레코드 {1:N0}개", "{0}   ·   Unreadable MFT records: {1:N0}", summaryText, mft.Skipped) : summaryText;
        if (remember && source != null)
        {
            try
            {
                if (mft != null)
                    mft.WriteLink(LinkFile, source.Name);
                else if (MftAttachment.ReadLink(LinkFile, source.Name) != null)
                    File.Delete(LinkFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                note += L.T("   ·   다음 실행을 위한 연결 정보는 저장하지 못했습니다.", "   ·   Could not save the attachment for the next session.");
            }
        }
        Status.Text = note;
    }
}
