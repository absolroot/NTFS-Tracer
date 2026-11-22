using System.IO;

namespace JournalDesk;

internal static class DeskSettings
{
    internal static string DirectoryPath
    {
        get; set;
    } = DefaultDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string DefaultDirectory(string appData)
    {
        string current = Path.Combine(appData, "NtfsTracer");
        string legacy = Path.Combine(appData, "JournalDesk");
        return Directory.Exists(current) || !Directory.Exists(legacy) ? current : legacy;
    }

    internal static string IndexDirectory
    {
        get
        {
            try
            {
                if (Read("index-directory.txt") is { Length: > 0 } saved && Path.IsPathFullyQualified(saved))
                    return saved;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return DirectoryPath;
        }
        set
        {
            string path = ValidateFolder(value);
            if (path.Length != 0)
            {
                string probe = Path.Combine(path, Guid.NewGuid().ToString("N") + ".tmp");
                using var test = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            Write("index-directory.txt", path);
        }
    }

    internal static string IndexStore => Path.Combine(IndexDirectory, "v1");

    internal static string OutputDirectory
    {
        get
        {
            try
            {
                return Read("output-directory.txt") ?? "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
        set
        {
            var path = ValidateFolder(value);
            if (Read("extraction-directory.txt") == null)
                Write("extraction-directory.txt", OutputDirectory);
            Write("output-directory.txt", path);
        }
    }

    internal static string ExtractionDirectory
    {
        get
        {
            try
            {
                return Read("extraction-directory.txt") ?? OutputDirectory;
            }
            catch (IOException) { return OutputDirectory; }
            catch (UnauthorizedAccessException) { return OutputDirectory; }
        }
        set => Write("extraction-directory.txt", ValidateFolder(value));
    }

    private static string ValidateFolder(string value)
    {
        var path = value.Trim();
        if (path.Length == 0)
            return "";
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException(L.T("사용할 수 있는 저장 폴더를 선택하세요.", "Choose an available output folder."));
        return Path.GetFullPath(path);
    }

    internal static string InitialExportDirectory
    {
        get
        {
            var path = OutputDirectory;
            return Path.IsPathFullyQualified(path) && Directory.Exists(path) ? path : "";
        }
    }

    internal static IndexingMode PreferredIndexingMode
    {
        get
        {
            try
            {
                return Read("indexing-mode.txt") switch
                {
                    nameof(IndexingMode.Sequential) => IndexingMode.Sequential,
                    nameof(IndexingMode.Parallel) => IndexingMode.Parallel,
                    _ => IndexingMode.Auto
                };
            }
            catch (IOException) { return IndexingMode.Auto; }
            catch (UnauthorizedAccessException) { return IndexingMode.Auto; }
        }
        set => Write("indexing-mode.txt", value.ToString());
    }

    internal static string? Read(string name)
    {
        string path = Path.Combine(DirectoryPath, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    internal static void Write(string name, string value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temp = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, value);
            File.Move(temp, Path.Combine(DirectoryPath, name), true);
        }
        finally { File.Delete(temp); }
    }
}
