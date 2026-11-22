using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace JournalDesk;

internal static class UsnEvidence
{
    internal static List<EvidenceField> RecordFields(Entry entry, byte[] fileId)
    {
        var fields = new List<EvidenceField> {
            new(L.T("구조체", "Structure"), $"USN_RECORD_V{entry.Version} ({entry.Version}.{entry.Minor})"),
            new("File offset", $"0x{entry.Position:X}"),
            new(entry.Version == 4 ? "Header.RecordLength" : "RecordLength", $"{entry.Size} B"),
            new("Usn", entry.Usn.ToString(CultureInfo.InvariantCulture)),
            new("FileReferenceNumber", UsnHexFields.ReferenceText(fileId)),
            new("ParentFileReferenceNumber", UsnHexFields.ReferenceText(entry.Parent)),
            new("Reason", entry.FlagsText) { SecondaryValue = entry.RawReason },
            new("SourceInfo", $"0x{entry.Source:X8}")
        };
        if (entry.Version == 4)
        {
            fields.Add(new("RemainingExtents", entry.Remaining?.ToString(CultureInfo.InvariantCulture) ?? L.T("색인에 기록되지 않음", "Not recorded in index")));
            if (entry.ExtentStride is int stride)
                fields.Add(new("ExtentSize", $"{stride} B"));
            fields.Add(new("Extents (Offset + Length, B)", entry.Extents.Length == 0 ? L.T("색인에 저장된 범위 없음", "No indexed extents") : entry.Extents));
        }
        else
        {
            string missing = L.T("기록 없음", "Not recorded");
            fields.Add(new("FileName", entry.RecordedName));
            fields.Add(new("TimeStamp (FILETIME)", entry.Timestamp?.ToString(CultureInfo.InvariantCulture) ?? missing));
            fields.Add(new("SecurityId", entry.Security?.ToString(CultureInfo.InvariantCulture) ?? missing));
            fields.Add(new("FileAttributes", entry.Attributes is long bits ? $"0x{bits:X8}" : missing));
        }
        return fields;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonFormat = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string RecordJson(Entry entry, byte[] fileId, string? path, string? hash, IReadOnlyList<EvidenceField>? mft = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Source = path,
            SHA256 = string.IsNullOrEmpty(hash) ? null : hash,
            FileOffset = $"0x{entry.Position:X}",
            RecordLength = entry.Size,
            MajorVersion = entry.Version,
            MinorVersion = entry.Minor,
            Usn = entry.Usn.ToString(CultureInfo.InvariantCulture),
            FileReferenceNumber = Convert.ToHexString(fileId),
            ParentFileReferenceNumber = Convert.ToHexString(entry.Parent),
            FileName = entry.Version == 4 ? null : entry.RecordedName,
            FileNameEscaped = entry.Escaped,
            FileTime = entry.Timestamp?.ToString(CultureInfo.InvariantCulture),
            Reason = $"0x{entry.Reason:X8}",
            SourceInfo = $"0x{entry.Source:X8}",
            SecurityId = entry.Security,
            FileAttributes = entry.Attributes is long attributes ? $"0x{attributes:X8}" : null,
            RemainingExtents = entry.Remaining,
            ExtentSize = entry.ExtentStride,
            Extents = entry.Version == 4 ? entry.Extents : null,
            Resynced = entry.Resynced,
            Mft = mft
        }, JsonFormat);

    internal static byte[] Read(FileStream file, Entry entry, byte[]? expectedFileId = null)
    {
        if (entry.Position < 0 || entry.Size is < 1 or > 65536 || entry.Position > file.Length - entry.Size)
            throw new InvalidDataException(L.T("기록의 시작 위치와 길이가 추출 파일의 범위를 벗어납니다.", "Record offset or length extends beyond the extracted file."));

        byte[] data = new byte[entry.Size];
        int total = 0;
        while (total < data.Length)
        {
            int read = RandomAccess.Read(file.SafeFileHandle, data.AsSpan(total), entry.Position + total);
            if (read == 0)
                throw new EndOfStreamException(L.T("원본 기록을 끝까지 읽을 수 없습니다.", "Could not read the complete source record."));
            total += read;
        }
        if (!MatchesIndex(data, entry, expectedFileId))
            throw new InvalidDataException(L.T("원본 위치 0x{0:X}의 기록이 색인과 일치하지 않습니다. 같은 추출본을 다시 열어 주세요.", "The record at 0x{0:X} does not match the index. Reopen the matching extraction.", entry.Position));
        return data;
    }

    private static bool MatchesIndex(ReadOnlySpan<byte> data, Entry entry, byte[]? expectedFileId)
    {
        int idWidth = entry.Version == 2 ? 8 : 16;
        int usnAt = 8 + 2 * idWidth;
        int reasonAt = usnAt + (entry.Version == 4 ? 8 : 16);
        int minimum = entry.Version == 4 ? 64 : usnAt + 36;
        if (entry.Version is not (2 or 3 or 4) || data.Length < minimum || data.Length % 8 != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != data.Length ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != entry.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != entry.Minor ||
            (expectedFileId != null && !data.Slice(8, idWidth).SequenceEqual(expectedFileId)) ||
            !data.Slice(8 + idWidth, idWidth).SequenceEqual(entry.Parent) ||
            BinaryPrimitives.ReadInt64LittleEndian(data[usnAt..]) != entry.Usn ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[reasonAt..]) != entry.Reason ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[(reasonAt + 4)..]) != entry.Source)
            return false;
        if (entry.Version == 4)
            return MatchesExtents(data, entry);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(usnAt + 32)..]);
        int nameAt = BinaryPrimitives.ReadUInt16LittleEndian(data[(usnAt + 34)..]);
        return nameAt >= minimum && nameLength is >= 2 and <= 510 && ((nameAt | nameLength) & 1) == 0 && nameAt + nameLength <= data.Length &&
            (entry.Timestamp is not long stamp || BinaryPrimitives.ReadInt64LittleEndian(data[(usnAt + 8)..]) == stamp) &&
            (entry.Security is not long security || BinaryPrimitives.ReadUInt32LittleEndian(data[(usnAt + 24)..]) == security) &&
            (entry.Attributes is not long attributes || BinaryPrimitives.ReadUInt32LittleEndian(data[(usnAt + 28)..]) == attributes) &&
            UsnHexFields.DecodeName(data.Slice(nameAt, nameLength), escapeControls: false) == entry.Name;
    }

    private static bool MatchesExtents(ReadOnlySpan<byte> data, Entry entry)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[60..]);
        int stride = BinaryPrimitives.ReadUInt16LittleEndian(data[62..]);
        if ((entry.Remaining is long remaining && BinaryPrimitives.ReadUInt32LittleEndian(data[56..]) != remaining) ||
            (entry.ExtentStride is int stored && stored != stride) || stride < 16 ||
            count > (data.Length - 64) / stride || data.Length - 64 - count * stride >= 8)
            return false;
        ReadOnlySpan<char> indexed = entry.Extents;
        for (int i = 0; i < count; i++)
        {
            int next = indexed.IndexOf("; ", StringComparison.Ordinal);
            var extent = next < 0 ? indexed : indexed[..next];
            int plus = extent.IndexOf(" + ", StringComparison.Ordinal);
            var raw = data[(64 + i * stride)..];
            if (plus < 0 || !long.TryParse(extent[..plus], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long offset) ||
                !long.TryParse(extent[(plus + 3)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long length) ||
                BinaryPrimitives.ReadInt64LittleEndian(raw) != offset || BinaryPrimitives.ReadInt64LittleEndian(raw[8..]) != length ||
                (i == count - 1) != (next < 0))
                return false;
            indexed = next < 0 ? [] : indexed[(next + 2)..];
        }
        return indexed.IsEmpty;
    }

    internal static void Display(HexEvidenceView view, byte[] data, Entry entry, int? cursor = null)
    {
        var fields = UsnHexFields.Decode(data);
        string label = (entry.Reason & 0x3000) != 0 && (entry.Reason & 0x80000000) == 0
            ? UsnHexFields.FileNameLabel : UsnHexFields.ReasonLabel;
        int preferred = cursor ?? fields.FirstOrDefault(field => field.Label == label)?.Start ?? 0;
        view.ShowBytes(data, entry.Position, fields, preferred);
    }
}
