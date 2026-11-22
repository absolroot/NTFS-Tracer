using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace JournalDesk;

public sealed class PageSizeOption(int size) : INotifyPropertyChanged
{
    private static readonly int[] sizes = [50, 100, 200, 500, 1000];
    private static int? preferred;
    public static int Preferred => preferred ??= Read();
    public static PageSizeOption[] Choices { get; } = Array.ConvertAll(sizes, static size => new PageSizeOption(size));
    public int Size { get; } = size;
    public string Caption => string.Format(CultureInfo.CurrentUICulture, CompositeFormat.Parse(L.Caption("RowsPerPageValue")), Size);
    public event PropertyChangedEventHandler? PropertyChanged;

    static PageSizeOption()
    {
        L.Text.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LanguageCatalog.Language))
                foreach (var option in Choices)
                    option.PropertyChanged?.Invoke(option, new(nameof(Caption)));
        };
    }

    public static readonly DependencyProperty SynchronizeProperty = DependencyProperty.RegisterAttached(
        "Synchronize", typeof(bool), typeof(PageSizeOption), new PropertyMetadata(false, SynchronizeChanged));
    public static bool GetSynchronize(DependencyObject obj) => (bool)obj.GetValue(SynchronizeProperty);
    public static void SetSynchronize(DependencyObject obj, bool value) => obj.SetValue(SynchronizeProperty, value);

    private static int Read()
    {
        try
        {
            if (int.TryParse(DeskSettings.Read("rows-per-page.txt"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int size) && sizes.Contains(size))
                return size;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 200;
    }

    internal static void Remember(int size)
    {
        if (Preferred == size || !sizes.Contains(size))
            return;
        preferred = size;
        try
        {
            DeskSettings.Write("rows-per-page.txt", size.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SynchronizeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ComboBox box || !(bool)e.NewValue)
            return;
        Window? owner = null;
        void Update()
        {
            if (box.IsLoaded && box.IsVisible && box.IsEnabled && box.SelectedValue is int current && current != Preferred)
                box.SelectedValue = Preferred;
        }
        void Activated(object? source, EventArgs args) => Update();
        box.Loaded += (_, _) =>
        {
            owner = Window.GetWindow(box);
            if (owner != null)
                owner.Activated += Activated;
            Update();
        };
        box.Unloaded += (_, _) =>
        {
            if (owner != null)
                owner.Activated -= Activated;
            owner = null;
        };
        box.IsVisibleChanged += (_, _) => Update();
        box.IsEnabledChanged += (_, _) => Update();
    }
}
