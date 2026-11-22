using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public partial class RecordSearchFields : UserControl
{
    private bool resetting;
    internal RecordSearch? Criteria { get; private set; } = new();
    internal bool HasInput => Criteria?.IsEmpty != true;
    public event EventHandler? Changed;

    public RecordSearchFields()
    {
        InitializeComponent();
        UpdateLabel();
        PropertyChangedEventManager.AddHandler(L.Text, LanguageChanged, nameof(LanguageCatalog.Language));
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateLabel();

    private void UpdateLabel()
    {
        int count = (string.IsNullOrWhiteSpace(PathInput.Text) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(FileInput.Text) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(ParentInput.Text) ? 0 : 1);
        string label = L.Caption("PathReferenceFilters");
        Section.Header = count == 0 ? label : $"{label} ({count})";
        Clear.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FieldChanged(object sender, TextChangedEventArgs e)
    {
        if (!resetting && ParentInput != null)
            ReadFields();
    }

    private void ReadFields()
    {
        bool fileValid = Parse(FileInput.Text, out var file);
        bool parentValid = Parse(ParentInput.Text, out var parent);
        var next = fileValid && parentValid ? new RecordSearch(PathInput.Text.Trim(), file, parent) : null;
        Error.Visibility = next == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateLabel();
        if (next == Criteria)
            return;
        Criteria = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Parse(string text, out NtfsReference? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!NtfsReference.TryParse(text, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    internal void Reset()
    {
        resetting = true;
        try
        {
            PathInput.Clear();
            FileInput.Clear();
            ParentInput.Clear();
        }
        finally { resetting = false; }
        ReadFields();
    }

    internal void SetId(RecordIdChoice choice)
    {
        Section.IsExpanded = true;
        var field = choice.IsParent ? ParentInput : FileInput;
        field.Text = choice.Value;
    }

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        Reset();
        PathInput.Focus();
    }
}
