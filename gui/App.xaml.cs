using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
namespace JournalDesk;

internal enum AppearancePreference
{
    Windows, Light, Dark
}

public partial class App : Application
{
    public App() => L.Initialize();

    private static readonly (string Key, string Light, string Dark)[] Palette = [
        ("Canvas","#FFFFFF","#1F1F1F"),("Panel","#F8F8F8","#181818"),
        ("Inset","#F3F3F3","#242424"),("TabRail","#ECEEF1","#232527"),("TabRule","#CED2D7","#36393C"),("Rule","#DADADA","#383838"),
        ("Hairline","#EEEEEE","#2C2C2C"),("LogAlternate","#F7F7F7","#252525"),("Text","#242424","#D7D7D7"),
        ("Muted","#565C63","#C1C4C8"),("Disabled","#777777","#989898"),
        ("Accent","#005FB8","#3794FF"),("AccentText","#FFFFFF","#101820"),("Hover","#EBEDF0","#2A2D2E"),
        ("TabHover","#EBEDF0","#2D3033"),("TabHoverText","#242424","#D7D7D7"),
        ("Pressed","#DDDFE2","#373A3C"),("Selected","#DCEAF6","#264F78"),
        ("FieldHighlight","#D7EAF7","#273F50"),("Link","#245681","#8AC4FF"),("Warn","#FFF5DF","#382E1B"),
        ("WarnRule","#DFC899","#806331"),("WarnText","#704100","#F1D39A")
    ];
    public static bool IsDark
    {
        get; private set;
    }
    internal static AppearancePreference Appearance
    {
        get; private set;
    }
    internal static event EventHandler? AppearanceChanged;

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(EnableCellSelection));
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(ReleasePointerFocus), true);
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent, new RoutedEventHandler(SetUpButton));
        try
        {
            Appearance = DeskSettings.Read("appearance.txt") switch
            {
                "dark" => AppearancePreference.Dark,
                "light" => AppearancePreference.Light,
                _ => AppearancePreference.Windows
            };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Apply(UseDarkTheme());
        SystemEvents.UserPreferenceChanged += WindowsAppearanceChanged;
        if (L.ChooseOnStartup)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (new LanguageWindow().ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            MainWindow = null;
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
        base.OnStartup(e);
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, CellTextSelection> CellInteractions = new();
    private static void EnableCellSelection(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(window, e.OriginalSource))
        {
            ApplyWindowTheme(window);
            CellInteractions.GetValue(window, static w => new CellTextSelection(w));
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= WindowsAppearanceChanged;
        base.OnExit(e);
    }

    private static void ReleasePointerFocus(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window { Content: UIElement surface } window ||
            e.ChangedButton is not (MouseButton.Left or MouseButton.Right) ||
            Keyboard.FocusedElement is not DependencyObject focused || ReferenceEquals(focused, surface) ||
            !ReferenceEquals(Window.GetWindow(focused), window) || e.OriginalSource is not DependencyObject target)
            return;
        for (DependencyObject? node = target; node != null; node = FocusParent(node))
        {
            if (ReferenceEquals(node, focused) || node is MenuItem or ScrollBar or Thumb or ComboBoxItem)
                return;
        }
        if (!surface.Focusable)
        {
            surface.SetCurrentValue(UIElement.FocusableProperty, true);
            KeyboardNavigation.SetIsTabStop(surface, false);
        }
        Keyboard.Focus(surface);
    }

    private static DependencyObject? FocusParent(DependencyObject source) =>
        (source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null) ?? LogicalTreeHelper.GetParent(source);

    private static void SetUpButton(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !ReferenceEquals(e.OriginalSource, b))
            return;
        b.IsEnabledChanged -= ButtonEnabledChanged;
        b.IsEnabledChanged += ButtonEnabledChanged;
        SetButtonOpacity(b);
    }
    private static void ButtonEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => SetButtonOpacity((Button)sender);
    private static void SetButtonOpacity(Button button) => button.SetCurrentValue(UIElement.OpacityProperty, button.IsEnabled ? 1d : 0.45d);
    internal static void SetAppearance(AppearancePreference appearance)
    {
        if (Appearance == appearance)
            return;
        Appearance = appearance;
        Apply(UseDarkTheme());
        try
        {
            DeskSettings.Write("appearance.txt", appearance.ToString().ToLowerInvariant());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static bool UseDarkTheme() => Appearance switch
    {
        AppearancePreference.Dark => true,
        AppearancePreference.Light => false,
        _ => Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0
    };

    private void WindowsAppearanceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Appearance == AppearancePreference.Windows && UseDarkTheme() != IsDark)
                Apply(!IsDark);
        }, DispatcherPriority.Background);
    }

    private static void ApplyWindowTheme(Window window)
    {
#pragma warning disable WPF0001
        window.ThemeMode = IsDark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001
    }

    private static void Apply(bool dark)
    {
        IsDark = dark;
#pragma warning disable WPF0001
        Current.ThemeMode = dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001
        foreach (var (key, light, night) in Palette)
        {
            var brush = SystemParameters.HighContrast && key is ("TabRail" or "TabRule")
                ? (key == "TabRail" ? SystemColors.WindowBrush : SystemColors.WindowTextBrush)
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            Current.Resources["Desk." + key] = brush;
        }
        if (!SystemParameters.HighContrast)
        {
            SetFluentBrushes("Inset", "ButtonBackground", "ButtonBackgroundDisabled", "ComboBoxBackground", "ComboBoxBackgroundDisabled", "ComboBoxDropDownBackground");
            SetFluentBrushes("Hover", "ButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver", "ComboBoxItemBackgroundSelected");
            SetFluentBrushes("Pressed", "ButtonBackgroundPressed", "ComboBoxBackgroundPressed");
            SetFluentBrushes("Rule", "ButtonBorderBrush", "ButtonBorderBrushDisabled", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
                "ComboBoxBorderBrush", "ComboBoxBorderBrushDisabled", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed", "ComboBoxDropDownBorderBrush");
            SetFluentBrushes("Text", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ComboBoxForeground",
                "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed",
                "ComboBoxItemForegroundSelected", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed");
            SetFluentBrushes("Disabled", "ButtonForegroundDisabled", "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled");
            SetFluentBrushes("Accent", "ComboBoxItemPillFillBrush");
        }
        foreach (Window window in Current.Windows)
            ApplyWindowTheme(window);
        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void SetFluentBrushes(string color, params string[] keys)
    {
        var brush = Current.Resources["Desk." + color];
        foreach (var key in keys)
            Current.Resources[key] = brush;
    }
}
