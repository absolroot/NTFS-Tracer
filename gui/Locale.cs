namespace JournalDesk;

internal sealed record Locale(string Code, string CultureName, string NativeName)
{
    internal static readonly Locale English = new("en", "en-US", "English");
    internal static readonly Locale BrazilianPortuguese = new("pt-BR", "pt-BR", "Português (Brasil)");
    internal static IReadOnlyList<Locale> All
    {
        get;
    } =
    [
        English,
        new("ko", "ko-KR", "한국어"),
        new("ja", "ja-JP", "日本語"),
        new("de", "de-DE", "Deutsch"),
        new("es", "es-ES", "Español"),
        new("fr", "fr-FR", "Français"),
        new("id", "id-ID", "Bahasa Indonesia"),
        new("it", "it-IT", "Italiano"),
        new("pl", "pl-PL", "Polski"),
        BrazilianPortuguese,
        new("tr", "tr-TR", "Türkçe"),
        new("vi", "vi-VN", "Tiếng Việt"),
        new("ru", "ru-RU", "Русский"),
        new("uk", "uk-UA", "Українська"),
        new("zh-Hans", "zh-CN", "简体中文"),
        new("zh-Hant", "zh-TW", "繁體中文")
    ];
}
