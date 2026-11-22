using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace JournalDesk;

public sealed class HintTextBox : TextBox
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(HintTextBox),
        new PropertyMetadata("", (sender, _) => ((HintTextBox)sender).RefreshHint()));
    public string Hint
    {
        get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value);
    }

    private AdornerLayer? layer;
    private HintAdorner? hint;

    public HintTextBox()
    {
        Loaded += (_, _) =>
        {
            layer = AdornerLayer.GetAdornerLayer(this);
            if (layer == null || hint != null)
                return;
            hint = new HintAdorner(this) { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
            layer.Add(hint);
            LayoutUpdated += PositionHint;
            RefreshHint();
        };
        Unloaded += (_, _) =>
        {
            LayoutUpdated -= PositionHint;
            if (hint != null)
                layer?.Remove(hint);
            hint = null;
            layer = null;
        };
        TextChanged += (_, _) => RefreshHint();
        IsKeyboardFocusWithinChanged += (_, _) => RefreshHint();
        IsVisibleChanged += (_, _) => RefreshHint();
    }

    private void RefreshHint()
    {
        if (hint == null)
            return;
        hint.Visibility = IsVisible && Text.Length == 0 && !IsKeyboardFocusWithin && Hint.Length != 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (hint.Visibility == Visibility.Visible)
            hint.UpdateOrigin();
    }

    private void PositionHint(object? sender, EventArgs e)
    {
        if (hint?.IsVisible == true)
            hint.UpdateOrigin();
    }

    private sealed class HintAdorner : Adorner
    {
        private readonly HintTextBox box;
        private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private Rect caret = Rect.Empty;

        internal HintAdorner(HintTextBox box) : base(box)
        {
            this.box = box;
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Hint)) { Source = box });
            foreach (var property in new[]{ TextBlock.FontFamilyProperty, TextBlock.FontWeightProperty,
                TextBlock.FontStyleProperty, TextBlock.FontStretchProperty, TextBlock.FontSizeProperty,
                LanguageProperty, FlowDirectionProperty })
                label.SetBinding(property, new Binding(property.Name) { Source = box });
            label.SetResourceReference(TextBlock.ForegroundProperty, "Desk.Muted");
            AddVisualChild(label);
        }
        internal void UpdateOrigin()
        {
            if (!box.IsArrangeValid)
                return;
            Rect next = box.GetRectFromCharacterIndex(0);
            if (next == caret)
                return;
            caret = next;
            InvalidateArrange();
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => label;
        protected override Size MeasureOverride(Size available)
        {
            label.Measure(box.RenderSize);
            return box.RenderSize;
        }
        protected override Size ArrangeOverride(Size size)
        {
            if (caret.IsEmpty)
                label.Arrange(new Rect(0, 0, 0, 0));
            else
                label.Arrange(new Rect(caret.X, caret.Y,
                Math.Max(0, size.Width - box.Padding.Right - caret.X), label.DesiredSize.Height));
            return size;
        }
    }
}
