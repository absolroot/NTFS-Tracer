using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

internal sealed record SourceInfo(string Title, string SourcePath, string Digest, string Result, string Diagnostic, string Engine)
{
    internal static SourceInfo Read(string path, string database, string expectedHash, bool mft)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        EngineIndexFormat.Validate(db, mft ? EngineArtifact.Mft : EngineArtifact.Journal);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA application_id";
        bool hasManifest = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        cmd.CommandText = "PRAGMA user_version";
        long schema = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        string engine = L.T("색인 형식 {0} · 생성 엔진 버전 미기록", "Index format {0} · Engine version not recorded", schema);
        if (hasManifest)
        {
            cmd.CommandText = "SELECT engine_version FROM artifact_meta";
            engine = L.T("색인 형식 {0} · Rust 엔진 {1}", "Index format {0} · Rust engine {1}", schema, cmd.ExecuteScalar());
        }
        cmd.CommandText = mft ? "SELECT hash,bytes,records,skipped FROM mft_summary" : "SELECT hash,bytes,records,skipped FROM summary";
        long length, count, warnings;
        using (var row = cmd.ExecuteReader())
        {
            if (!row.Read() || !StringComparer.OrdinalIgnoreCase.Equals(row.GetString(0), expectedHash))
                throw new InvalidDataException(L.T("연결한 원본과 색인 요약의 SHA-256이 일치하지 않습니다.", "The source SHA-256 differs from the index summary."));
            length = row.GetInt64(1);
            count = row.GetInt64(2);
            warnings = row.GetInt64(3);
        }
        var diagnostic = warnings == 0 ? L.T("없음", "None") : warnings.ToString("N0", CultureInfo.CurrentCulture);
        if (warnings > 0)
        {
            cmd.CommandText = mft
                ? "SELECT pos,reason FROM mft_warnings ORDER BY pos LIMIT 5"
                : "SELECT pos,kind,size FROM warnings ORDER BY pos LIMIT 5";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
                diagnostic += $"\n0x{rows.GetInt64(0):X}  ·  {EngineDiagnostic.Display(rows.GetString(1))}" + (mft ? "" : L.T("  ·  {0:N0}바이트", "  ·  {0:N0} bytes", rows.GetInt64(2)));
            if (warnings > 5)
                diagnostic += L.T("\n외 {0:N0}건", "\n{0:N0} more", warnings - 5);
        }
        return new(mft ? "Master File Table ($MFT)" : "USN Journal ($J)", path, expectedHash,
            L.T("{0:N0}바이트  ·  {1} {2:N0}건", "{0:N0} bytes  ·  {1}: {2:N0}", length, (mft ? L.T("기본 MFT 항목", "Base MFT entries") : L.T("변경 기록", "Change records")), count), diagnostic, engine);
    }
}

public partial class SourceInfoWindow : Window
{
    private readonly IReadOnlyList<SourceInfo> records;
    internal SourceInfoWindow(IReadOnlyList<SourceInfo> records, string? pairingNote)
    {
        InitializeComponent();
        this.records = records;
        Sources.ItemsSource = records;
        PairNote.Text = pairingNote ?? "";
        PairNote.Visibility = string.IsNullOrEmpty(pairingNote) ? Visibility.Collapsed : Visibility.Visible;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
    }
    private void CopyInfo(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join("\n\n", records.Select(x => L.T("{0}\n{1}\nSHA-256  {2}\n{3}\n판독 경고  {4}\n{5}", "{0}\n{1}\nSHA-256  {2}\n{3}\nRead warnings  {4}\n{5}", x.Title, x.SourcePath, x.Digest, x.Result, x.Diagnostic, x.Engine))) + "\n\n" + PairNote.Text);
            Feedback.Text = L.T("원본 정보를 복사했습니다.", "Copied source details.");
        }
        catch (System.Runtime.InteropServices.ExternalException) { Feedback.Text = L.T("클립보드를 사용할 수 없습니다.", "Clipboard unavailable."); }
    }
}
