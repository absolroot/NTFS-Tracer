using System.Security.Cryptography;

namespace EvidenceImage.Tests;

internal static class SessionChecks
{
    private sealed class Observer(Action<ImageReadProgress> report) : IProgress<ImageReadProgress>
    {
        public void Report(ImageReadProgress value) => report(value);
    }

    internal static async Task RunAsync(string fixtures, string output, bool logical)
    {
        await CheckSelectionAsync(Path.Combine(fixtures, "split", "case.001"), Path.Combine(fixtures, "split", "case.002"), output);
        if (logical)
        {
            await CheckSelectionAsync(Path.Combine(fixtures, "logical.L01"), Path.Combine(fixtures, "logical.L01"), output);
            await CheckSelectionAsync(Path.Combine(fixtures, "logical.Lx01"), Path.Combine(fixtures, "logical.Lx01"), output);
        }
        string disk = Path.Combine(fixtures, "case-mbr.raw");
        using var session = await JournalImageSession.OpenAsync(disk);
        string volume = session.Probe.Volumes.Single().Id;
        var original = await session.ExtractAsync(volume, output, ["journal"]);
        using var stop = new CancellationTokenSource();
        string? partial = null;
        var abortVerification = new Observer(p =>
        {
            if (p.Stage == "verify")
            {
                partial = Path.GetDirectoryName(p.Item);
                stop.Cancel();
            }
        });
        await MustThrowAsync<OperationCanceledException>(() => session.ExtractAsync(volume, output, ["mft"], abortVerification, stop.Token));
        Require(session.Extraction == original, "A cancellation replaced the active extraction.");
        var retried = await session.ExtractAsync(volume, output, ["mft"]);
        Require(partial != null && Path.GetDirectoryName(retried.ManifestPath) == partial, "Retry duplicated a published extraction.");
        var corruptManifest = new Observer(p =>
        {
            if (p.Stage == "verify" && p.Completed == 0 && Path.GetFileName(p.Item) == "$J")
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(p.Item)!, "manifest.json"), "{}");
        });
        await MustThrowAsync<InvalidDataException>(() => session.ExtractAsync(volume, output, ["journal"], corruptManifest));
        Require(session.Extraction == retried, "A tampered publication replaced verified files.");
        var corruptArtifact = new Observer(p =>
        {
            if (p.Stage == "verify" && p.Completed == 0 && Path.GetFileName(p.Item) == "$J")
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(p.Item)!, "$MFT"), [0xff]);
        });
        await MustThrowAsync<InvalidDataException>(() => session.ExtractAsync(volume, output, ["journal", "mft"], corruptArtifact));
        Require(session.Extraction == retried, "A modified artifact was accepted for analysis.");
        var close = new Observer(p =>
        {
            if (p.Stage == "extract")
                session.Dispose();
        });
        await MustThrowAsync<OperationCanceledException>(() => session.ExtractAsync(volume, output, ["logFile"], close));
        if (OperatingSystem.IsWindows())
        {
            using var releasedSource = File.Open(disk, FileMode.Open, FileAccess.Read, FileShare.None);
            using var releasedOutput = File.Open(retried.MftPath!, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        Console.WriteLine("PASS session cancellation, retry, manifest authentication and disposal during extraction");
    }

    private static async Task CheckSelectionAsync(string image, string segment, string output)
    {
        ExtractionResult first;
        using (var session = await JournalImageSession.OpenAsync(image))
        {
            string volume = session.Probe.Volumes.Single().Id;
            if (OperatingSystem.IsWindows())
                RequireLocked(segment);
            first = await session.ExtractAsync(volume, output, ["journal", "mft"]);
            using (var opened = File.OpenRead(first.JournalPath!))
            {
                var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(opened));
                if (OperatingSystem.IsWindows())
                    Require(session.GetVerifiedSha256(opened) == actual, "No verified digest for the authenticated handle.");
            }
            var duplicate = await session.ExtractAsync(volume, output, ["mft"]);
            Require(duplicate.JournalPath == null && duplicate.LogFilePath == null && duplicate.MftPath != null, "A subset returned unselected artifacts.");
            if (OperatingSystem.IsWindows())
                Require(duplicate.ManifestPath == first.ManifestPath && session.CanReuse(volume, output, ["journal"]), "A subset caused duplicate extraction.");
            var active = session.Extraction;
            await MustThrowAsync<ArgumentException>(() => session.ExtractAsync(volume, output, ["journal", "journal"]));
            await MustThrowAsync<InvalidDataException>(() => session.ExtractAsync("missing volume", output, ["journal"]));
            Require(session.Extraction == active, "An invalid selection replaced the active extraction.");
            if (OperatingSystem.IsWindows())
                RequireLocked(first.JournalPath!);
            bool rehashed = false;
            var second = await session.ExtractAsync(volume, output, ["logFile"], new Observer(p => rehashed |= p.Stage == "hash"));
            if (OperatingSystem.IsWindows() && !Path.GetExtension(image).StartsWith(".L", StringComparison.OrdinalIgnoreCase))
                Require(!rehashed, "A changed selection rehashed locked image segments.");
            Require(second.LogFilePath != null && second.JournalPath == null, "A changed selection mixed the output sets.");
        }
        if (OperatingSystem.IsWindows())
        {
            using var releasedOutput = File.Open(first.JournalPath!, FileMode.Open, FileAccess.Read, FileShare.None);
            using var releasedSource = File.Open(segment, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        Console.WriteLine("PASS session selected volume, subset reuse, source and output leases " + Path.GetFileName(image));
    }

    private static void RequireLocked(string path)
    {
        try
        {
            using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException) { return; }
        throw new InvalidOperationException("A writable handle could open locked evidence: " + path);
    }

    private static async Task MustThrowAsync<T>(Func<Task> operation) where T : Exception
    {
        try
        {
            await operation();
        }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
