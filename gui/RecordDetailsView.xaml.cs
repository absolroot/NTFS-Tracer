using System.Windows.Controls;

namespace JournalDesk;

public sealed record EvidenceField(string Label, string Value)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SecondaryValue
    {
        get; init;
    }
}

public partial class RecordDetailsView : UserControl
{
    public RecordDetailsView() => InitializeComponent();

    internal void SetFields(IEnumerable<EvidenceField> fields)
    {
        FieldList.ItemsSource = fields;
        FieldScroll.ScrollToTop();
    }
}
