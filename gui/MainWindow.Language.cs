using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public partial class MainWindow
{
    private void ShowLanguageChoices(object sender, RoutedEventArgs e)
    {
        if (LanguageMenu.Items.Count == 2)
        {
            foreach (var locale in Locale.All)
            {
                var item = new MenuItem { Header = locale.NativeName, Tag = locale.Code, Language = System.Windows.Markup.XmlLanguage.GetLanguage(locale.CultureName) };
                item.Click += ChooseLanguage;
                LanguageMenu.Items.Add(item);
            }
        }
        foreach (var item in LanguageMenu.Items.OfType<MenuItem>())
            item.IsChecked = Equals(item.Tag, L.Preference);
    }

    private async void ChooseLanguage(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value })
            return;
        try
        {
            string previous = L.Code;
            L.SavePreference(value);
            if (previous != L.Code)
                await RefreshLanguage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); }
        ShowLanguageChoices(this, e);
    }

    private async Task RefreshLanguage()
    {
        Setup.RefreshLanguage();
        foreach (var guide in OwnedWindows.OfType<GuideWindow>())
            guide.RefreshLanguage();
        RefreshJournalSummary();
        if (!busy)
            UpdateAttachmentStatus(false);
        UpdateSourceLabel();
        SetHistoryPathSource();
        foreach (var activity in activities)
            activity.RefreshLanguage();
        GlobalView.RefreshLanguage(historyPaths);
        (LeadsTab.Content as InvestigationView)?.RefreshLanguage();
        var selectedFile = FileList.SelectedItem;
        updatingFileList = true;
        try
        {
            FileList.Items.Refresh();
            FileList.SelectedItem = selectedFile;
        }
        finally { updatingFileList = false; }
        cachedHistoryValues = null;
        if (HistoryTab.IsSelected)
            await ApplyHistoryOrder();
        else
            historyPathSortPending = true;
        await RefreshHistoryProof(false);
        await Transactions.RefreshLanguage();
    }
}
