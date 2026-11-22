using System.Buffers.Binary;
using System.Globalization;

namespace JournalDesk;

internal sealed record RecordSearch(string Path = "", NtfsReference? File = null, NtfsReference? Parent = null)
{
    internal bool IsEmpty => Path.Length == 0 && File == null && Parent == null;
    internal bool MatchesPath(string path) =>
        path.Contains(Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct NtfsReference(ulong Value, bool SlotOnly, string? WideHex = null)
{
    private const ulong SlotMask = 0x0000_FFFF_FFFF_FFFF;

    internal byte[]? ExactBytes
    {
        get
        {
            if (SlotOnly)
                return null;
            if (WideHex != null)
                return Convert.FromHexString(WideHex);
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, Value);
            return bytes;
        }
    }

    internal bool Matches(byte[]? raw)
    {
        if (raw is not { Length: 8 or 16 })
            return false;
        if (WideHex != null)
            return raw.Length == 16 && string.Equals(Convert.ToHexString(raw), WideHex, StringComparison.Ordinal);
        if (raw.Length != 8)
            return false;
        ulong recorded = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        return (SlotOnly ? recorded & SlotMask : recorded) == Value;
    }

    internal static string? DisplayId(byte[]? raw)
    {
        if (raw is { Length: 16 })
            return Convert.ToHexString(raw);
        if (raw is not { Length: 8 })
            return null;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        return id > SlotMask ? id.ToString(CultureInfo.InvariantCulture) : FormattableString.Invariant($"0x{id:X16}");
    }

    internal static bool TryParse(string text, out NtfsReference result)
    {
        result = default;
        var term = text.Trim().AsSpan();
        bool hex = term.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            term = term[2..];
            int label = term.IndexOf("  (MFT record ", StringComparison.Ordinal);
            if (label == 16 && term.EndsWith(")", StringComparison.Ordinal))
                term = term[..label];
        }
        if (term.Length == 32 && term.ToString().All(Uri.IsHexDigit))
        {
            result = new(0, false, term.ToString().ToUpperInvariant());
            return true;
        }
        if (hex)
        {
            if (!ulong.TryParse(term, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value))
                return false;
            result = new(value, false);
            return true;
        }
        int separator = term.IndexOf(':');
        var slotText = separator < 0 ? term : term[..separator].Trim();
        if (!ulong.TryParse(slotText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong slot))
            return false;
        if (separator < 0)
        {
            result = new(slot, slot <= SlotMask);
            return true;
        }
        if (slot > SlotMask || !ushort.TryParse(term[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort sequence))
            return false;
        result = new(slot | (ulong)sequence << 48, false);
        return true;
    }
}
