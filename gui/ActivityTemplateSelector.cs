using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public sealed class ActivityTemplateSelector : DataTemplateSelector
{
    public DataTemplate? RecordTemplate
    {
        get; set;
    }
    public DataTemplate? RenameTemplate
    {
        get; set;
    }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is Activity { PairedName: not null } ? RenameTemplate : RecordTemplate;
}
