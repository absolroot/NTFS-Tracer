using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JournalDesk;

[TemplatePart(Name = "PART_Copy", Type = typeof(Button))]
public sealed class CopyText : TextBox
{
    public static readonly DependencyProperty CellToolOnlyProperty = DependencyProperty.Register(
        nameof(CellToolOnly), typeof(bool), typeof(CopyText), new PropertyMetadata(false));
    public bool CellToolOnly
    {
        get => (bool)GetValue(CellToolOnlyProperty);
        set => SetValue(CellToolOnlyProperty, value);
    }
    public static readonly DependencyProperty CopyOnFocusProperty = DependencyProperty.RegisterAttached(
        "CopyOnFocus", typeof(bool), typeof(CopyText), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetCopyOnFocus(DependencyObject control, bool value) => control.SetValue(CopyOnFocusProperty, value);
    public static bool GetCopyOnFocus(DependencyObject control) => (bool)control.GetValue(CopyOnFocusProperty);
    private static readonly DependencyPropertyKey PointerCopyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PointerCopy), typeof(bool), typeof(CopyText), new PropertyMetadata(false));
    public static readonly DependencyProperty PointerCopyProperty = PointerCopyPropertyKey.DependencyProperty;
    public bool PointerCopy => (bool)GetValue(PointerCopyProperty);

    private Point pointerOrigin;
    private Button? copy;
    private DispatcherTimer? feedback;

    public CopyText()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        Unloaded += (_, _) => ResetFeedback();
        IsVisibleChanged += (_, _) => SetValue(PointerCopyPropertyKey, false);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        pointerOrigin = e.GetPosition(this);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.GetPosition(this) != pointerOrigin)
            SetValue(PointerCopyPropertyKey, true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetValue(PointerCopyPropertyKey, false);
    }

    public override void OnApplyTemplate()
    {
        ResetFeedback();
        if (copy != null)
            copy.Click -= CopyFullText;
        base.OnApplyTemplate();
        copy = GetTemplateChild("PART_Copy") as Button;
        if (copy == null)
            return;
        copy.Click += CopyFullText;
        ResetFeedback();
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (feedback?.IsEnabled == true)
            ResetFeedback();
    }

    private void CopyFullText(object sender, RoutedEventArgs e)
    {
        if (copy == null || Text.Length == 0)
            return;
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, Text);
            var copying = new DataObjectCopyingEventArgs(data, false);
            RaiseEvent(copying);
            e.Handled = true;
            if (copying.CommandCancelled)
                return;
            Clipboard.SetDataObject(data, true);
            copy.Content = "\uE73E";
            copy.ToolTip = CopyLabel("복사됨", "Copied");
        }
        catch (ExternalException)
        {
            copy.Content = "\uE7BA";
            copy.ToolTip = CopyLabel("클립보드에 접근할 수 없습니다. 다시 시도하세요.", "Clipboard unavailable. Try again.");
        }
        e.Handled = true;
        if (feedback == null)
        {
            feedback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            feedback.Tick += (_, _) => ResetFeedback();
        }
        feedback.Stop();
        feedback.Start();
    }

#if MAIL_VIEWER
    private string CopyLabel(string korean, string english) =>
        Language.IetfLanguageTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase) ? korean : english;
#else
    private static string CopyLabel(string _, string english) => english switch
    {
        "Copied" => L.T("복사됨", "Copied"),
        "Clipboard unavailable. Try again." => L.T("클립보드에 접근할 수 없습니다. 다시 시도하세요.", "Clipboard unavailable. Try again."),
        _ => L.T("전체 텍스트 복사", "Copy full text")
    };
#endif

    private void ResetFeedback()
    {
        feedback?.Stop();
        if (copy == null)
            return;
        string action = CopyLabel("전체 텍스트 복사", "Copy full text");
        copy.Content = "\uE8C8";
        copy.ToolTip = action;
        AutomationProperties.SetName(copy, action);
    }
}
