using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace JournalDesk;

// Keep ordinary clicks and double-clicks with the row. A horizontal drag opens
// one transient text editor, so virtualized lists need no editor per cell.
internal sealed class CellTextSelection
{
    private TextBlock? pending;
    private Point start;
    private CellOverlay? overlay;
    private AdornerLayer? layer;
    private int anchor;
    private bool dragging;

    internal CellTextSelection(Window window)
    {
        window.PreviewMouseMove += Move;
        window.PreviewMouseLeftButtonDown += Down;
        window.PreviewMouseLeftButtonUp += Up;
        window.MouseLeave += (_, _) => { if (!dragging && overlay?.Editing != true) Clear(); };
        window.Deactivated += (_, _) => Clear();
        window.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }

    private static DependencyObject? Parent(DependencyObject item) =>
        item is ContentElement content ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent :
        item is Visual ? VisualTreeHelper.GetParent(item) : null;

    private static TextBlock? CellText(object source)
    {
        TextBlock? label = null;
        for (var part = source as DependencyObject; part != null; part = Parent(part))
        {
            if (part is ButtonBase or TextBoxBase or ComboBoxItem or Hyperlink)
                return null;
            if (part is TextBlock block)
                label = block;
            if (part is DataGridCell or ListBoxItem)
                return label is { Text.Length: > 0 } && label.Name != "MissingValue" ? label : null;
        }
        return null;
    }

    private void Down(object sender, MouseButtonEventArgs e)
    {
        if (overlay?.IsMouseOver == true)
            return;
        Clear();
        pending = e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None ? CellText(e.OriginalSource) : null;
        if (pending != null)
            start = e.GetPosition(pending);
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (!dragging && pending is { IsLoaded: true, IsVisible: true } block)
            {
                Vector motion = e.GetPosition(block) - start;
                if (Math.Abs(motion.X) < SystemParameters.MinimumHorizontalDragDistance || Math.Abs(motion.X) < Math.Abs(motion.Y))
                    return;
                // CaptureMouse synchronously re-enters Move on Windows.
                pending = null;
                if (!Attach(block, true))
                    return;
                var editor = overlay!.Editor;
                editor.Focus();
                anchor = CharacterAt(editor, block.TranslatePoint(start, editor));
                dragging = true;
                if (!editor.CaptureMouse())
                {
                    Clear();
                    return;
                }
            }
            if (dragging && overlay != null)
            {
                var editor = overlay.Editor;
                int end = CharacterAt(editor, e.GetPosition(editor));
                editor.Select(Math.Min(anchor, end), Math.Abs(end - anchor));
                e.Handled = true;
            }
            return;
        }
        pending = null;
        if (overlay?.Editing == true || overlay?.IsMouseOver == true)
            return;
        var cell = CellText(e.OriginalSource);
        if (cell == overlay?.AdornedElement)
            return;
        Clear();
        if (cell != null && CanShowCopy(cell))
            Attach(cell, false);
    }

    private static bool CanShowCopy(TextBlock block)
    {
        if (block.ActualWidth < 48)
            return false;
        if (block.Padding.Right >= 22)
            return true;
        if (block is JournalPathText || block.TextWrapping != TextWrapping.NoWrap || block.TextAlignment is TextAlignment.Right or TextAlignment.Justify)
            return false;
        var font = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var text = new FormattedText(block.Text, block.Language.GetEquivalentCulture(), block.FlowDirection, font,
            block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return block.ActualWidth >= text.WidthIncludingTrailingWhitespace + block.Padding.Left + (block.TextAlignment == TextAlignment.Center ? 52 : 26);
    }

    private static int CharacterAt(TextBox editor, Point point)
    {
        int index = editor.GetCharacterIndexFromPoint(point, true);
        if (index < 0)
            return 0;
        var rect = editor.GetRectFromCharacterIndex(index, true);
        return Math.Min(editor.Text.Length, point.X >= rect.X ? index + 1 : index);
    }

    private void Up(object sender, MouseButtonEventArgs e)
    {
        pending = null;
        if (!dragging || overlay == null)
            return;
        dragging = false;
        overlay.Editor.ReleaseMouseCapture();
        e.Handled = true;
    }

    private bool Attach(TextBlock block, bool edit)
    {
        layer = AdornerLayer.GetAdornerLayer(block);
        if (layer == null)
            return false;
        overlay = new CellOverlay(block, edit);
        layer.Add(overlay);
        block.Unloaded += CellUnloaded;
        block.DataContextChanged += Recycled;
        if (edit)
            overlay.Editor.LostKeyboardFocus += LeftEditor;
        overlay.UpdateLayout();
        return true;
    }

    private void CellUnloaded(object sender, RoutedEventArgs e) => Clear();
    private void Recycled(object sender, DependencyPropertyChangedEventArgs e) => Clear();
    private void LeftEditor(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (overlay is { } active && (e.NewFocus is not Visual next || !active.IsAncestorOf(next)))
            Clear();
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (overlay != null && e.OriginalSource is ScrollViewer scroll &&
            (e.HorizontalChange != 0 || e.VerticalChange != 0) && scroll.IsAncestorOf(overlay.AdornedElement))
            Clear();
    }

    private void Clear()
    {
        pending = null;
        dragging = false;
        if (overlay == null)
            return;
        var old = overlay;
        overlay = null;
        var block = (TextBlock)old.AdornedElement;
        block.Unloaded -= CellUnloaded;
        block.DataContextChanged -= Recycled;
        old.Editor.LostKeyboardFocus -= LeftEditor;
        if (old.Editor.IsMouseCaptured)
            old.Editor.ReleaseMouseCapture();
        layer?.Remove(old);
    }

    private sealed class CellOverlay : Adorner
    {
        internal CopyText Editor { get; } = new();
        internal bool Editing
        {
            get;
        }

        internal CellOverlay(TextBlock block, bool editing) : base(block)
        {
            Editing = editing;
            ClipToBounds = true;
            Editor.Text = block is JournalPathText path ? path.FullText : block.Text;
            Editor.FontFamily = block.FontFamily;
            Editor.FontSize = block.FontSize;
            Editor.FontWeight = block.FontWeight;
            Editor.TextAlignment = block.TextAlignment;
            Editor.TextWrapping = block.TextWrapping;
            Editor.Padding = editing ? new Thickness(block.Padding.Left, block.Padding.Top, Math.Max(0, block.Padding.Right - 22), block.Padding.Bottom) : new Thickness(0);
            Editor.MinHeight = 0;
            if (editing)
            {
                DependencyObject? row = block;
                while (row != null && row is not (DataGridRow or ListBoxItem))
                    row = Parent(row);
                Editor.SetResourceReference(Control.BackgroundProperty,
                    row != null && Selector.GetIsSelected(row) ? "Desk.Selected" : "Desk.Canvas");
            }
            else
                Editor.Background = Brushes.Transparent;
            Editor.CellToolOnly = !editing;
            Editor.PreviewMouseLeftButtonDown += (_, _) =>
            {
                if (!editing)
                    Editor.Text = block is JournalPathText current ? current.FullText : block.Text;
            };
            AddVisualChild(Editor);
            AddLogicalChild(Editor);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => Editor;
        protected override Size MeasureOverride(Size constraint)
        {
            Size size = AdornedElement.RenderSize;
            Editor.Measure(new Size(Editing ? size.Width : Math.Min(22, size.Width), size.Height));
            return size;
        }
        protected override Size ArrangeOverride(Size size)
        {
            double width = Editing ? size.Width : Math.Min(22, size.Width);
            Editor.Arrange(new Rect(size.Width - width, 0, width, size.Height));
            return size;
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => null;
    }
}
