using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JournalDesk;

public sealed record HexField(int Start, int Length, string Label, string Value);

public static class UsnHexFields
{
    public const string FileNameLabel = "FileName";
    public const string ReasonLabel = "Reason";
    private static readonly (uint Mask, string Name)[] SourceFlags = [
        (1, "USN_SOURCE_DATA_MANAGEMENT"), (2, "USN_SOURCE_AUXILIARY_DATA"), (4, "USN_SOURCE_REPLICATION_MANAGEMENT"), (8, "USN_SOURCE_CLIENT_REPLICATION_MANAGEMENT")
    ];
    private static readonly (uint Mask, string Name)[] AttributeFlags = [
        (1, "FILE_ATTRIBUTE_READONLY"), (2, "FILE_ATTRIBUTE_HIDDEN"), (4, "FILE_ATTRIBUTE_SYSTEM"), (0x10, "FILE_ATTRIBUTE_DIRECTORY"), (0x20, "FILE_ATTRIBUTE_ARCHIVE"),
        (0x40, "FILE_ATTRIBUTE_DEVICE"), (0x80, "FILE_ATTRIBUTE_NORMAL"), (0x100, "FILE_ATTRIBUTE_TEMPORARY"), (0x200, "FILE_ATTRIBUTE_SPARSE_FILE"), (0x400, "FILE_ATTRIBUTE_REPARSE_POINT"),
        (0x800, "FILE_ATTRIBUTE_COMPRESSED"), (0x1000, "FILE_ATTRIBUTE_OFFLINE"), (0x2000, "FILE_ATTRIBUTE_NOT_CONTENT_INDEXED"), (0x4000, "FILE_ATTRIBUTE_ENCRYPTED"),
        (0x8000, "FILE_ATTRIBUTE_INTEGRITY_STREAM"), (0x10000, "FILE_ATTRIBUTE_VIRTUAL"), (0x20000, "FILE_ATTRIBUTE_NO_SCRUB_DATA"),
        (0x40000, "FILE_ATTRIBUTE_EA / FILE_ATTRIBUTE_RECALL_ON_OPEN"), (0x80000, "FILE_ATTRIBUTE_PINNED"),
        (0x100000, "FILE_ATTRIBUTE_UNPINNED"), (0x400000, "FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS"), (0x20000000, "FILE_ATTRIBUTE_STRICTLY_SEQUENTIAL")
    ];

    public static IReadOnlyList<HexField> Decode(byte[] bytes)
    {
        if (bytes.Length < 8)
            return [];
        uint size = U32(bytes, 0);
        if (size < 8)
            return [];

        ushort version = U16(bytes, 4);
        string prefix = version == 4 ? "Header." : "";
        var fields = new List<HexField> {
            new(0, 4, prefix + "RecordLength", L.T("{0}바이트", "{0} bytes", size)),
            new(4, 2, prefix + "MajorVersion", version.ToString(CultureInfo.InvariantCulture)),
            new(6, 2, prefix + "MinorVersion", U16(bytes, 6).ToString(CultureInfo.InvariantCulture))
        };
        if (size > bytes.Length || size > 65536 || size % 8 != 0 || version is not (2 or 3 or 4))
            return fields;

        int idWidth = version == 2 ? 8 : 16;
        int usnAt = 8 + 2 * idWidth;
        int headerLength = version == 4 ? 64 : usnAt + 36;
        if (size < headerLength)
            return fields;
        fields.Add(new(8, idWidth, "FileReferenceNumber", ReferenceText(bytes.AsSpan(8, idWidth))));
        fields.Add(new(8 + idWidth, idWidth, "ParentFileReferenceNumber", ReferenceText(bytes.AsSpan(8 + idWidth, idWidth))));
        fields.Add(new(usnAt, 8, "Usn", I64(bytes, usnAt).ToString(CultureInfo.InvariantCulture)));

        int reasonAt = usnAt + 8;
        if (version != 4)
        {
            fields.Add(new(reasonAt, 8, "TimeStamp", FileTime(I64(bytes, reasonAt))));
            reasonAt += 8;
        }
        uint reason = U32(bytes, reasonAt);
        fields.Add(new(reasonAt, 4, ReasonLabel, $"{Entry.DescribeReasons(reason)} (0x{reason:X8})"));
        fields.Add(new(reasonAt + 4, 4, "SourceInfo", Flags(U32(bytes, reasonAt + 4), SourceFlags)));

        if (version == 4)
        {
            AddExtents(fields, bytes, (int)size);
        }
        else
        {
            fields.Add(new(usnAt + 24, 4, "SecurityId", U32(bytes, usnAt + 24).ToString(CultureInfo.InvariantCulture)));
            fields.Add(new(usnAt + 28, 4, "FileAttributes", Flags(U32(bytes, usnAt + 28), AttributeFlags)));
            int nameLength = U16(bytes, usnAt + 32);
            int nameAt = U16(bytes, usnAt + 34);
            fields.Add(new(usnAt + 32, 2, "FileNameLength", L.T("{0}바이트", "{0} bytes", nameLength)));
            fields.Add(new(usnAt + 34, 2, "FileNameOffset", L.T("레코드 +0x{0:X}", "Record +0x{0:X}", nameAt)));
            if (nameLength is >= 2 and <= 510 && (nameLength | nameAt) % 2 == 0 &&
                nameAt >= headerLength && nameAt + nameLength <= size)
            {
                fields.Add(new(nameAt, nameLength, FileNameLabel, DecodeName(bytes.AsSpan(nameAt, nameLength))));
            }
        }
        return fields;
    }

    private static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));

    private static string Flags(uint value, (uint Mask, string Name)[] definitions)
    {
        if (value == 0)
            return L.T("설정된 비트 없음 (0x00000000)", "No bits set (0x00000000)");
        uint remaining = value;
        var names = new List<string>();
        foreach (var (mask, name) in definitions)
        {
            if ((value & mask) == 0)
                continue;
            names.Add(name);
            remaining &= ~mask;
        }
        if (remaining != 0)
            names.Add(L.T("그 밖의 비트 0x{0:X8}", "Other bits 0x{0:X8}", remaining));
        return $"{string.Join(" · ", names)} (0x{value:X8})";
    }

    internal static string ReferenceText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8)
            return Convert.ToHexString(bytes);
        ulong reference = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return $"0x{reference:X16}  (MFT record {reference & 0x0000FFFFFFFFFFFFUL}, sequence {reference >> 48})";
    }

    private static string FileTime(long value)
    {
        try
        {
            return DateTime.FromFileTimeUtc(value).ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return L.T("시각으로 변환할 수 없음 (FILETIME {0})", "Invalid timestamp (FILETIME {0})", value);
        }
    }

    internal static string DecodeName(ReadOnlySpan<byte> bytes, bool escapeControls = true)
    {
        var result = new StringBuilder(bytes.Length / 2);
        for (int at = 0; at < bytes.Length; at += 2)
        {
            char value = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
            if (char.IsHighSurrogate(value) && at + 4 <= bytes.Length)
            {
                char next = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
                if (char.IsLowSurrogate(next))
                {
                    result.Append(value).Append(next);
                    at += 2;
                    continue;
                }
            }
            if (char.IsSurrogate(value) || (escapeControls && char.IsControl(value)))
                result.Append(CultureInfo.InvariantCulture, $"\\u{{{(int)value:X4}}}");
            else
                result.Append(value);
        }
        return result.ToString();
    }

    private static void AddExtents(List<HexField> fields, byte[] bytes, int recordSize)
    {
        int count = U16(bytes, 60);
        int stride = U16(bytes, 62);
        fields.Add(new(56, 4, "RemainingExtents", L.T("{0}개", "{0} items", U32(bytes, 56))));
        fields.Add(new(60, 2, "NumberOfExtents", L.T("{0}개", "{0} items", count)));
        fields.Add(new(62, 2, "ExtentSize", L.T("{0}바이트", "{0} bytes", stride)));
        if (stride < 16 || count > (recordSize - 64) / stride)
            return;
        for (int i = 0; i < count; i++)
        {
            int start = 64 + i * stride;
            fields.Add(new(start, 8, $"Extents[{i}].Offset", L.T("{0}바이트", "{0} bytes", I64(bytes, start))));
            fields.Add(new(start + 8, 8, $"Extents[{i}].Length", L.T("{0}바이트", "{0} bytes", I64(bytes, start + 8))));
        }
    }
}
