using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace JournalDesk;

public static class L
{
    private static readonly ResourceManager Captions = new("JournalDesk.UiText", typeof(L).Assembly);
#if !MAIL_VIEWER
    private static readonly ResourceManager JapaneseMessages = new("JournalDesk.JapaneseMessages", typeof(L).Assembly);
    private static readonly ResourceManager Messages = new("JournalDesk.Messages", typeof(L).Assembly);
    private static CultureInfo systemCulture = CultureInfo.CurrentUICulture;
#endif
    public static LanguageCatalog Text { get; } = new();
    public static string Code { get; private set; } = "ko";
    public static bool IsEnglish => Code == "en";
#if !MAIL_VIEWER
    public static string Preference { get; private set; } = "system";
    internal static bool ChooseOnStartup
    {
        get; private set;
    }
    internal static Locale SystemLocale => Resolve("system", systemCulture);
#endif
    private static CultureInfo DisplayCulture { get; set; } = CultureInfo.GetCultureInfo("ko-KR");
    public static XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(DisplayCulture.Name);

#if MAIL_VIEWER
    internal static void ApplyMailLanguage(bool english)
    {
        Code = english ? "en" : "ko";
        DisplayCulture = CultureInfo.GetCultureInfo(english ? "en-US" : "ko-KR");
        Text.Refresh();
    }

    public static string T(string korean, string english) => IsEnglish ? english : korean;
#else
    internal static void Initialize()
    {
        systemCulture = CultureInfo.CurrentUICulture;
        string? stored = null;
        try
        {
            stored = DeskSettings.Read("language.txt");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ChooseOnStartup = !Supported(stored);
        Preference = ChooseOnStartup ? "system" : stored!;
        Apply(Preference, systemCulture);
    }

    private static bool Supported(string? value) => value == "system" || Locale.All.Any(locale => locale.Code == value);

    private static Locale Resolve(string preference, CultureInfo system)
    {
        if (preference == "system")
        {
            for (var culture = system; culture.Name.Length > 0; culture = culture.Parent)
            {
                var match = Locale.All.FirstOrDefault(locale => string.Equals(locale.Code, culture.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(locale.CultureName, culture.Name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match;
            }
            if (system.TwoLetterISOLanguageName == "pt")
                return Locale.BrazilianPortuguese;
        }
        return Locale.All.FirstOrDefault(locale => locale.Code == preference) ?? Locale.English;
    }

    internal static void Apply(string preference, CultureInfo system)
    {
        var locale = Resolve(preference, system);
        bool changed = Code != locale.Code;
        Code = locale.Code;
        DisplayCulture = CultureInfo.GetCultureInfo(locale.CultureName);
        CultureInfo.DefaultThreadCurrentUICulture = DisplayCulture;
        CultureInfo.CurrentUICulture = DisplayCulture;
        if (changed)
            Text.Refresh();
    }

    internal static void Preview(string preference) => Apply(preference, systemCulture);

    internal static void SavePreference(string value)
    {
        if (!Supported(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        DeskSettings.Write("language.txt", value);
        SelectForSession(value);
    }

    internal static void SelectForSession(string value)
    {
        Preference = value;
        ChooseOnStartup = false;
        Apply(value, systemCulture);
    }

    public static string T(string korean, string english) => Code switch
    {
        "ko" => korean,
        "ja" => JapaneseMessages.GetString(english, CultureInfo.InvariantCulture) ?? english,
        "en" => english,
        _ => Messages.GetString(english, DisplayCulture) ?? english
    };

#endif

    public static string T(string korean, string english, params object?[] values) =>
        string.Format(DisplayCulture, T(korean, english), values);
    internal static string Caption(string key) => Captions.GetString(key, DisplayCulture)
        ?? throw new MissingManifestResourceException($"Missing NTFS Tracer caption: {key}");
}

public sealed class LanguageCatalog : INotifyPropertyChanged
{
    public string this[string key] => L.Caption(key);
    public XmlLanguage Language { get; private set; } = XmlLanguage.GetLanguage("ko-KR");
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        Language = L.XmlLanguage;
        PropertyChanged?.Invoke(this, new(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new(nameof(Language)));
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class UiExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetProperty: System.Reflection.PropertyInfo property }
            && property.PropertyType == typeof(string))
            return L.Caption(key);
        return new Binding($"[{key}]") { Source = L.Text, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
