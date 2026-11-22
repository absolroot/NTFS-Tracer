using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace JournalDesk;

public sealed class CopyTextValue : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CopyTextValue), new PropertyMetadata("", TextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public CopyTextValue()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = CreateField("");
    }

    private static void TextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var control = (CopyTextValue)element;
        string text = e.NewValue as string ?? "";
        if (control.Content is CopyText { IsKeyboardFocusWithin: true } field)
        {
            field.Text = text;
            return;
        }
        // Set the text before the field is attached to Windows text services.
        control.Content = control.CreateField(text);
    }

    private CopyText CreateField(string text)
    {
        var field = new CopyText { Text = text };
        field.SetBinding(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        field.SetBinding(AutomationProperties.NameProperty, new Binding { Source = this, Path = new PropertyPath("(0)", AutomationProperties.NameProperty) });
        return field;
    }
}
