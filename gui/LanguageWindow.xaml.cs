using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public partial class LanguageWindow : Window
{
    private bool sessionOnly;

    public LanguageWindow()
    {
        InitializeComponent();
        foreach (var locale in Locale.All)
            Languages.Items.Add(new ListBoxItem { Tag = locale.Code, Content = locale.NativeName, Language = System.Windows.Markup.XmlLanguage.GetLanguage(locale.CultureName) });
        Languages.SelectedIndex = 0;
        Loaded += (_, _) => ((ListBoxItem)Languages.SelectedItem).Focus();
    }

    private void SelectLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (Languages.SelectedItem is not ListBoxItem { Tag: string choice })
            return;
        L.Preview(choice);
        Language = L.XmlLanguage;
        Heading.Content = L.T("표시 언어", "Display language");
        ExitButton.Content = L.T("취소", "Cancel");
        ContinueButton.Content = L.T("계속", "Continue");
        SystemLanguage.Content = L.T("Windows 설정 따름 ({0})", "Use Windows language ({0})", L.SystemLocale.NativeName);
        sessionOnly = false;
        SaveError.Visibility = Visibility.Collapsed;
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (Languages.SelectedItem is not ListBoxItem { Tag: string choice })
            return;
        try
        {
            if (sessionOnly)
                L.SelectForSession(choice);
            else
                L.SavePreference(choice);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sessionOnly = true;
            ContinueButton.Content = L.T("이번 실행에만 적용", "Continue for this session");
            SaveError.Text = L.T("표시 언어를 저장하지 못했습니다. 다음에 실행하면 언어를 다시 선택할 수 있습니다.",
                "Could not save the display language. You can choose it again next time.");
            SaveError.Visibility = Visibility.Visible;
        }
    }
}
