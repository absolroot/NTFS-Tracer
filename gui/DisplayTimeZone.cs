using System.ComponentModel;
using System.IO;

namespace JournalDesk;

public readonly record struct JournalTimeZone(int OffsetMinutes)
{
    internal TimeZoneInfo? WindowsZone
    {
        get; init;
    }
    internal static JournalTimeZone FromWindows(TimeZoneInfo zone) => new((int)zone.BaseUtcOffset.TotalMinutes) { WindowsZone = zone };
    internal bool IsUtc => OffsetMinutes == 0 && WindowsZone?.SupportsDaylightSavingTime != true;
    public TimeSpan Offset => TimeSpan.FromMinutes(OffsetMinutes);
    public string Code => WindowsZone != null ? "Windows" : OffsetMinutes switch
    {
        0 => "UTC",
        540 => "KST",
        _ => FormattableString.Invariant($"UTC{(OffsetMinutes < 0 ? "-" : "+")}{Math.Abs(OffsetMinutes) / 60:00}:{Math.Abs(OffsetMinutes) % 60:00}")
    };
    public string Caption => WindowsZone != null ? L.Caption("WindowsSetting") : OffsetMinutes == 540 ? "KST (UTC+09:00)" : Code;
    public string? WindowsZoneName => WindowsZone?.DisplayName;
    public static implicit operator JournalTimeZone(bool utc) => new(utc ? 0 : 540);

    internal DateTimeOffset At(DateTimeOffset instant) => WindowsZone == null
        ? instant.ToOffset(Offset) : TimeZoneInfo.ConvertTime(instant, WindowsZone);

    internal bool TryFileTime(DateTime local, bool end, bool dayBoundary, out long value)
    {
        value = 0;
        var offset = Offset;
        try
        {
            if (WindowsZone is { } system)
            {
                while (system.IsInvalidTime(local))
                {
                    if (!dayBoundary)
                        return false;
                    local = local.AddMinutes(1);
                }
                if (system.IsAmbiguousTime(local))
                {
                    var offsets = system.GetAmbiguousTimeOffsets(local);
                    offset = end ? offsets.Min() : offsets.Max();
                }
                else
                    offset = system.GetUtcOffset(local);
            }
            value = new DateTimeOffset(local, offset).ToFileTime();
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}

public sealed class DisplayTimeZone : INotifyPropertyChanged
{
    private static readonly JournalTimeZone WindowsDefault = JournalTimeZone.FromWindows(TimeZoneInfo.Local);
    public static IReadOnlyList<JournalTimeZone> Choices
    {
        get;
    } =
        [WindowsDefault, new(540), new(0), .. Enumerable.Range(-48, 105).Where(i => i is not (0 or 36)).Select(i => new JournalTimeZone(i * 15))];
    public static DisplayTimeZone Current { get; } = new();
    private JournalTimeZone zone = WindowsDefault;

    private DisplayTimeZone()
    {
        L.Text.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LanguageCatalog.Language))
            {
                PropertyChanged?.Invoke(this, new(nameof(TimeHeader)));
                PropertyChanged?.Invoke(this, new(nameof(RecordedHeader)));
            }
        };
        try
        {
            if (DeskSettings.Read("time-zone.txt") is { } saved)
                zone = Choices.FirstOrDefault(z => z.Code == saved || z.Caption == saved, WindowsDefault);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public JournalTimeZone Zone => zone;
    public bool Utc => zone.IsUtc;
    public string TimeHeader => L.Caption("TimeKst").Replace("KST", Code, StringComparison.Ordinal);
    public string RecordedHeader => L.Caption("RecordedTimeKstLabel").Replace("KST", Code, StringComparison.Ordinal);
    public string Code
    {
        get => zone.Code;
        set
        {
            int index = -1;
            for (int i = 0; i < Choices.Count; i++)
                if (Choices[i].Code == value)
                {
                    index = i;
                    break;
                }
            if (index < 0 || Choices[index] == zone)
                return;
            zone = Choices[index];
            try
            {
                DeskSettings.Write("time-zone.txt", Code);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (string property in new[] { nameof(Code), nameof(Zone), nameof(Utc), nameof(TimeHeader), nameof(RecordedHeader) })
                PropertyChanged?.Invoke(this, new(property));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
