namespace JournalDesk;

internal sealed record GuidePart(string Heading, string Text, string? Path = null);

internal sealed record GuideTopic(string Title, IReadOnlyList<GuidePart> Parts)
{
    internal bool Matches(string query) => Title.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Parts.Any(part => part.Heading.Contains(query, StringComparison.OrdinalIgnoreCase)
            || part.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || part.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
}

internal static class GuideContent
{
    public static IReadOnlyList<GuidePart> Locations =>
    [
        new("$J", L.Caption("ArtifactJournal"), @"\$Extend\$UsnJrnl:$J:$DATA"),
        new("$MFT", L.Caption("ArtifactMft"), @"\$MFT"),
        new("$LogFile", L.Caption("ArtifactLog"), @"\$LogFile")
    ];

    private static GuidePart Part(string title, string text) => new(L.Caption(title), L.Caption(text));

    internal static GuideTopic[] Topics() =>
    [
        new(L.Caption("GuideOpen"),
            [Part("SelectEvidenceFiles", "GuideOpenFiles"), Part("ImageFile", "GuideOpenImage"), Part("SourceInformationLabel", "GuideOpenVerify")]),
        new(L.Caption("ArtifactLocationTitle"),
            [new("", L.Caption("ArtifactRoot")), .. Locations, Part("IntakeSources", "ArtifactAcquire")]),
        new(L.Caption("GuideFollow"),
            [Part("AllRecords", "GuideFollowFilter"), Part("FileHistory", "GuideFollowFile"), Part("PathEvidence", "GuideFollowPath")]),
        new(L.Caption("GuideInspect"),
            [Part("CompareChanges", "GuideCompare"), Part("RecordDetails", "GuideFields"), new("Hex", L.Caption("GuideHex"))]),
        new(L.Caption("GuideTransactions"),
            [Part("NtfsTransactions", "GuideTransactionReads"), Part("InvestigationLeads", "GuideRecovery"), Part("TrackerResults", "GuideTracker")]),
        new(L.Caption("GuideSave"),
            [Part("Export", "GuideExportScope"), Part("IntakeOutput", "GuideFolder")]),
        new(L.Caption("GuidePreferences"),
            [Part("IndexingModeLabel", "GuideIndexing"), Part("TimeZone", "GuideTimeZone"), Part("GuideLanguage", "GuideLanguageText")])
    ];
}
