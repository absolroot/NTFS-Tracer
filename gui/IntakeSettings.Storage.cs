using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace JournalDesk;

public partial class IntakeSettings
{
    private (string Saved, string Default, TextBlock Notice, Button Reset) StorageState(TextBox field) =>
        ReferenceEquals(field, IndexPath)
            ? (DeskSettings.IndexDirectory, DeskSettings.DirectoryPath, IndexNotice, ResetIndexFolder)
            : ReferenceEquals(field, ExtractionPath)
                ? (DeskSettings.ExtractionDirectory, DefaultExtractionRoot, ExtractionNotice, ResetExtractionFolder)
                : (DeskSettings.OutputDirectory, "", OutputNotice, ResetOutputFolder);

    private void RefreshStoragePaths()
    {
        ExtractionSettings.Visibility = imageSession != null ? Visibility.Visible : Visibility.Collapsed;
        RefreshStoragePath(IndexPath);
        if (imageSession != null)
            RefreshStoragePath(ExtractionPath);
        RefreshStoragePath(OutputPath);
    }

    private void RefreshStoragePath(TextBox field)
    {
        var (saved, fallback, notice, reset) = StorageState(field);
        field.Text = saved.Length > 0 ? saved : fallback;
        bool canReset = saved.Length > 0 && !string.Equals(saved, fallback, StringComparison.OrdinalIgnoreCase);
        reset.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
        notice.Text = !canReset || Directory.Exists(saved) ? ""
            : ReferenceEquals(field, OutputPath)
                ? L.T("저장 폴더에 접근할 수 없어 Windows 기본 위치를 사용합니다.", "The save folder is unavailable. Windows will use its default location.")
                : L.T("사용할 수 있는 저장 폴더를 선택하세요.", "Choose an available output folder.");
    }

    private bool CommitStoragePath(TextBox field)
    {
        var (saved, fallback, notice, _) = StorageState(field);
        var previous = saved.Length > 0 ? saved : fallback;
        string path = field.Text.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];
        if (string.Equals(path, previous, StringComparison.Ordinal))
        {
            if (field.Text != previous || notice.Text.Length != 0)
                RefreshStoragePath(field);
            return true;
        }
        return SaveStoragePath(field, path);
    }

    private bool SaveStoragePath(TextBox field, string path)
    {
        try
        {
            if (ReferenceEquals(field, IndexPath))
                DeskSettings.IndexDirectory = path;
            else if (ReferenceEquals(field, ExtractionPath))
                DeskSettings.ExtractionDirectory = path;
            else
                DeskSettings.OutputDirectory = path;
            RefreshStoragePath(field);
            RefreshState();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StorageState(field).Notice.Text = ex.Message;
            return false;
        }
    }

    private void FinishStorageEdit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox field && !field.IsKeyboardFocusWithin)
            CommitStoragePath(field);
    }

    private void StorageKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox field || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (e.Key == Key.Enter)
        {
            CommitStoragePath(field);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            var (saved, fallback, _, _) = StorageState(field);
            if (field.Text == (saved.Length > 0 ? saved : fallback))
                return;
            RefreshStoragePath(field);
            e.Handled = true;
        }
    }

    private void PickStorageFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBox field } button)
            return;
        var (saved, fallback, _, _) = StorageState(field);
        string initial = saved.Length > 0 ? saved : fallback;
        var picker = new OpenFolderDialog
        {
            Title = AutomationProperties.GetName(button),
            InitialDirectory = Directory.Exists(initial) ? initial : ""
        };
        if (picker.ShowDialog(Window.GetWindow(this)) == true)
            SaveStoragePath(field, picker.FolderName);
    }

    private void ResetStorageFolder(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TextBox field })
            SaveStoragePath(field, "");
    }
}
