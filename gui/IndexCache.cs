using System.IO;

namespace JournalDesk;

internal static class IndexCache
{
    internal static async Task<T> OpenAsync<T>(string path, Func<string, Task> build,
        Func<string, T> readSummary, Action<T> verify, CancellationToken token)
    {
        async Task<T> ReadVerified(string index)
        {
            T summary = await Task.Run(() => readSummary(index), token);
            verify(summary);
            token.ThrowIfCancellationRequested();
            return summary;
        }

        token.ThrowIfCancellationRequested();
        if (File.Exists(path))
            return await ReadVerified(path);

        string temporary = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            await build(temporary);
            T summary = await ReadVerified(temporary);
            try
            {
                File.Move(temporary, path);
                return summary;
            }
            catch (IOException) when (File.Exists(path))
            {
                return await ReadVerified(path);
            }
        }
        finally
        {
            DeleteTemporary(temporary);
            DeleteTemporary(temporary + "-journal");
            DeleteTemporary(temporary + "-wal");
            DeleteTemporary(temporary + "-shm");
        }
    }

    private static void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
