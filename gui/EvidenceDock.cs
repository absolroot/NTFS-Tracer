using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace JournalDesk;

public sealed class EvidenceDock : Grid
{
    public static readonly DependencyProperty PreferRightProperty = DependencyProperty.RegisterAttached(
        "PreferRight", typeof(bool), typeof(EvidenceDock),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits,
            (element, _) => (element as EvidenceDock)?.UpdatePlacement()));

    public static bool GetPreferRight(DependencyObject element) => (bool)element.GetValue(PreferRightProperty);
    public static void SetPreferRight(DependencyObject element, bool value) => element.SetValue(PreferRightProperty, value);

    private const double MinimumSideWidth = 370;
    private readonly ColumnDefinition divider = new() { Width = new(0) };
    private readonly ColumnDefinition side = new() { Width = new(0) };
    private FrameworkElement? detail;
    private PaneSplitter? splitter;
    private int detailRow, splitterRow, originalColumn, firstSideRow;
    private double minimumListWidth, splitterHeight, preferredSideWidth = 490;
    private GridLength bottomDivider;
    private Action? fitBottom;
    private (GridLength Height, double Minimum, double Maximum) bottomSize;
    internal bool IsRight
    {
        get; private set;
    }

    internal void Connect(FrameworkElement pane, PaneSplitter sash, int listRow, double listWidth, Action? fit = null)
    {
        detail = pane;
        splitter = sash;
        detailRow = GetRow(pane);
        splitterRow = GetRow(sash);
        originalColumn = GetColumn(pane);
        firstSideRow = listRow;
        minimumListWidth = listWidth;
        splitterHeight = double.IsNaN(sash.Height) ? 4 : sash.Height;
        bottomDivider = RowDefinitions[splitterRow].Height;
        fitBottom = fit;
        if (ColumnDefinitions.Count == 0)
            ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(divider);
        ColumnDefinitions.Add(side);

        SizeChanged += (_, e) => { if (e.WidthChanged) UpdatePlacement(); };
        pane.IsVisibleChanged += (_, _) => UpdatePlacement();
        sash.DragCompleted += (_, e) => { if (IsRight && !e.Canceled) preferredSideWidth = side.ActualWidth; };
        sash.KeyUp += (_, e) =>
        {
            if (IsRight && e.Key is Key.Left or Key.Right)
                preferredSideWidth = side.Width.Value;
        };
        UpdatePlacement();
    }

    private void UpdatePlacement()
    {
        if (detail is null || splitter is null)
            return;
        double available = ActualWidth - minimumListWidth - splitterHeight;
        bool right = GetPreferRight(this) && available >= MinimumSideWidth;
        var row = RowDefinitions[detailRow];
        if (right != IsRight)
        {
            IsRight = right;
            if (right)
            {
                bottomSize = (row.Height, row.MinHeight, row.MaxHeight);
                row.MinHeight = 0;
                row.MaxHeight = 0;
                row.Height = new(0);
            }
            else
            {
                row.MaxHeight = bottomSize.Maximum;
                row.MinHeight = detail.Visibility == Visibility.Visible ? bottomSize.Minimum : 0;
                row.Height = detail.Visibility == Visibility.Visible ? bottomSize.Height : new(0);
            }

            SetRow(detail, right ? firstSideRow : detailRow);
            SetRow(splitter, right ? firstSideRow : splitterRow);
            SetRowSpan(detail, right ? detailRow - firstSideRow : 1);
            SetRowSpan(splitter, GetRowSpan(detail));
            SetColumn(detail, right ? ColumnDefinitions.Count - 1 : originalColumn);
            SetColumn(splitter, right ? ColumnDefinitions.Count - 2 : originalColumn);
            splitter.Height = right ? double.NaN : splitterHeight;
            splitter.ResizeDirection = right ? GridResizeDirection.Columns : GridResizeDirection.Rows;
            splitter.Cursor = right ? Cursors.SizeWE : Cursors.SizeNS;
            RowDefinitions[splitterRow].Height = right ? new(0) : bottomDivider;
            if (!right && fitBottom is { } fit)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, fit);
        }

        bool open = right && detail.Visibility == Visibility.Visible;
        divider.Width = new(open ? splitterHeight : 0);
        side.MinWidth = open ? MinimumSideWidth : 0;
        side.MaxWidth = open ? available : double.PositiveInfinity;
        side.Width = new(open ? Math.Clamp(preferredSideWidth, MinimumSideWidth, available) : 0);
    }
}
