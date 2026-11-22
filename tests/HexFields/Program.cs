using System.Buffers.Binary;
using System.Text;
using JournalDesk;

static void Check(bool expected, string message)
{
    if (!expected)
        throw new InvalidDataException(message);
}
static void Word(byte[] data, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)value);
static void Dword(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
static void Long(byte[] data, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), value);
static byte[] Record(int version, int size)
{
    var bytes = new byte[size];
    Dword(bytes, 0, (uint)size);
    Word(bytes, 4, version);
    return bytes;
}
static IReadOnlyList<HexField> Verify(byte[] bytes)
{
    var fields = UsnHexFields.Decode(bytes);
    int end = 0;
    foreach (var field in fields)
    {
        Check(field.Start >= end && field.Length > 0 && field.Length <= bytes.Length - field.Start, field.ToString());
        end = field.Start + field.Length;
    }
    return fields;
}
static HexField At(IReadOnlyList<HexField> fields, int start) => fields.Single(f => f.Start == start);

foreach (int version in new[] { 2, 3 })
{
    string name = "이전📄.txt";
    int usn = version == 2 ? 24 : 40;
    int nameAt = usn + 40; // Include bytes beyond the known fixed header.
    byte[] rawName = Encoding.Unicode.GetBytes(name);
    var bytes = Record(version, (nameAt + rawName.Length + 7) / 8 * 8);
    bytes[8] = 44;
    bytes[15] = 1;
    bytes[23] = 0xab;
    Long(bytes, usn, 123456789);
    Long(bytes, usn + 8, DateTime.UnixEpoch.ToFileTimeUtc());
    Dword(bytes, usn + 16, 0x80002000u);
    Dword(bytes, usn + 20, 0x21);
    Dword(bytes, usn + 28, 0x12);
    Word(bytes, usn + 32, rawName.Length);
    Word(bytes, usn + 34, nameAt);
    rawName.CopyTo(bytes, nameAt);
    var fields = Verify(bytes);
    int idWidth = version == 2 ? 8 : 16;
    (int Start, int Length)[] expectedRanges = [
        (0,4),(4,2),(6,2),(8,idWidth),(8+idWidth,idWidth),(usn,8),(usn+8,8),(usn+16,4),(usn+20,4),
        (usn+24,4),(usn+28,4),(usn+32,2),(usn+34,2),(nameAt,rawName.Length)
    ];
    Check(fields.Select(f => (f.Start, f.Length)).SequenceEqual(expectedRanges), $"V{version} exact field boundaries");
    string[] fieldsLabels = [
        "RecordLength","MajorVersion","MinorVersion","FileReferenceNumber","ParentFileReferenceNumber",
        "Usn","TimeStamp","Reason","SourceInfo","SecurityId","FileAttributes","FileNameLength","FileNameOffset","FileName"
    ];
    Check(fields.Select(f => f.Label).SequenceEqual(fieldsLabels), $"V{version} structure member names");
    Check(At(fields, usn).Value == "123456789", "USN");
    Check(At(fields, usn + 8).Value == "1970-01-01 00:00:00.0000000 UTC", "FILETIME");
    Check(At(fields, usn + 16).Value.Contains("RENAME_NEW_NAME") && At(fields, usn + 16).Value.Contains("CLOSE"), "all reason bits");
    Check(At(fields, usn + 20).Value.Contains("그 밖의 비트 0x00000020") && At(fields, usn + 28).Value.Contains("FILE_ATTRIBUTE_HIDDEN · FILE_ATTRIBUTE_DIRECTORY"), "additional and unknown bits");
    Dword(bytes, usn + 28, 0x80040000);
    var attributeValue = At(Verify(bytes), usn + 28).Value;
    Check(attributeValue.Contains("FILE_ATTRIBUTE_EA / FILE_ATTRIBUTE_RECALL_ON_OPEN") &&
        attributeValue.Contains("0x80000000") && attributeValue.EndsWith("(0x80040000)", StringComparison.Ordinal), "aliased and unknown attribute bits coexist");
    Check(At(fields, nameAt) == new HexField(nameAt, rawName.Length, UsnHexFields.FileNameLabel, name), "UTF-16LE filename");
    Check(At(fields, 8).Length == (version == 2 ? 8 : 16), "file ID width");
    if (version == 3)
        Check(At(fields, 8).Value == Convert.ToHexString(bytes.AsSpan(8, 16)), "opaque 128-bit ID");
    Check(!Verify(bytes[..^1]).Any(f => f.Label == UsnHexFields.FileNameLabel), "truncated source");
    Word(bytes, nameAt, 0xd800);
    Check(At(Verify(bytes), nameAt).Value.StartsWith(@"\u{D800}", StringComparison.Ordinal), "unpaired surrogate preservation");
    foreach (int badOffset in new[] { 6, nameAt - 1, 65534 })
    {
        Word(bytes, usn + 34, badOffset);
        Check(!Verify(bytes).Any(f => f.Label == UsnHexFields.FileNameLabel), "overlap / alignment / record boundary");
    }
}

var ranges = Record(4, 112);
for (int i = 8; i < 40; i++) ranges[i] = (byte)(i + 0x40);
Word(ranges, 60, 2);
Word(ranges, 62, 24);
Dword(ranges, 56, uint.MaxValue);
Long(ranges, 40, -1);
Long(ranges, 64, 300);
Long(ranges, 72, 7);
Long(ranges, 88, 400);
Long(ranges, 96, 9);
var v4 = Verify(ranges);
Check(v4.Select(f => (f.Start, f.Length)).SequenceEqual(new[]{
    (0,4),(4,2),(6,2),(8,16),(24,16),(40,8),(48,4),(52,4),(56,4),(60,2),(62,2),(64,8),(72,8),(88,8),(96,8)
}), "V4 exact field boundaries, including the unassigned part of a 24-byte stride");
Check(At(v4, 40).Value == "-1" && At(v4, 56).Value.Contains("4294967295"), "signed USN and unsigned remaining count");
string[] v4Labels = [
    "Header.RecordLength","Header.MajorVersion","Header.MinorVersion","FileReferenceNumber","ParentFileReferenceNumber",
    "Usn","Reason","SourceInfo","RemainingExtents","NumberOfExtents","ExtentSize",
    "Extents[0].Offset","Extents[0].Length","Extents[1].Offset","Extents[1].Length"
];
Check(v4.Select(f => f.Label).SequenceEqual(v4Labels), "V4 header and extent member paths");
Check(At(v4, 8).Length == 16 && At(v4, 8).Value == Convert.ToHexString(ranges.AsSpan(8, 16))
    && At(v4, 24).Length == 16 && At(v4, 24).Value == Convert.ToHexString(ranges.AsSpan(24, 16)), "V4 opaque 128-bit IDs");
Check(At(v4, 88).Value == "400바이트" && At(v4, 96).Value == "9바이트", "V4 extent stride");
var readRecord = typeof(HexField).Assembly.GetType("JournalDesk.UsnEvidence")!
    .GetMethod("Read", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
    .CreateDelegate<Func<FileStream, Entry, byte[]?, byte[]>>();
var indexed = new Entry(0, ranges.Length, 4, ranges[24..40], -1, null, 0, 0, null, null, "", 0, false, false, uint.MaxValue, "300 + 7; 400 + 9", 0) { ExtentStride = 24 };
var recordDetails = typeof(HexField).Assembly.GetType("JournalDesk.UsnEvidence")!
    .GetMethod("RecordFields", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
    .CreateDelegate<Func<Entry, byte[], List<EvidenceField>>>();
Check(recordDetails(indexed, ranges[8..24]).Contains(new EvidenceField("ExtentSize", "24 B")), "indexed V4 stride");
var zero = recordDetails(indexed with { Extents = "", Remaining = 0, ExtentStride = null }, ranges[8..24]);
var unknown = recordDetails(indexed with { Extents = "", Remaining = null, ExtentStride = null }, ranges[8..24]);
Check(zero.Contains(new EvidenceField("RemainingExtents", "0")) && unknown.Contains(new EvidenceField("RemainingExtents", "색인에 기록되지 않음")) &&
    unknown.Any(f => f.Value == "색인에 저장된 범위 없음") && !unknown.Any(f => f.Label == "ExtentSize"), "unavailable, absent, and zero range values");
var named = recordDetails(indexed with { Version = 3, Name = "조사 파일.eml" }, ranges[8..24]);
Check(named.Contains(new EvidenceField("FileName", "조사 파일.eml")) && !named.Any(f => f.Label == "FileNameOffset") && !named.Any(f => f.Label.StartsWith("Extents", StringComparison.Ordinal)), "indexed filename and source-only fields");
using (var temp = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".usn"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
{
    bool Matches(byte[] data, Entry entry)
    {
        temp.Position = 0;
        temp.SetLength(data.Length);
        temp.Write(data);
        temp.Flush();
        try
        {
            return readRecord(temp, entry, ranges[8..24]).SequenceEqual(data);
        }
        catch (InvalidDataException) { return false; }
    }
    Check(Matches(ranges, indexed) && Matches(ranges, indexed with
    {
        ExtentStride = null
    }), "current and legacy V4 index");
    Check(!Matches(ranges, indexed with
    {
        Extents = "300 + 7; 400 + 8"
    }) &&
          !Matches(ranges, indexed with
          {
              Extents = "400 + 9; 300 + 7"
          }) &&
          !Matches(ranges, indexed with
          {
              Extents = "300 + 7"
          }) &&
          !Matches(ranges, indexed with
          {
              Remaining = 0
          }) && !Matches(ranges, indexed with
          {
              ExtentStride = 16
          }), "changed V4 index");
    var corrupt = (byte[])ranges.Clone();
    Long(corrupt, 64, -300);
    Check(!Matches(corrupt, indexed) && Matches(corrupt, indexed with
    {
        Extents = "-300 + 7; 400 + 9"
    }), "signed source offset mismatch and preservation");
    corrupt = (byte[])ranges.Clone();
    Word(corrupt, 60, 65535);
    Check(!Matches(corrupt, indexed), "V4 count exceeds source");
    corrupt = (byte[])ranges.Clone();
    Word(corrupt, 62, 8);
    Check(!Matches(corrupt, indexed with
    {
        ExtentStride = null
    }), "invalid source stride even without a stored stride");
}
Word(ranges, 60, 65535);
Word(ranges, 62, 65535);
Check(Verify(ranges).All(f => f.Start < 64), "count × stride must not overflow");
Check(Verify(Record(7, 64)).Count == 3, "unknown version common header");
for (int i = 0; i < ranges.Length; i++) Verify(ranges[..i]);

if (args.Length is not (0 or 3))
    throw new ArgumentException("Pass a journal path and the old and new rename record offsets in hexadecimal.");
if (args.Length == 3)
{
    using var input = File.OpenRead(args[0]);
    void CheckRenameRecord(string offset, string reason)
    {
        long position = Convert.ToInt64(offset, 16);
        input.Position = position;
        Span<byte> header = stackalloc byte[8];
        input.ReadExactly(header);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        Check(size is >= 60 and <= 1_048_576 && version is 2 or 3, "expected a complete V2 or V3 rename record");
        input.Position = position;
        var bytes = new byte[size];
        input.ReadExactly(bytes);
        int reasonOffset = version == 2 ? 40 : 56;
        int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(reasonOffset + 18));
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(reasonOffset + 16));
        var fields = Verify(bytes);
        Check(At(fields, reasonOffset).Value.Contains(reason, StringComparison.Ordinal), "real rename reason");
        Check(At(fields, nameOffset).Value == Encoding.Unicode.GetString(bytes, nameOffset, nameLength),
            "real UTF-16LE filename");
    }
    CheckRenameRecord(args[1], "USN_REASON_RENAME_OLD_NAME");
    CheckRenameRecord(args[2], "USN_REASON_RENAME_NEW_NAME");
    Console.WriteLine("Real $J rename pair: source reason and UTF-16LE name verified.");
}
Console.WriteLine("USN V2/V3/V4 field and boundary tests passed.");
