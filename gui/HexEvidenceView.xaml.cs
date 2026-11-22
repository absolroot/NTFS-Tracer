using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JournalDesk;

public partial class HexEvidenceView : UserControl
{
    private byte[] bytes = [];
    private IReadOnlyList<HexField> fields = [];
    private HexField? comparisonField;
    private readonly DispatcherTimer clipboardFeedback = new() { Interval = TimeSpan.FromSeconds(3) };
    private int anchor = -1;
    private int contextOffset = -1;
    private HexField? contextField;
    private readonly DispatcherTimer dragScroll = new() { Interval = TimeSpan.FromMilliseconds(55) };
    public long BaseOffset
    {
        get; private set;
    }
    public string AddressLabel { get; set; } = L.T("원본 내 위치", "Source offset");
    public int ByteCount => bytes.Length;
    public int SelectedOffset { get; private set; } = -1;
    public int SelectionStart => Math.Min(anchor, SelectedOffset);
    internal int SelectionAnchor => anchor;
    public int SelectionLength => SelectedOffset < 0 ? 0 : Math.Abs(SelectedOffset - anchor) + 1;
    public string SelectedHex => SelectionLength == 0 ? "" : HexStrip.HexRun(bytes, SelectionStart, SelectionLength);
    public HexField? SelectedField
    {
        get; private set;
    }
    internal HexField? HighlightedField => comparisonField ?? SelectedField;
    public string SelectionSummary => SelectedOffset < 0 ? "" :
        $"{CursorValueLabel.Text} {CursorValue.Text}  {ByteValueLabel.Text} {ByteValue.Text}  Little-endian  UInt8 {UInt8.Text}  UInt16 {UInt16.Text}  UInt32 {UInt32.Text}  UInt64 {UInt64.Text}  {FieldValue.Text}";
    public string Text
    {
        get
        {
            if (bytes.Length == 0)
                return Message.Text;
            var output = new StringBuilder();
            string addressFormat = "X" + Body.AddressDigits.ToString(CultureInfo.InvariantCulture);
            for (int start = 0; start < bytes.Length; start += 16)
            {
                if (start > 0)
                    output.AppendLine();
                output.Append((BaseOffset + start).ToString(addressFormat, CultureInfo.InvariantCulture)).Append("  ")
                      .Append(HexStrip.HexRun(bytes, start, Math.Min(16, bytes.Length - start)).PadRight(47)).Append("  ")
                      .Append(HexStrip.AsciiRun(bytes, start, Math.Min(16, bytes.Length - start)));
            }
            return output.ToString();
        }
    }

    public HexEvidenceView()
    {
        InitializeComponent();
        Body.Reader = Columns.Reader = this;
        Body.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopySelection(Body, e); e.Handled = true; },
            (_, e) => { e.CanExecute = SelectionLength > 0; e.Handled = true; }));
        Body.CommandBindings.Add(new CommandBinding(ApplicationCommands.SelectAll,
            (_, e) => { SelectRange(0, bytes.Length - 1); e.Handled = true; },
            (_, e) => { e.CanExecute = bytes.Length > 0; e.Handled = true; }));
        dragScroll.Tick += (_, _) => { if (Body.IsMouseCaptured) ContinueDrag(Mouse.GetPosition(Body)); else dragScroll.Stop(); };
        clipboardFeedback.Tick += (_, _) => ClearCopyStatus();
        Unloaded += (_, _) => { ReleaseRange(); ClearCopyStatus(); };
        SizeChanged += (_, _) => ArrangeInspector();
        IsVisibleChanged += (_, _) => { if (IsVisible) Dispatcher.BeginInvoke(ScrollToSelection); };
        ClearView();
    }
    public void ShowBytes(byte[] bytes, long absoluteOffset, IReadOnlyList<HexField>? fields = null)
        => ShowBytes(bytes, absoluteOffset, fields, 0);

    internal void ShowBytes(byte[] bytes, long absoluteOffset, IReadOnlyList<HexField>? fields, int initialOffset)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > 65536 || absoluteOffset < 0 || (bytes.Length > 0 && absoluteOffset > long.MaxValue - (bytes.Length - 1)))
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset), L.T("원본의 위치 또는 표시 범위가 유효하지 않습니다.", "Invalid source offset or display range."));
        if (bytes.Length == 0)
        {
            ClearView(L.T("표시할 바이트가 없습니다.", "No bytes to display."));
            return;
        }
        ReleaseRange();
        this.bytes = bytes;
        BaseOffset = absoluteOffset;
        comparisonField = null;
        Inspector.Visibility = Visibility.Visible;
        ManualSelection.Visibility = Visibility.Collapsed;
        this.fields = (fields ?? []).Where(f => f.Start >= 0 && f.Length > 0 && f.Start <= bytes.Length - f.Length).ToArray();
        ClearCopyStatus();
        Reader.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        ArrangeInspector();
        Body.ResetExtent();
        Columns.ResetExtent();
        Viewport.ScrollToHorizontalOffset(0);
        SelectByte((uint)initialOffset < (uint)bytes.Length ? initialOffset : 0);
    }
    public void ClearView(string? message = null)
    {
        DismissCopyMenu();
        ReleaseRange();
        bytes = [];
        fields = [];
        BaseOffset = 0;
        anchor = SelectedOffset = -1;
        SelectedField = null;
        comparisonField = null;
        ManualSelection.Visibility = Visibility.Collapsed;
        ClearCopyStatus();
        FieldName.Text = FieldSize.Text = ByteValue.Text = CursorValue.Text = FieldValue.Text = "";
        UInt8.Text = UInt16.Text = UInt32.Text = UInt64.Text = "";
        Reader.Visibility = Visibility.Collapsed;
        Message.Text = message ?? "";
        Message.Visibility = Visibility.Visible;
        Body.ResetExtent();
        Columns.ResetExtent();
    }
    public void SelectByte(int offset) => SetRange(offset, offset, revealHexColumn: true);
    internal void FocusHexColumn() => Body.Focus();

    public void SelectRange(int anchorOffset, int caretOffset) => SetRange(anchorOffset, caretOffset, revealHexColumn: true);
    internal void CompareField(HexField field, bool showDetails)
    {
        comparisonField = field;
        SetComparisonDetails(showDetails);
        SelectRange(field.Start + field.Length - 1, field.Start);
    }
    internal void SetComparisonDetails(bool showDetails)
    {
        if (comparisonField == null)
            return;
        FieldControl.Visibility = Visibility.Collapsed;
        Inspector.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        ShowNumbers.IsExpanded = showDetails;
        ArrangeInspector();
        UpdateManualSelection();
    }
    private void UpdateManualSelection()
    {
        bool comparisonSelection = comparisonField != null &&
            (comparisonField.Start != SelectionStart || comparisonField.Length != SelectionLength);
        bool show = SelectedOffset >= 0 && (comparisonField == null ? SelectionLength > 1 :
            comparisonSelection && (SelectionLength > 1 || Inspector.Visibility != Visibility.Visible));
        ManualSelection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ManualSelection.Visibility != Visibility.Visible)
            return;
        ManualSelection.Text = SelectionLength == 1
            ? L.T("커서 위치 0x{0:X}   ·   값 0x{1:X2}", "Cursor 0x{0:X}   ·   Value 0x{1:X2}", BaseOffset + SelectedOffset, bytes[SelectedOffset])
            : L.T("선택 영역 {0:N0}바이트   ·   0x{1:X}–0x{2:X}", "Selection: {0:N0} bytes   ·   0x{1:X}–0x{2:X}", SelectionLength, BaseOffset + SelectionStart, BaseOffset + SelectionStart + SelectionLength - 1);
    }
    private void SetRange(int anchorOffset, int offset, bool revealHexColumn)
    {
        if (offset < 0 || offset >= bytes.Length || anchorOffset < 0 || anchorOffset >= bytes.Length)
            return;
        DismissCopyMenu();
        anchor = anchorOffset;
        SelectedOffset = offset;
        string copyLabel = L.T("선택한 {0:N0}바이트 복사", "Copy selected bytes ({0:N0})", SelectionLength);
        CopyBytesItem.Header = CopyBytesButton.ToolTip = copyLabel;
        AutomationProperties.SetName(CopyBytesButton, copyLabel);
        SelectedField = fields.FirstOrDefault(f => offset >= f.Start && offset - f.Start < f.Length);
        bool showField = comparisonField == null && SelectedField is { } current &&
            (SelectionLength == 1 || (SelectionStart == current.Start && SelectionLength == current.Length));
        var visibility = showField ? Visibility.Visible : Visibility.Collapsed;
        if (FieldControl.Visibility != visibility)
        {
            FieldControl.Visibility = visibility;
            ArrangeInspector();
        }
        FieldName.Text = showField ? SelectedField!.Label : "";
        FieldSize.Text = showField ? SelectedField!.Length.ToString("N0", CultureInfo.CurrentCulture) + L.Caption("Bytes") : "";
        FieldValue.Text = showField ? SelectedField!.Value : "";
        byte b = bytes[offset];
        string glyph = b is >= 32 and <= 126 ? $"'{(char)b}'" : L.T("표시 불가", "Not printable");
        CursorValue.Text = $"0x{BaseOffset + offset:X}";
        ByteValue.Text = $"0x{b:X2}";
        string location = $"{AddressLabel} 0x{BaseOffset + offset:X}";
        if (BaseOffset != 0)
            location += L.T("   ·   시작 기준 +0x{0:X}", "   ·   Relative offset +0x{0:X}", offset);
        CursorValue.ToolTip = location;
        AutomationProperties.SetHelpText(CursorValue, location);
        ByteValue.ToolTip = $"ASCII {glyph}";
        AutomationProperties.SetHelpText(ByteValue, $"ASCII {glyph}");
        UpdateIntegers();
        UpdateManualSelection();
        Body.InvalidateVisual();
        ScrollToSelection();
        if (revealHexColumn && Viewport.ViewportWidth > 0)
        {
            var (left, right) = Body.CellBounds(offset % 16);
            if (left < Viewport.HorizontalOffset)
                Viewport.ScrollToHorizontalOffset(Math.Max(0, left - 4));
            else if (right > Viewport.HorizontalOffset + Viewport.ViewportWidth)
                Viewport.ScrollToHorizontalOffset(right + 4 - Viewport.ViewportWidth);
        }
    }
    private void RevealIntegerValues(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ShowNumbers.BringIntoView());
    }
    private void UpdateIntegers()
    {
        var rest = bytes.AsSpan(SelectedOffset);
        UInt8.Text = rest[0].ToString(CultureInfo.InvariantCulture);
        UInt16.Text = rest.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(rest).ToString(CultureInfo.InvariantCulture) : "—";
        UInt32.Text = rest.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(rest).ToString(CultureInfo.InvariantCulture) : "—";
        UInt64.Text = rest.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(rest).ToString(CultureInfo.InvariantCulture) : "—";
    }
    public void SelectField(string label)
    {
        var field = fields.FirstOrDefault(f => f.Label == label);
        if (field != null)
            SelectByte(field.Start);
    }
    private void ArrangeInspector()
    {
        if (ActualWidth <= 0)
            return;
        bool shallow = ActualHeight is > 0 and < 118;
        double byteWidth = Math.Ceiling(Body.ByteTableWidth) + SystemParameters.VerticalScrollBarWidth;
        double inspectorMinimum = shallow ? 190 : 224;
        bool below = ActualWidth < byteWidth + inspectorMinimum && !(shallow && ActualWidth >= 440);
        bool showField = FieldControl.Visibility == Visibility.Visible;
        bool sideBySide = below && ActualWidth >= 430 && showField;
        bool besideBytes = !below && Inspector.Visibility == Visibility.Visible;
        ByteColumn.Width = besideBytes ? new GridLength(Math.Min(byteWidth, ActualWidth - inspectorMinimum)) : new GridLength(1, GridUnitType.Star);
        InspectorColumn.Width = besideBytes ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(Inspector, below ? 0 : 1);
        Grid.SetRow(Inspector, below ? 1 : 0);
        Inspector.MaxHeight = below ? Math.Clamp(ActualHeight - 21 - 3 * HexStrip.RowHeight, 36, ActualHeight * 0.55 + 36) : double.PositiveInfinity;
        Inspector.BorderThickness = below ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        SecondaryColumn.Width = sideBySide ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(CursorGroup, sideBySide ? 1 : 0);
        Grid.SetRow(CursorGroup, sideBySide ? 0 : 1);
        CursorGroup.Margin = sideBySide ? new Thickness(12, 0, 0, 0) : new Thickness(0, showField ? 4 : 0, 0, 0);
    }
    private void ScrollToSelection()
    {
        if (!IsVisible || SelectedOffset < 0 || Viewport.ViewportHeight <= 0)
            return;
        double top = (SelectedOffset / 16) * HexStrip.RowHeight, bottom = top + HexStrip.RowHeight;
        if (top < Viewport.VerticalOffset)
            Viewport.ScrollToVerticalOffset(top);
        else if (bottom > Viewport.VerticalOffset + Viewport.ViewportHeight)
            Viewport.ScrollToVerticalOffset(bottom - Viewport.ViewportHeight);
    }
    private void ViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        Columns.RenderTransform = new TranslateTransform(-e.HorizontalOffset, 0);
        Body.InvalidateVisual();
        if (e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
            ScrollToSelection();
    }
    private void BeginRange(object sender, MouseButtonEventArgs e)
    {
        int offset = Body.AtPoint(e.GetPosition(Body));
        if (offset < 0 || offset >= bytes.Length)
            return;
        Body.Focus();
        SetRange((Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && anchor >= 0) ? anchor : offset, offset, revealHexColumn: false);
        Body.CaptureMouse();
        e.Handled = true;
    }
    private void DragRange(object sender, MouseEventArgs e)
    {
        if (!Body.IsMouseCaptured)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleaseRange();
            return;
        }
        ContinueDrag(e.GetPosition(Body));
        e.Handled = true;
    }
    private void ContinueDrag(Point point)
    {
        if (bytes.Length == 0)
            return;
        double y = point.Y - Viewport.VerticalOffset;
        if (y < 0 || y >= Viewport.ViewportHeight)
        {
            double top = Math.Clamp(Viewport.VerticalOffset + (y < 0 ? -HexStrip.RowHeight : HexStrip.RowHeight), 0, Viewport.ScrollableHeight);
            Viewport.ScrollToVerticalOffset(top);
            point.Y = top + (y < 0 ? 0 : Math.Max(0, Viewport.ViewportHeight - 1));
            dragScroll.Start();
        }
        else
            dragScroll.Stop();
        int target = Body.AtPoint(point, clamp: true);
        target = Math.Clamp(target, 0, bytes.Length - 1);
        if (SelectedOffset != target)
            SetRange(anchor, target, revealHexColumn: false);
    }
    private void EndRange(object sender, MouseButtonEventArgs e)
    {
        if (!Body.IsMouseCaptured)
            return;
        ReleaseRange();
        e.Handled = true;
    }
    private void ReleaseRange()
    {
        dragScroll.Stop();
        if (Body.IsMouseCaptured)
            Body.ReleaseMouseCapture();
    }
    private void RangeCaptureLost(object sender, MouseEventArgs e) => dragScroll.Stop();
    private void DismissCopyMenu()
    {
        contextOffset = -1;
        contextField = null;
        if (Body.ContextMenu.IsOpen)
            Body.ContextMenu.IsOpen = false;
    }
    private void OpenCopyMenu(object sender, ContextMenuEventArgs e)
    {
        bool keyboard = e.CursorLeft == -1 && e.CursorTop == -1;
        var point = new Point(e.CursorLeft, e.CursorTop);
        if (!keyboard && e.OriginalSource is UIElement element && !ReferenceEquals(element, Body))
            point = element.TranslatePoint(point, Body);
        int offset = keyboard ? SelectedOffset : Body.AtPoint(point);
        if (offset < 0 || offset >= bytes.Length)
        {
            DismissCopyMenu();
            e.Handled = true;
            return;
        }
        Body.Focus();
        if (keyboard)
            ScrollToSelection();
        if (offset < SelectionStart || offset >= SelectionStart + SelectionLength)
            SetRange(offset, offset, revealHexColumn: false);
        contextOffset = offset;
        contextField = fields.FirstOrDefault(f => offset >= f.Start && offset - f.Start < f.Length);
        CopyAddressItem.Header = L.T("{0} 복사 (0x{1:X})", "Copy {0} (0x{1:X})", AddressLabel, BaseOffset + offset);
        FieldCopyDivider.Visibility = CopyValueItem.Visibility = CopyFieldItem.Visibility =
            contextField == null ? Visibility.Collapsed : Visibility.Visible;
        if (contextField is { } field)
        {
            CopyFieldItem.Header = L.T("{0} 바이트 복사 ({1:N0}바이트)", "Copy {0} bytes ({1:N0} bytes)", field.Label, field.Length);
            CopyValueItem.Header = L.T("{0} 해석값 복사", "Copy decoded {0} value", field.Label);
        }
        var menu = Body.ContextMenu;
        menu.PlacementTarget = Body;
        ContextMenuService.SetPlacement(Body, keyboard ? PlacementMode.RelativePoint : PlacementMode.MousePoint);
        menu.HorizontalOffset = keyboard ? Math.Clamp(Body.CellBounds(offset % 16).Left, Viewport.HorizontalOffset, Viewport.HorizontalOffset + Viewport.ViewportWidth) : 0;
        menu.VerticalOffset = keyboard ? Math.Clamp((offset / 16 + 1) * HexStrip.RowHeight, Viewport.VerticalOffset, Viewport.VerticalOffset + Viewport.ViewportHeight) : 0;
    }
    private void NavigateBytes(object sender, KeyEventArgs e)
    {
        if (bytes.Length == 0)
            return;
        var modifiers = e.KeyboardDevice.Modifiers;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        int line = SelectedOffset / 16, page = Math.Max(1, (int)(Viewport.ViewportHeight / HexStrip.RowHeight)) * 16;
        if (ctrl && e.Key == Key.C)
        {
            CopySelection(sender, e);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.A)
        {
            SelectRange(0, bytes.Length - 1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SelectionLength > 1)
        {
            SelectByte(SelectedOffset);
            e.Handled = true;
            return;
        }
        bool extend = (modifiers & ModifierKeys.Shift) != 0;
        if (!extend && !ctrl && SelectionLength > 1 && e.Key is Key.Left or Key.Right)
        {
            SelectByte(e.Key == Key.Left ? SelectionStart : SelectionStart + SelectionLength - 1);
            e.Handled = true;
            return;
        }
        int? target = e.Key switch
        {
            Key.Left => SelectedOffset - 1,
            Key.Right => SelectedOffset + 1,
            Key.Up => SelectedOffset - 16,
            Key.Down => SelectedOffset + 16,
            Key.Home => ctrl ? 0 : line * 16,
            Key.End => ctrl ? bytes.Length - 1 : line * 16 + 15,
            Key.PageUp => SelectedOffset - page,
            Key.PageDown => SelectedOffset + page,
            _ => null
        };
        if (!target.HasValue)
            return;
        int caret = Math.Clamp(target.Value, 0, bytes.Length - 1);
        SetRange(extend ? anchor : caret, caret, revealHexColumn: true);
        e.Handled = true;
    }
    private void PutClipboard(string value)
    {
        try
        {
            var data = new DataObject(DataFormats.UnicodeText, value);
            var copying = new DataObjectCopyingEventArgs(data, false);
            RaiseEvent(copying);
            if (copying.CommandCancelled)
                return;
            Clipboard.SetDataObject(data, true);
            ClearCopyStatus();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopyStatus.Text = L.T("클립보드를 사용할 수 없습니다. 다시 시도해 주세요.", "Clipboard unavailable. Try again.");
            CopyStatus.Visibility = Visibility.Visible;
            clipboardFeedback.Stop();
            clipboardFeedback.Start();
        }
    }
    private void ClearCopyStatus()
    {
        clipboardFeedback.Stop();
        CopyStatus.Visibility = Visibility.Collapsed;
    }
    private void CopyAddress(object sender, RoutedEventArgs e)
    {
        if (contextOffset >= 0)
            PutClipboard($"0x{BaseOffset + contextOffset:X}");
    }
    private void CopySelection(object sender, RoutedEventArgs e)
    {
        if (SelectedOffset >= 0 && (sender is not MenuItem || contextOffset >= 0))
            PutClipboard(SelectedHex);
    }
    private void CopyField(object sender, RoutedEventArgs e)
    {
        if (contextField is { } f)
            PutClipboard(HexStrip.HexRun(bytes, f.Start, f.Length));
    }
    private void CopyFieldValue(object sender, RoutedEventArgs e)
    {
        if (contextField is { } f)
            PutClipboard(f.Value);
    }
    private void CopyAll(object sender, RoutedEventArgs e)
    {
        if (contextOffset >= 0)
            PutClipboard(Text);
    }
    internal byte[] Buffer => bytes;
    internal double Top => Viewport.VerticalOffset;
    internal double VisibleHeight => Viewport.ViewportHeight;
}

// The strip paints only the viewport. A full 64 KiB record therefore uses the same number
// of WPF elements as an ordinary USN record, and both panes use exactly the same glyph metrics.
public sealed class HexStrip : Control
{
    internal const double RowHeight = 19;
    private static readonly Typeface Mono = new("Consolas");
    public bool Header
    {
        get; set;
    }
    internal HexEvidenceView? Reader
    {
        get; set;
    }
    private double Glyph => Label("0", Foreground ?? Brushes.Gray).WidthIncludingTrailingWhitespace;
    internal int AddressDigits => Reader is { ByteCount: > 0 } r ? Math.Max(8, (r.BaseOffset + (r.ByteCount - 1)).ToString("X", CultureInfo.InvariantCulture).Length) : 8;
    private double HexLeft(double c) => 9 + (AddressDigits + 2) * c;
    private double HexX(int column, double c) => HexLeft(c) + column * 3 * c + (column >= 8 ? c : 0);
    private double AsciiX(double c) => HexX(16, c) + c;
    internal (double Left, double Right) CellBounds(int column)
    {
        double c = Glyph, left = HexX(column, c) - 2;
        return (left, left + 2 * c + 4);
    }
    internal void ResetExtent()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }
    internal double ByteTableWidth
    {
        get
        {
            double c = Glyph;
            return AsciiX(c) + 16 * c + 8;
        }
    }
    protected override Size MeasureOverride(Size constraint)
    {
        return new Size(Math.Ceiling(ByteTableWidth), Header ? 20 : RowHeight * ((Reader?.ByteCount + 15 ?? 0) / 16));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new HexPeer(this);
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }
    internal int AtPoint(Point point, bool clamp = false)
    {
        double c = Glyph;
        int row = (int)Math.Floor(point.Y / RowHeight), column;
        if (point.X >= AsciiX(c) && point.X < AsciiX(c) + 16 * c)
            column = (int)((point.X - AsciiX(c)) / c);
        else
        {
            double local = point.X - HexLeft(c);
            if (local < 0)
                return clamp ? Math.Max(0, row) * 16 : -1;
            if (point.X >= HexX(15, c) + 3 * c)
                return clamp ? Math.Max(0, row) * 16 + 15 : -1;
            if (local >= 25 * c)
                local -= c;
            else if (local >= 24 * c)
                return clamp ? Math.Max(0, row) * 16 + 7 : -1;
            column = (int)(local / (3 * c));
        }
        return row < 0 || column >= 16 ? -1 : row * 16 + column;
    }
    private FormattedText Label(string text, Brush ink) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        Mono, 12, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private Brush Ink(string name, Brush fallback) => TryFindResource("Desk." + name) as Brush ?? fallback;
    internal static string HexRun(byte[] data, int start, int count) => BitConverter.ToString(data, start, count).Replace('-', ' ');
    internal static string AsciiRun(byte[] data, int start, int count) =>
        string.Create(count, (data, start), (chars, state) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                byte b = state.data[state.start + i];
                chars[i] = b is >= 32 and <= 126 ? (char)b : '.';
            }
        });
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Reader is not { } view)
            return;
        var ink = Foreground ?? SystemColors.ControlTextBrush;
        var muted = Ink("Muted", SystemColors.GrayTextBrush);
        var fieldFill = Ink("FieldHighlight", SystemColors.GradientInactiveCaptionBrush);
        var selectedFill = Ink("Selected", SystemColors.HighlightBrush);
        var outline = new Pen(Ink("Accent", SystemColors.HighlightBrush), IsKeyboardFocused ? 1.5 : 1);
        double c = Glyph, ascii = AsciiX(c);
        if (Header)
        {
            drawingContext.DrawText(Label("Offset", muted), new Point(8, 2));
            for (int i = 0; i < 16; i++)
                drawingContext.DrawText(Label(i.ToString("X2", CultureInfo.InvariantCulture), muted), new Point(HexX(i, c), 2));
            drawingContext.DrawText(Label("ASCII", muted), new Point(ascii, 2));
            return;
        }
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var data = view.Buffer;
        int first = Math.Max(0, (int)Math.Floor(view.Top / RowHeight));
        int last = Math.Min((data.Length + 15) / 16, (int)Math.Ceiling((view.Top + Math.Max(RowHeight, view.VisibleHeight)) / RowHeight) + 1);
        var field = view.HighlightedField;
        for (int row = first; row < last; row++)
        {
            int start = row * 16, count = Math.Min(16, data.Length - start);
            double y = row * RowHeight;
            if (field != null)
            {
                int from = Math.Max(0, field.Start - start), to = Math.Min(count, field.Start + field.Length - start);
                if (from < to)
                {
                    drawingContext.DrawRectangle(fieldFill, null, new Rect(HexX(from, c) - 2, y, HexX(to - 1, c) - HexX(from, c) + 2 * c + 4, RowHeight));
                    drawingContext.DrawRectangle(fieldFill, null, new Rect(ascii + from * c, y, (to - from) * c, RowHeight));
                }
            }
            if (view.SelectionLength > 1)
            {
                int from = Math.Max(0, view.SelectionStart - start), to = Math.Min(count, view.SelectionStart + view.SelectionLength - start);
                if (from < to)
                {
                    drawingContext.DrawRectangle(selectedFill, null, new Rect(HexX(from, c) - 2, y, HexX(to - 1, c) - HexX(from, c) + 2 * c + 4, RowHeight));
                    drawingContext.DrawRectangle(selectedFill, null, new Rect(ascii + from * c, y, (to - from) * c, RowHeight));
                }
            }
            int selected = view.SelectedOffset - start;
            if (selected >= 0 && selected < count)
            {
                drawingContext.DrawRoundedRectangle(selectedFill, outline, new Rect(HexX(selected, c) - 2, y + 1, 2 * c + 4, RowHeight - 2), 2, 2);
                drawingContext.DrawRectangle(selectedFill, outline, new Rect(ascii + selected * c, y + 1, c, RowHeight - 2));
            }
            drawingContext.DrawText(Label((view.BaseOffset + start).ToString("X" + AddressDigits, CultureInfo.InvariantCulture), muted), new Point(8, y + 2));
            drawingContext.DrawText(Label(HexRun(data, start, Math.Min(8, count)), ink), new Point(HexX(0, c), y + 2));
            if (count > 8)
                drawingContext.DrawText(Label(HexRun(data, start + 8, count - 8), ink), new Point(HexX(8, c), y + 2));
            drawingContext.DrawText(Label(AsciiRun(data, start, count), ink), new Point(ascii, y + 2));
        }
    }
    private sealed class HexPeer(HexStrip owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue && !owner.Header ? this : base.GetPattern(patternInterface);
        protected override string GetClassNameCore() => "HexByteGrid";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        public bool IsReadOnly => false;
        public double LargeChange => 16;
        public double SmallChange => 1;
        public double Maximum => Math.Max(0, (owner.Reader?.ByteCount ?? 0) - 1);
        public double Minimum => 0;
        public double Value => Math.Max(0, owner.Reader?.SelectedOffset ?? 0);
        public void SetValue(double value)
        {
            if (double.IsNaN(value) || value < 0 || value > Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            owner.Reader?.SelectByte((int)value);
        }
    }
}
