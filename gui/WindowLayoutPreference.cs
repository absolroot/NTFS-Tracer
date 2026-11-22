using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JournalDesk;

internal readonly record struct WindowLayout(double Width, double Height, bool Maximized)
{
    internal static WindowLayout? Parse(string? text)
    {
        if (text?.Split(' ') is not [var width, var height, var state]
            || !double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0
            || state is not ("Normal" or "Maximized"))
            return null;
        return new WindowLayout(w, h, state == "Maximized");
    }

    internal string Serialize() => FormattableString.Invariant($"{Width:R} {Height:R} {(Maximized ? "Maximized" : "Normal")}");

    internal WindowLayout Fit(double minWidth, double minHeight, double availableWidth, double availableHeight) =>
        this with
        {
            Width = Math.Clamp(Width, Math.Min(minWidth, availableWidth), availableWidth),
            Height = Math.Clamp(Height, Math.Min(minHeight, availableHeight), availableHeight)
        };
}

internal static class WindowLayoutPreference
{
    private const string FileName = "window-state.txt";

    internal static void Attach(Window window)
    {
        bool maximized = false;
        window.SourceInitialized += (_, _) =>
        {
            var restore = Read() ?? new WindowLayout(window.Width, window.Height, false);
            var area = WorkArea(window);
            var fitted = restore.Fit(window.MinWidth, window.MinHeight, area.Width, area.Height);
            window.MinWidth = Math.Min(window.MinWidth, area.Width);
            window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = fitted.Width;
            window.Height = fitted.Height;
            window.Left = area.Left + (area.Width - fitted.Width) / 2;
            window.Top = area.Top + (area.Height - fitted.Height) / 2;
            maximized = fitted.Maximized;
            if (maximized)
                window.WindowState = WindowState.Maximized;
        };
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized)
                maximized = window.WindowState == WindowState.Maximized;
        };
        window.Closing += (_, args) =>
        {
            if (args.Cancel || !window.IsLoaded || window.RestoreBounds is not { Width: > 0, Height: > 0 } bounds)
                return;
            try
            {
                DeskSettings.Write(FileName, new WindowLayout(bounds.Width, bounds.Height, maximized).Serialize());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    private static WindowLayout? Read()
    {
        try
        {
            return WindowLayout.Parse(DeskSettings.Read(FileName));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static Rect WorkArea(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref info) || HwndSource.FromHwnd(hwnd)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var work = info.Work;
        return Rect.Transform(new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), target.TransformFromDevice);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor, Work;
        internal uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
