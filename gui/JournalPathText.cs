using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace JournalDesk;

public sealed class JournalPathPreference : INotifyPropertyChanged
{
    public static JournalPathPreference Current { get; } = new();
    private bool enabled = true;
    private JournalPathPreference()
    {
        try
        {
            enabled = DeskSettings.Read("full-paths.txt")?.Trim() != "0";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
                return;
            enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
            try
            {
                DeskSettings.Write("full-paths.txt", value ? "1" : "0");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class JournalPathSource(Journal journal, PathResolver? resolver, string? warning, bool carved)
{
    private readonly Dictionary<long, PathResult> completed = [];
    private readonly Queue<long> oldest = [];
    private readonly SemaphoreSlim[] workers = [new(1), new(1)];
    internal string? CachedPath(long position) => completed.TryGetValue(position, out var result) && result.HasKnownPath ? result.Display : null;
    internal string? CachedSource(long position) => completed.TryGetValue(position, out var result) ? result.SourceLabel : null;
    internal PathResult Resolve(Entry record, byte[] file) => journal.ResolveRecordPath(record, file, resolver, warning, carved);

    internal async Task<PathResult> ReadAsync(Entry record, byte[] file, CancellationToken stop)
    {
        if (completed.TryGetValue(record.Position, out var cached))
            return cached;
        // Path and source cells for the same record share one worker and cache entry.
        var worker = workers[HashCode.Combine(record.Position) & 1];
        await worker.WaitAsync(stop);
        try
        {
            if (completed.TryGetValue(record.Position, out cached))
                return cached;
            var value = await Task.Run(() => Resolve(record, file), stop);
            stop.ThrowIfCancellationRequested();
            if (completed.TryAdd(record.Position, value))
            {
                oldest.Enqueue(record.Position);
                if (oldest.Count > 1024)
                    completed.Remove(oldest.Dequeue());
            }
            return value;
        }
        finally { worker.Release(); }
    }
}

public sealed class JournalPathText : TextBlock
{
    private static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(JournalPathSource), typeof(JournalPathText),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, Changed));
    internal static void SetSource(FrameworkElement root, JournalPathSource? source)
    {
        if (!BindingOperations.IsDataBound(root, EnabledPathsProperty))
            root.SetBinding(EnabledPathsProperty, PathPreference);
        root.SetValue(ScopeProperty, source);
    }
    public static readonly DependencyProperty RecordProperty = DependencyProperty.Register(nameof(Record), typeof(Entry), typeof(JournalPathText), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty FileIdProperty = DependencyProperty.Register(nameof(FileId), typeof(object), typeof(JournalPathText), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ShortTextProperty = DependencyProperty.Register(nameof(ShortText), typeof(string), typeof(JournalPathText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty BasisOnlyProperty = DependencyProperty.Register(nameof(BasisOnly), typeof(bool), typeof(JournalPathText), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty EnabledPathsProperty = DependencyProperty.RegisterAttached(
        "EnabledPaths", typeof(bool), typeof(JournalPathText),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, Changed));
    public Entry? Record
    {
        get => (Entry?)GetValue(RecordProperty); set => SetValue(RecordProperty, value);
    }
    public object? FileId
    {
        get => GetValue(FileIdProperty); set => SetValue(FileIdProperty, value);
    }
    public string ShortText
    {
        get => (string)GetValue(ShortTextProperty); set => SetValue(ShortTextProperty, value);
    }
    public bool BasisOnly
    {
        get => (bool)GetValue(BasisOnlyProperty); set => SetValue(BasisOnlyProperty, value);
    }
    public static readonly DependencyProperty KeepPathEndProperty = DependencyProperty.Register(nameof(KeepPathEnd),
        typeof(bool), typeof(JournalPathText), new PropertyMetadata(false, Changed));

    public bool KeepPathEnd
    {
        get => (bool)GetValue(KeepPathEndProperty);
        set => SetValue(KeepPathEndProperty, value);
    }

    private CancellationTokenSource? pending;
    private bool refreshQueued;
    private string copyText = "";
    internal string FullText => copyText;

    [ThreadStatic] private static ContextMenu? sharedCopyMenu;
    private static readonly Binding PathPreference = new(nameof(JournalPathPreference.Enabled)) { Source = JournalPathPreference.Current };

    public JournalPathText()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => pending?.Cancel();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && KeepPathEnd)
                UpdateDisplayedText();
        };
        IsVisibleChanged += (_, _) => Refresh();
        ContextMenu = sharedCopyMenu ??= CreateCopyMenu();
    }
    private static ContextMenu CreateCopyMenu()
    {
        var copy = new MenuItem { Header = L.T("값 복사", "Copy value") };
        var menu = new ContextMenu { Items = { copy } };
        menu.Opened += (_, _) =>
        {
            copy.Header = L.T("값 복사", "Copy value");
            copy.IsEnabled = menu.PlacementTarget is JournalPathText { FullText.Length: > 0 };
        };
        copy.Click += (_, _) =>
        {
            if (menu.PlacementTarget is JournalPathText cell)
            {
                try
                {
                    Clipboard.SetText(cell.FullText);
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
            }
        };
        return menu;
    }
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is JournalPathText text)
            text.Refresh();
    }
    private void SetCellText(string value)
    {
        if (copyText == value)
            return;
        copyText = value;
        UpdateDisplayedText();
        AutomationProperties.SetName(this, value);
    }
    private void UpdateDisplayedText()
    {
        string displayed = copyText;
        double available = ActualWidth - Padding.Left - Padding.Right;
        if (KeepPathEnd && available > 0 && !Fits(displayed, available))
        {
            int separator = displayed.IndexOf('\\');
            while (separator >= 0)
            {
                string suffix = "…" + displayed[separator..];
                if (Fits(suffix, available))
                {
                    Text = suffix;
                    return;
                }
                separator = displayed.IndexOf('\\', separator + 1);
            }
            displayed = ShortText;
        }
        Text = displayed;
    }

    private bool Fits(string text, double width) => new FormattedText(text, CultureInfo.CurrentUICulture,
        FlowDirection, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
        VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace <= width;

    private void Refresh()
    {
        pending?.Cancel();
        if (!IsLoaded || !IsVisible)
            return;
        SetCellText(BasisOnly ? "" : ShortText ?? "");
        SetPathToolTip(copyText);
        AutomationProperties.SetHelpText(this, "");
        if (refreshQueued || !(bool)GetValue(EnabledPathsProperty))
            return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, ResolvePath);
    }

    private void SetPathToolTip(string text)
    {
        if (text.Length == 0)
        {
            ToolTip = null;
            return;
        }

        if (ToolTip is not ToolTip { Content: TextBlock content })
        {
            content = new TextBlock { MaxWidth = 540, TextWrapping = TextWrapping.Wrap };
            ToolTip = new ToolTip { Content = content };
        }
        content.Text = text;
    }

    private async void ResolvePath()
    {
        refreshQueued = false;
        var record = Record;
        if (!IsLoaded || !IsVisible || !(bool)GetValue(EnabledPathsProperty) ||
            FileId is not byte[] file || record == null || record.Version == 4 || record.Name.Length == 0 ||
            GetValue(ScopeProperty) is not JournalPathSource scope)
            return;
        using var request = new CancellationTokenSource();
        pending = request;
        try
        {
            var result = await scope.ReadAsync(record, file, request.Token);
            if (request.IsCancellationRequested)
                return;
            var values = result.ListValues(record.Name);
            SetCellText(BasisOnly ? values.Source : values.Path);
            SetPathToolTip(BasisOnly ? "" : values.Path);
            AutomationProperties.SetHelpText(this, result.DisplayBasis + " · " + result.Explanation);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            if (!request.IsCancellationRequested)
            {
                var values = PathResult.UnreadableListValues(record.Name);
                SetCellText(BasisOnly ? values.Source : values.Path);
                SetPathToolTip(BasisOnly ? "" : copyText);
            }
        }
        finally { if (ReferenceEquals(pending, request)) pending = null; }
    }
}
