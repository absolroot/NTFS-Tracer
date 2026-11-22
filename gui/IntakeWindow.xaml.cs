using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace JournalDesk;

public partial class IntakeWindow : Window
{
    private readonly IntakeSettings setup;
    private readonly Action cancelWork;
    private bool openingResults;

    internal IntakeWindow(MainWindow owner, IntakeSettings setup, Action cancelWork)
    {
        InitializeComponent();
        Owner = owner;
        this.setup = setup;
        this.cancelWork = cancelWork;
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 24));
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 24));
        ProgressBar.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = owner.CancelButton });
        ProgressText.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = owner.Status });
        SettingsHost.Content = setup;
    }

    internal void DetachSettings() => SettingsHost.Content = null;

    internal void OpenResults()
    {
        openingResults = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!openingResults && ProgressBar.Visibility == Visibility.Visible)
        {
            e.Cancel = true;
            cancelWork();
        }
    }

    private void CancelWork(object sender, RoutedEventArgs e) => cancelWork();

    private void Dismiss(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && setup.IsEnabled)
        {
            e.Handled = true;
            _ = setup.ChooseSourcesAsync();
        }
    }

    protected override void OnPreviewDragOver(DragEventArgs e)
    {
        base.OnPreviewDragOver(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = setup.IsEnabled && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
    }

    protected override async void OnPreviewDrop(DragEventArgs e)
    {
        base.OnPreviewDrop(e);
        if (setup.IsEnabled && (e.AllowedEffects & DragDropEffects.Copy) != 0 && e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            e.Effects = DragDropEffects.Copy;
            await setup.OpenInputsAsync(paths);
        }
    }
}
