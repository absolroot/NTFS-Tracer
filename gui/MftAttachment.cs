using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace JournalDesk;

internal sealed record MftAttachment(string SourcePath, string Hash, long Records, long Skipped, PathResolver Paths, string? PairWarning, string Database)
{
    internal sealed record Link(string JournalPath, string MftPath, string Hash);

    internal List<EvidenceField> NameEvidence(byte[] fileId, string preferredName)
    {
        var fields = new List<EvidenceField> { new(L.T("MFT 원본 파일", "MFT source file"), SourcePath), new("MFT SHA-256", Hash) };
        if (PairWarning != null)
            fields.Add(new(L.T("자료 연결 주의", "Source pairing warning"), PairWarning));
        List<EvidenceField> WithNote(string note)
        {
            fields.Add(new(L.T("MFT 판독", "MFT lookup"), note));
            return fields;
        }
        if (fileId.Length != 8)
            return WithNote(L.T("이 저널의 식별자 형식으로는 MFT 항목을 특정할 수 없습니다.", "This journal identifier cannot identify an MFT entry."));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT e.pos,n.name,n.pos,n.attr_offset,COUNT(n.name) OVER()
            FROM mft_entries e LEFT JOIN mft_names n ON n.file=e.id
            WHERE e.id=$file
            ORDER BY (n.name=$name) DESC,(n.namespace=2),n.pos,n.attr_offset LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$name", preferredName);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return WithNote(L.T("이 파일 참조와 일치하는 MFT 레코드를 수집본에서 찾지 못했습니다.", "No MFT record in the acquisition matches this file reference."));
        long baseOffset = r.GetInt64(0);
        fields.Add(new(L.T("기본 FILE 레코드 시작", "Base FILE record start"), $"0x{baseOffset:X}"));
        if (r.IsDBNull(1))
            return WithNote(L.T("이 항목에서 읽을 수 있는 $FILE_NAME 속성이 없습니다.", "No readable $FILE_NAME attribute in this entry."));
        long container = r.GetInt64(2), relative = r.GetInt64(3);
        fields.Add(new("$FILE_NAME.FileName", r.GetString(1)));
        if (r.GetInt64(4) > 1)
            fields.Add(new(L.T("파일명 속성 수", "File name attribute count"), r.GetInt64(4).ToString(CultureInfo.InvariantCulture)));
        if (container != baseOffset)
            fields.Add(new(L.T("확장 FILE 레코드 시작", "Extension FILE record start"), $"0x{container:X}"));
        fields.Add(new(L.T("$FILE_NAME 속성 시작", "$FILE_NAME attribute start"), $"0x{checked(container + relative):X}  (FILE + 0x{relative:X})"));
        return fields;
    }

    internal static (string Hash, long Records, long Skipped) Summary(string database)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        EngineIndexFormat.Validate(c, EngineArtifact.Mft);
        cmd.CommandText = "SELECT hash,records,skipped FROM mft_summary";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt64(1) == 0)
            throw new InvalidDataException(L.T("분석할 수 있는 MFT 항목이 없습니다. 추출한 $MFT 파일을 선택해 주세요.", "No readable MFT entries. Select an extracted $MFT file."));
        return (r.GetString(0), r.GetInt64(1), r.GetInt64(2));
    }

    internal static Link? ReadLink(string file, string journalPath)
    {
        if (!File.Exists(file))
            return null;
        var link = JsonSerializer.Deserialize<Link>(File.ReadAllText(file));
        return link is not null && StringComparer.OrdinalIgnoreCase.Equals(link.JournalPath, journalPath)
            && !string.IsNullOrWhiteSpace(link.MftPath) && !string.IsNullOrWhiteSpace(link.Hash) ? link : null;
    }

    internal void WriteLink(string file, string journalPath)
    {
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Link(journalPath, SourcePath, Hash)));
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
