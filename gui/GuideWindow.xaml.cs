using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace JournalDesk;

public partial class GuideWindow : Window
{
    private GuideTopic[] topics = GuideContent.Topics();

    public GuideWindow()
    {
        InitializeComponent();
        Language = L.XmlLanguage;
        Topics.ItemsSource = topics;
        Topics.SelectedIndex = 0;
        Loaded += (_, _) => (Topics.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
    }

    internal void RefreshLanguage()
    {
        var selected = Topics.SelectedItem as GuideTopic;
        int index = Array.IndexOf(topics, selected);
        Language = L.XmlLanguage;
        topics = GuideContent.Topics();
        FilterTopics(TopicSearch, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
        if (index >= 0 && Topics.Items.Contains(topics[index]))
            Topics.SelectedItem = topics[index];
    }

    private void FilterTopics(object sender, TextChangedEventArgs e)
    {
        var query = TopicSearch.Text.Trim();
        var previous = Topics.SelectedItem as GuideTopic;
        var matches = query.Length == 0 ? topics : topics.Where(topic => topic.Matches(query)).ToArray();
        Topics.SelectionChanged -= ShowTopic;
        Topics.ItemsSource = matches;
        Topics.SelectedItem = previous != null && matches.Contains(previous) ? previous : matches.FirstOrDefault();
        Topics.SelectionChanged += ShowTopic;
        DisplayTopic();
        NoTopics.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearGuideSearch(object sender, RoutedEventArgs e)
    {
        TopicSearch.Clear();
        TopicSearch.Focus();
    }

    private void GuideKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TopicSearch.Focus();
            TopicSearch.SelectAll();
        }
        else if (e.Key == Key.Escape)
        {
            if (TopicSearch.Text.Length == 0)
                Close();
            else
                TopicSearch.Clear();
        }
        else if (TopicSearch.IsKeyboardFocused && e.Key is Key.Down or Key.Enter && Topics.SelectedIndex >= 0)
        {
            if (Topics.ItemContainerGenerator.ContainerFromIndex(Topics.SelectedIndex) is ListBoxItem item)
                item.Focus();
        }
        else
            return;
        e.Handled = true;
    }

    private void ShowTopic(object sender, SelectionChangedEventArgs e) => DisplayTopic();

    private void DisplayTopic()
    {
        if (Topics.SelectedItem is not GuideTopic topic)
        {
            Reader.Document = null;
            return;
        }
        var document = new FlowDocument
        {
            PagePadding = new Thickness(20, 16, 20, 16),
            MaxPageWidth = 690,
            FontFamily = FontFamily,
            FontSize = 14,
            LineHeight = 21,
            TextAlignment = TextAlignment.Left,
            Language = L.XmlLanguage
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Desk.Text");
        var title = Paragraph(topic.Title, 22, FontWeights.SemiBold, new Thickness(0, 0, 0, 6));
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        document.Blocks.Add(title);
        foreach (var part in topic.Parts)
        {
            if (part.Heading.Length > 0)
            {
                var heading = Paragraph(part.Heading, 15, FontWeights.SemiBold, new Thickness(0, 14, 0, 4));
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
                document.Blocks.Add(heading);
            }
            if (part.Path is { } path)
            {
                var location = Paragraph(path, 14, FontWeights.Normal, new Thickness(0, 0, 0, 3));
                location.FontFamily = new FontFamily("Consolas");
                document.Blocks.Add(location);
            }
            document.Blocks.Add(Paragraph(part.Text, 14, FontWeights.Normal, new Thickness(0, 2, 0, 5)));
        }
        Reader.Document = document;
    }

    private Paragraph Paragraph(string text, double size, FontWeight weight, Thickness margin)
    {
        var paragraph = new Paragraph { FontSize = size, FontWeight = weight, Margin = margin };
        var query = TopicSearch.Text.Trim();
        int start = 0;
        int match;
        while (query.Length > 0 && (match = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            paragraph.Inlines.Add(new Run(text[start..match]));
            var hit = new Run(text.Substring(match, query.Length));
            hit.SetResourceReference(TextElement.BackgroundProperty, "Desk.FieldHighlight");
            paragraph.Inlines.Add(hit);
            start = match + query.Length;
        }
        paragraph.Inlines.Add(new Run(text[start..]));
        return paragraph;
    }
}
