using JournalDesk;

internal static class IndexCacheChecks
{
    internal static async Task RunAsync(string directory)
    {
        string path = Path.Combine(directory, "cache-test");
        var reads = new List<string>();
        string Read(string index)
        {
            reads.Add(index);
            return File.ReadAllText(index);
        }
        static void Verify(string value)
        {
            if (value != "verified")
                throw new InvalidDataException();
        }
        Task<string> Open(Func<string, Task> build) => IndexCache.OpenAsync(path, build, Read, Verify, CancellationToken.None);

        await Open(temp => File.WriteAllTextAsync(temp, "verified"));
        if (reads.Count != 1 || reads[0] == path)
            throw new InvalidOperationException("The newly built index must be verified before publication.");
        await Open(_ => throw new InvalidOperationException("Existing cache rebuilt."));
        if (reads.Count != 2 || reads[1] != path)
            throw new InvalidOperationException("Existing indexes must be verified.");
        File.Delete(path);

        try
        {
            await Open(async temp =>
            {
                await File.WriteAllTextAsync(temp, "verified");
                await File.WriteAllTextAsync(path, "different source");
            });
            throw new InvalidOperationException("A competing index with the wrong source was accepted.");
        }
        catch (InvalidDataException) { }
        File.Delete(path);
        try
        {
            await Open(temp => File.WriteAllTextAsync(temp, "different source"));
            throw new InvalidOperationException("An invalid index was published.");
        }
        catch (InvalidDataException) { }
        if (File.Exists(path) || Directory.EnumerateFiles(directory, "*.partial").Any())
            throw new InvalidOperationException("An invalid or incomplete index remained on disk.");

        string otherJournal = Path.Combine(directory, "other.partial-journal");
        await File.WriteAllTextAsync(otherJournal, "other writer");
        string[] interrupted = [];
        try
        {
            await Open(async temp =>
            {
                interrupted = [temp, temp + "-journal", temp + "-wal", temp + "-shm"];
                foreach (string file in interrupted)
                    await File.WriteAllTextAsync(file, "unfinished");
                throw new OperationCanceledException();
            });
            throw new InvalidOperationException("An interrupted index was accepted.");
        }
        catch (OperationCanceledException) { }
        if (interrupted.Length != 4 || interrupted.Any(File.Exists) || File.Exists(path) ||
            await File.ReadAllTextAsync(otherJournal) != "other writer")
            throw new InvalidOperationException("Cleanup did not isolate the interrupted writer's SQLite files.");
    }
}
