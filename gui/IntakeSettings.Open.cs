using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using EvidenceImage;
using Microsoft.Win32;

namespace JournalDesk;

public partial class IntakeSettings
{
    private void OpenSavedInvestigation(object sender, RoutedEventArgs e) => Host?.OpenInvestigation(sender, e);
    private void ResumeSavedInvestigation(object sender, RoutedEventArgs e) => Host?.ResumeInvestigation(sender, e);
}

public sealed class IntakeShellIcon : IValueConverter
{
    public bool Folder
    {
        get; set;
    }
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path && !string.IsNullOrWhiteSpace(path) ? ShellIcons.For(path, Folder) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class IntakeSettings
{
    internal enum Role
    {
        Unknown, Journal, Mft, Log, Image, Skip
    }
    private sealed record RoleChoice(Role Role, string Label);
    private sealed class Assignment(string path, Role role, bool companion = false)
    {
        public string Path { get; } = path;
        public string FileName { get; } = System.IO.Path.GetFileName(path);
        public string ParentFolder { get; set; } = "";
        public bool Companion { get; } = companion;
        public Role Role { get; set; } = role;
        public IReadOnlyList<RoleChoice> Choices
        {
            get;
        } = [
            new(Role.Unknown,L.T("종류 선택","Choose type")), new(Role.Journal,"USN Journal ($J)"),
            new(Role.Mft,"$MFT"), new(Role.Log,"$LogFile"), new(Role.Image,L.T("디스크 이미지","Disk image")), new(Role.Skip,L.T("제외","Skip"))];
    }
    private List<Assignment>? pending;
    private bool showFileRows;

    internal static Role FileRole(string path)
    {
        string name = Path.GetFileName(path);
        if (IsJournal(name))
            return Role.Journal;
        if (name.Equals("$MFT", StringComparison.OrdinalIgnoreCase))
            return Role.Mft;
        if (name.Equals("$LogFile", StringComparison.OrdinalIgnoreCase))
            return Role.Log;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[512];
        var data = header[..input.ReadAtLeast(header, header.Length, false)];
        if (data.StartsWith("RSTR"u8))
            return Role.Log;
        if (data.Length >= 48 && data.StartsWith("FILE"u8) && BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) >= 0x28)
            return Role.Mft;
        return ImageFileTypes.IsImage(input, data) ? Role.Image : Role.Unknown;
    }

    private async void PickSources(object sender, RoutedEventArgs e) => await ChooseSourcesAsync();
    internal async Task ChooseSourcesAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = L.Caption("OpenSources"),
            Multiselect = true,
            Filter = L.T("모든 파일|*.*|증거 이미지|", "All files|*.*|Evidence images|") + ImageFileTypes.PickerPatterns,
            InitialDirectory = SourceDirectory() ?? ""
        };
        var owner = Window.GetWindow(this) ?? Host;
        if (picker.ShowDialog(owner) != true)
            return;
        if (Host != null && owner is not IntakeWindow)
            Host.SettingsTab.IsSelected = true;
        await OpenInputsAsync(picker.FileNames);
    }

    internal async Task OpenInputsAsync(string[] paths)
    {
        if (paths.Length == 0)
            return;
        try
        {
            if (paths.Length == 1 && Directory.Exists(paths[0]))
            {
                SelectFolder(paths[0]);
                return;
            }
            if (paths.Length > 1 && ImageFileTypes.FirstSelectedSegment(paths) is { } first)
            {
                await OpenImageAsync(first);
                return;
            }
            var items = await Task.Run(() => IdentifySources(paths));
            if (items is [{ Role: Role.Image } image])
            {
                await OpenImageAsync(image.Path);
                return;
            }
            StageAssignments(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ShowError(ex.Message); }
    }

    private static List<Assignment> IdentifySources(string[] paths)
    {
        var selected = paths.Select(path => new Assignment(path, FileRole(path))).ToList();
        if (selected is not [{ Role: Role.Journal or Role.Mft or Role.Log } source])
            return selected;

        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(source.Path))!);
        if (source.Role == Role.Journal)
        {
            if (directory.Name.Equals("$UsnJrnl", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                directory = directory.Parent;
            if (directory.Name.Equals("$Extend", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                directory = directory.Parent;
        }
        var companions = KnownFiles(directory.FullName)
            .Where(path => !path.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
            .Select(path => new Assignment(path, FileRole(path), companion: true))
            .Where(candidate => candidate.Role != source.Role)
            .GroupBy(candidate => candidate.Role)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single());
        selected.AddRange(companions);
        return selected;
    }

    private static bool HasDuplicateRoles(List<Assignment> items) =>
        items.GroupBy(item => item.Role).Any(group => group.Count() > 1);

    private static string[] KnownFiles(string directory)
    {
        var names = JournalNames.Concat(["$MFT", "$LogFile"])
            .Concat(JournalNames.Select(n => Path.Combine("$Extend", n))).Append(Path.Combine("$Extend", "$UsnJrnl", "$J"));
        return names.Select(n => Path.Combine(directory, n)).Where(File.Exists).ToArray();
    }

    private void StageAssignments(List<Assignment> items)
    {
        if (items.Any(i => i.Role is Role.Unknown or Role.Image) || HasDuplicateRoles(items))
        {
            foreach (var group in items.GroupBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                foreach (var item in group)
                    item.ParentFolder = System.IO.Path.GetDirectoryName(item.Path) ?? "";
            pending = items;
            SourceAssignments.ItemsSource = items;
            ShowError("");
            RefreshState();
            return;
        }
        ApplyFiles(items);
    }

    private void ApplyFiles(List<Assignment> items)
    {
        EndAssignments();
        ClearImage();
        string Get(Role role) => items.FirstOrDefault(i => i.Role == role)?.Path ?? "";
        SetSources(Get(Role.Journal), Get(Role.Mft), Get(Role.Log));
        foreach (var item in items.Where(i => i.Companion))
        {
            var field = item.Role switch
            {
                Role.Journal => JournalPath,
                Role.Mft => MftPath,
                _ => LogPath
            };
            System.Windows.Automation.AutomationProperties.SetHelpText(field, L.T("자동 추가", "Auto-added"));
        }
        ShowError("");
    }
    private static string AssignmentIssue(List<Assignment> selected)
    {
        if (selected.Count == 0)
            return L.T("분석할 파일을 하나 이상 선택하세요.", "Select at least one file to analyze.");
        if (selected.FirstOrDefault(item => item.Role == Role.Unknown) is { } unknown)
            return L.T("‘{0}’의 종류를 선택하거나 이 파일을 제외하세요.", "Choose a type for ‘{0}’ or skip this file.", unknown.FileName);
        if (HasDuplicateRoles(selected))
            return L.T("같은 종류의 파일이 여러 개입니다. 종류별로 파일을 하나씩 선택하세요.", "Several files have the same type. Select one file per type.");
        return selected.Count > 1 && selected.Any(item => item.Role == Role.Image)
            ? L.T("디스크 이미지는 하나씩 엽니다. 다른 파일은 제외하세요.", "Open one disk image at a time and skip the other files.") : "";
    }

    private void AssignmentChanged(object sender, DataTransferEventArgs e) => CheckAssignments();

    private List<Assignment>? CheckAssignments()
    {
        if (pending == null)
            return null;
        var selected = pending.Where(item => item.Role != Role.Skip).ToList();
        string issue = AssignmentIssue(selected);
        ConfirmAssignments.IsEnabled = issue.Length == 0;
        ShowError(issue);
        return issue.Length == 0 ? selected : null;
    }

    private async void ApplyAssignments(object sender, RoutedEventArgs e)
    {
        if (CheckAssignments() is not { } selected)
            return;
        if (selected is [{ Role: Role.Image } image])
            await OpenImageAsync(image.Path);
        else
            ApplyFiles(selected);
    }
    private void EndAssignments()
    {
        pending = null;
        SourceAssignments.ItemsSource = null;
    }
    private void CancelAssignments(object sender, RoutedEventArgs e)
    {
        EndAssignments();
        ShowError("");
        RefreshState();
    }
    private void RefreshSourceRows()
    {
        bool image = imageSession != null, assign = pending != null;
        bool empty = !assign && !image && !showFileRows;
        EmptySources.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SourceForm.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        foreach (FrameworkElement element in SourceForm.Children)
        {
            if (element == JournalHash || element == MftHash || element == LogHash)
                continue;
            bool visible = element == ResolveSources ? assign
                : !assign && (element == ImageFileRow || element == ImageFileRowLabel || element == ImageVolumeRow || element == ImageVolumeRowLabel || element == ImageFactsRow || element == ImageFactsLabel ? image : !image && showFileRows);
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
