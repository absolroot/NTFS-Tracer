using System.Buffers.Binary;
using System.Globalization;

namespace JournalDesk;

// Display the same unmodified bytes that the Hex pane reads. The engine parses
// update-sequence-corrected pages, so a raw sector trailer isn't a field value.
internal static class LogHeaderFields
{
    private static readonly (int Start, int Length, string Name, bool Hex)[] RecordFields = [
        (0,8,"ThisLsn",true),(8,8,"ClientPreviousLsn",true),(16,8,"ClientUndoNextLsn",true),
        (24,4,"ClientDataLength",false),(28,2,"ClientId.SeqNumber",false),
        (30,2,"ClientId.ClientIndex",false),(32,4,"RecordType",false),
        (36,4,"TransactionId",false),(40,2,"Flags",true)
    ];
    private static readonly (int Start, int Length, string Name, bool Hex)[] PageFields = [
        (4,2,"UpdateSequenceArrayOffset",true),(6,2,"UpdateSequenceArraySize",false),
        (20,2,"PageCount",false),(22,2,"PagePosition",false),(24,2,"NextRecordOffset",true),
        (32,8,"LastEndLsn",true)
    ];
    public static IReadOnlyList<HexField> Record(byte[] header, long pageOffset) => Read(header, pageOffset, RecordFields);
    public static IReadOnlyList<HexField> Page(byte[] header)
    {
        var fields = Read(header, 0, PageFields);
        if (header.AsSpan().StartsWith("RCRD"u8))
            fields.Insert(0, new(0, 4, "Signature", "RCRD"));
        return fields;
    }
    private static List<HexField> Read(byte[] header, long pageOffset, (int Start, int Length, string Name, bool Hex)[] layout)
    {
        var fields = new List<HexField>(layout.Length);
        foreach (var (start, length, name, hex) in layout)
        {
            if (start + length > header.Length)
                continue;
            var bytes = header.AsSpan(start, length);
            string text;
            if (((pageOffset + start) % 512) + length > 510)
                text = L.T("섹터 끝 보정 전 바이트 {0}", "Sector trailer before fixup: {0}", Convert.ToHexString(bytes));
            else
            {
                ulong value = length switch
                {
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                    _ => BinaryPrimitives.ReadUInt64LittleEndian(bytes)
                };
                text = hex ? "0x" + value.ToString("X" + length * 2, CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
            }
            fields.Add(new(start, length, name, text));
        }
        return fields;
    }
}
