using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using JournalDesk;

internal static class LocaleChecks
{
    internal static void Run()
    {
        var captions = new ResourceManager("JournalDesk.UiText", typeof(L).Assembly);
        var neutral = Keys(captions, CultureInfo.InvariantCulture);
        var japanese = Keys(captions, CultureInfo.GetCultureInfo("ja"));
        if (neutral.Count != japanese.Count || neutral.Any(pair =>
            !japanese.TryGetValue(pair.Key, out string? translation) || string.IsNullOrWhiteSpace(translation) ||
            CompositeFormat.Parse(pair.Value).MinimumArgumentCount != CompositeFormat.Parse(translation).MinimumArgumentCount))
            throw new InvalidOperationException("Japanese captions must cover all neutral keys and format arguments.");

        var original = CultureInfo.CurrentUICulture;
        var threadDefault = CultureInfo.DefaultThreadCurrentUICulture;
        string originalCode = L.Code;
        try
        {
            var messages = new ResourceManager("JournalDesk.Messages", typeof(L).Assembly);
            foreach (var locale in Locale.All.Where(locale => locale.Code is not ("ko" or "en" or "ja")))
            {
                var localized = Keys(captions, CultureInfo.GetCultureInfo(locale.Code));
                var dialogs = Keys(messages, CultureInfo.GetCultureInfo(locale.Code));
                string[] required = ["AllRecords", "IntakeStart", "IndexLocation", "SearchFileNames", "Overview", "PathEvidence", "GuideLanguageText"];
                if (localized.Count < 100 || required.Any(key => !localized.ContainsKey(key)) ||
                    localized.Any(pair => !neutral.TryGetValue(pair.Key, out string? source) ||
                        CompositeFormat.Parse(pair.Value).MinimumArgumentCount != CompositeFormat.Parse(source).MinimumArgumentCount))
                    throw new InvalidOperationException($"Invalid UI caption resources for {locale.Code}.");
                if (dialogs.Count < 35 || dialogs.Any(pair => CompositeFormat.Parse(pair.Key).MinimumArgumentCount != CompositeFormat.Parse(pair.Value).MinimumArgumentCount))
                    throw new InvalidOperationException($"Invalid runtime message resources for {locale.Code}.");
                L.Apply("system", CultureInfo.GetCultureInfo(locale.CultureName));
                if (L.Code != locale.Code || L.Caption("AllRecords") != localized["AllRecords"] ||
                    L.T("없음", "Loading records…") != dialogs["Loading records…"] || L.T("없음", "Hex") != "Hex")
                    throw new InvalidOperationException($"Windows language mapping or English fallback failed for {locale.Code}.");
            }

            L.Apply("system", CultureInfo.GetCultureInfo("zh-HK"));
            if (L.Code != "zh-Hant")
                throw new InvalidOperationException("Traditional Chinese must cover Windows zh-HK.");
            L.Apply("system", CultureInfo.GetCultureInfo("es-MX"));
            if (L.Code != "es")
                throw new InvalidOperationException("Spanish must cover Windows es-MX.");
            L.Apply("system", CultureInfo.GetCultureInfo("ja-JP"));
            const string diagnostic = "레코드 크기 불일치";
            const string unknown = "unexpected parser status [0x31]";
            if (L.Code != "ja" || L.Caption("Overview") != japanese["Overview"] ||
                EngineDiagnostic.Display(diagnostic) is "Record size mismatch" or diagnostic ||
                EngineDiagnostic.Display(unknown) != unknown ||
                !GlobalLogFilter.TryUsn("0x100", out long usn) || usn != 256)
                throw new InvalidOperationException("Japanese UI must translate known diagnostics and preserve evidence identifiers and native errors.");
        }
        finally
        {
            L.Apply(originalCode, original);
            CultureInfo.DefaultThreadCurrentUICulture = threadDefault;
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static Dictionary<string, string> Keys(ResourceManager captions, CultureInfo culture) =>
        (captions.GetResourceSet(culture, true, false) ?? throw new MissingManifestResourceException(culture.Name))
        .Cast<DictionaryEntry>().ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal);
}
