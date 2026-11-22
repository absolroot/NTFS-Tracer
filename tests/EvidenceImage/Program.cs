using System.Security.Cryptography;
using System.Text.Json;
using EvidenceImage;

namespace EvidenceImage.Tests;

internal static class Program
{
    private static readonly string[] DiskImages =
    [
        "Alpha/volume.raw", "case-mbr.raw", "case-gpt.raw", "split/case.001", "case-fixed.vhd",
        "case-dynamic.vhd", "case.vhdx", "case.vmdk", "case-flat.vmdk", "case-stream.vmdk",
        "case-v1.qcow", "case-v2.qcow2", "case-v3.qcow2", "case-compressed.qcow2", "case.aff", "case.E01"
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--extended-ewf"))
        {
            Console.Error.WriteLine("Usage: EvidenceImage.Tests <generated fixture directory> [--extended-ewf]");
            return 2;
        }

        string fixtures = Path.GetFullPath(args[0]);
        string output = Directory.CreateTempSubdirectory("EvidenceImage-tests-").FullName;
        try
        {
            CheckImageSelection(output);
            await CheckEvidenceHashAsync(output);
            foreach (string image in DiskImages)
                await CheckImage(fixtures, output, image, logical: false);

            if (args.Length == 2)
            {
                await CheckImage(fixtures, output, "physical.Ex01", logical: false);
                await CheckImage(fixtures, output, "logical.L01", logical: true);
                await CheckImage(fixtures, output, "logical.Lx01", logical: true);
                try
                {
                    using var logicalDisk = EvidenceDisk.OpenRead(Path.Combine(fixtures, "logical.L01"));
                    throw new InvalidOperationException("Logical catalog opened as a disk");
                }
                catch (NotSupportedException) { Console.WriteLine("PASS rejected logical catalog as a disk"); }
            }
            await SessionChecks.RunAsync(fixtures, output, args.Length == 2);
            CheckDecodedDisk(fixtures, "case-compressed.qcow2");
            CheckDecodedDisk(fixtures, "split/case.001");
            await CheckRejections(fixtures, output);
            await CheckSparseExtraction(fixtures, output);
            Console.WriteLine("All requested fixture checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static async Task CheckEvidenceHashAsync(string output)
    {
        string path = Path.Combine(output, "unaligned-evidence.bin");
        byte[] bytes = new byte[262147];
        bytes[0] = 0xc3;
        bytes[^1] = 0x7e;
        File.WriteAllBytes(path, bytes);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        foreach (bool asynchronous in new[] { false, true })
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, asynchronous);
            file.Position = 37;
            Require(await EvidenceHash.Sha256Async(file) == expected && file.Position == 37,
                "Verification must hash every byte and restore the original position for both file handle modes.");
        }
    }

    private static void CheckImageSelection(string output)
    {
        Require(ImageFileTypes.FirstSelectedSegment([]) == null, "Empty selection had a first segment");
        Require(ImageFileTypes.FirstSelectedSegment(["case.002", "case.001", "case.000"]) == "case.000", "Incorrect RAW start");
        Require(ImageFileTypes.FirstSelectedSegment(["case.101", "case.102"]) == null, "Late RAW part became first");
        Require(ImageFileTypes.FirstSelectedSegment(["case.Lx02", "CASE.lX01"]) == "CASE.lX01", "Incorrect logical EWF start");
        Require(ImageFileTypes.FirstSelectedSegment(["case.E01", "case.Ex02"]) == null &&
            ImageFileTypes.FirstSelectedSegment(["a/case.E01", "b/case.E02"]) == null &&
            ImageFileTypes.FirstSelectedSegment(["case.L01", "$MFT"]) == null, "Mixed selection became one image");
        Check("unknown.bin", "FILE"u8.ToArray(), false);
        Check("disk.DD", [], true);
        Check("renamed.bin", "EVF2"u8.ToArray(), true);
        byte[] volume = new byte[1024];
        "NTFS    "u8.CopyTo(volume.AsSpan(3));
        Check("without-boot-signature", volume, false);
        volume[510] = 0x55;
        volume[511] = 0xaa;
        Check("ntfs-volume", volume, true);
        Array.Clear(volume);
        "conectix"u8.CopyTo(volume.AsSpan(512));
        Check("fixed-footer-only", volume, true);

        void Check(string name, byte[] bytes, bool expected)
        {
            string path = Path.Combine(output, name);
            File.WriteAllBytes(path, bytes);
            using var file = File.OpenRead(path);
            byte[] header = new byte[Math.Min(512, bytes.Length)];
            file.ReadExactly(header);
            Require(ImageFileTypes.IsImage(file, header) == expected && file.Position == header.Length,
                "Incorrect image classification or changed read position: " + name);
        }
    }

    private static void CheckDecodedDisk(string fixtures, string image)
    {
        using var plain = File.OpenRead(Path.Combine(fixtures, "case-mbr.raw"));
        using var decoded = EvidenceDisk.OpenRead(Path.Combine(fixtures, image));
        Require(decoded.CanRead && decoded.CanSeek && !decoded.CanWrite && decoded.Length == plain.Length,
            $"{image}: incorrect decoded stream capabilities");
        var bytes = new byte[1536];
        var expected = new byte[bytes.Length];
        foreach (long position in new[] { 2097152L + 509, (12L << 20) - 333, plain.Length - bytes.Length, 0L })
        {
            plain.Position = position;
            Require(decoded.Seek(position, SeekOrigin.Begin) == position, "Incorrect decoded seek result");
            plain.ReadExactly(expected);
            decoded.ReadExactly(bytes);
            Require(bytes.AsSpan().SequenceEqual(expected), $"Incorrect decoded bytes at {position} in {image}");
        }
        decoded.Seek(0, SeekOrigin.End);
        Require(decoded.ReadByte() == -1, "Decoded stream did not report EOF");
        try
        {
            decoded.WriteByte(0);
            throw new InvalidOperationException("Evidence stream allowed a write");
        }
        catch (NotSupportedException) { Console.WriteLine("PASS read-only decoded sectors " + image); }
    }

    private static async Task CheckSparseExtraction(string fixtures, string output)
    {
        string image = Path.Combine(output, "sparse-test.raw");
        File.Copy(Path.Combine(fixtures, "Alpha", "volume.raw"), image);
        const int unit = 64 * 1024;
        var expected = new byte[72 * unit + 31];
        Array.Fill(expected, (byte)0xf4, 18 * unit, unit);
        Array.Fill(expected, (byte)0xb9, 50 * unit, unit);
        using (var disk = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var ntfs = new DiscUtils.Ntfs.NtfsFileSystem(disk, DiscUtils.Ntfs.NtfsJournalMode.Ignore))
        {
            const string name = @"\sparse.bin";
            using var stream = ntfs.OpenFile(name, FileMode.CreateNew, FileAccess.ReadWrite);
            stream.Write(expected);
            ntfs.SetAttributes(name, ntfs.GetAttributes(name) | FileAttributes.SparseFile);
            stream.Position = 0;
            stream.Clear(18 * unit);
            stream.Position = 19 * unit;
            stream.Clear(31 * unit);
            stream.Position = 51 * unit;
            stream.Clear(21 * unit);
            Require(stream.Extents.Sum(range => range.Length) < expected.Length / 4, "Expected sparse NTFS extents");
            const string zeros = @"\holes.bin";
            using var holes = ntfs.OpenFile(zeros, FileMode.CreateNew, FileAccess.ReadWrite);
            holes.Write(new byte[unit]);
            ntfs.SetAttributes(zeros, ntfs.GetAttributes(zeros) | FileAttributes.SparseFile);
            holes.Position = 0;
            holes.Clear(2 * unit);
            Require(!holes.Extents.Any(), "Expected a file containing only holes");
        }
        var volume = (await ImageEvidence.ProbeAsync(image)).Volumes.Single();
        var extraction = await ImageEvidence.ExtractArtifactsAsync(image, volume.Id, output,
            [new("sparse", @"\sparse.bin", null, "sparse.bin"), new("holes", @"\holes.bin", null, "holes.bin")]);
        using var manifest = JsonDocument.Parse(File.ReadAllText(extraction.ManifestPath));
        string? digest = manifest.RootElement.GetProperty("artifacts").GetProperty("sparse").GetProperty("sha256").GetString();
        Require(expected.AsSpan().SequenceEqual(File.ReadAllBytes(extraction.Files["sparse"])) &&
            digest == Convert.ToHexStringLower(SHA256.HashData(expected)), "Incorrect logical bytes or SHA-256 across NTFS sparse holes");
        var zerosOnly = new byte[2 * unit];
        Require(zerosOnly.AsSpan().SequenceEqual(File.ReadAllBytes(extraction.Files["holes"])) &&
            manifest.RootElement.GetProperty("artifacts").GetProperty("holes").GetProperty("sha256").GetString() ==
            Convert.ToHexStringLower(SHA256.HashData(zerosOnly)), "Incorrect all-hole file");
        Console.WriteLine("PASS NTFS sparse holes, partial final cluster and logical SHA-256");
    }

    private static async Task CheckImage(string fixtures, string output, string relativePath, bool logical)
    {
        string path = Path.Combine(fixtures, relativePath);
        var probe = await ImageEvidence.ProbeAsync(path);
        bool dual = relativePath == "case-gpt.raw";
        Require(probe.Volumes.Count == (dual ? 2 : 1),
            $"{relativePath}: unexpected volumes. {string.Join(" | ", probe.Warnings)}");
        if (dual)
            Require(probe.Volumes.Select(v => v.Offset).Order().SequenceEqual([1048576L, 27262976L]),
                "Incorrect GPT volume offsets");

        foreach (var volume in probe.Volumes)
        {
            string expectedVolume = dual && volume.Offset == 27262976 ? "Beta" : "Alpha";
            string oracle = Path.Combine(fixtures, expectedVolume);
            if (!logical && !dual)
                Require(volume.Offset == (relativePath.StartsWith("Alpha/", StringComparison.Ordinal) ? 0 : 2097152), "Incorrect volume offset");
            Require(volume.Artifacts.Order().SequenceEqual(["journal", "logFile", "mft"]),
                $"{relativePath}: incomplete artifact inventory");

            var preset = await ImageEvidence.ExtractAsync(path, volume.Id, output);
            using var manifest = JsonDocument.Parse(File.ReadAllText(preset.ManifestPath));
            var entries = manifest.RootElement.GetProperty("artifacts");
            Match("journal", preset.JournalPath, "expected-j.bin");
            Match("mft", preset.MftPath, "tsk-mft.bin");
            Match("logFile", preset.LogFilePath, "expected-l.bin");

            if (dual || logical)
            {
                bool mftOnly = logical;
                string key = mftOnly ? "mft" : "journal";
                var selection = await ImageEvidence.ExtractSelectedAsync(path, volume.Id, output, [key]);
                using var selectedManifest = JsonDocument.Parse(File.ReadAllText(selection.ManifestPath));
                var record = selectedManifest.RootElement.GetProperty("artifacts").EnumerateObject().Single();
                var selectedPath = (mftOnly ? selection.MftPath : selection.JournalPath)!;
                Require(record.Name == key && record.Value.GetProperty("sha256").GetString() == Hash(selectedPath)
                    && Hash(selectedPath) == entries.GetProperty(key).GetProperty("sha256").GetString()
                    && (mftOnly ? selection.JournalPath : selection.MftPath) == null && selection.LogFilePath == null
                    && Directory.EnumerateFiles(Path.GetDirectoryName(selection.ManifestPath)!).Count() == 2,
                    "Selected extraction contains extra artifacts or the wrong bytes");
            }

            var extra = logical
                ? await LogicalEvidence.ExtractArtifactsAsync(path, [new("bitmap", @"\Volume\$Bitmap", null, "bitmap.bin")], output)
                : await ImageEvidence.ExtractArtifactsAsync(path, volume.Id, output, [new("bitmap", @"\$Bitmap", null, "bitmap.bin")]);
            Require(Hash(extra.Files["bitmap"]) == Hash(Path.Combine(oracle, "tsk-bitmap.bin")),
                $"{relativePath}: $Bitmap differs from icat");
            var sources = manifest.RootElement.GetProperty("provenance").GetProperty(logical ? "segments" : "sourceFiles")
                .EnumerateArray().ToArray();
            int count = relativePath switch
            {
                "case.E01" or "split/case.001" => 3,
                "case-flat.vmdk" => 2,
                _ => 1
            };
            Require(sources.Length == count, $"{relativePath}: incomplete source inventory");
            foreach (var source in sources)
            {
                string original = source.GetProperty("path").GetString()!;
                Require(source.GetProperty("length").GetInt64() == new FileInfo(original).Length &&
                    source.GetProperty("sha256").GetString() == Hash(original), "Incorrect source length or digest: " + original);
            }
            Console.WriteLine($"PASS {relativePath} / {volume.Label} at {volume.Offset}");

            void Match(string role, string? actual, string oracleFile)
            {
                string reference = Path.Combine(oracle, oracleFile);
                string digest = Hash(reference);
                if (actual == null || !File.Exists(actual))
                    throw new InvalidDataException($"{relativePath}: missing {role}");
                long length = new FileInfo(reference).Length;
                Require(new FileInfo(actual).Length == length && Hash(actual) == digest,
                    $"{relativePath}: extracted bytes differ for {expectedVolume}/{role}");
                var entry = entries.GetProperty(role);
                Require(entry.GetProperty("length").GetInt64() == length && entry.GetProperty("sha256").GetString() == digest,
                    $"{relativePath}: incorrect manifest length or digest for {role}");
            }
        }
    }

    private static async Task CheckRejections(string fixtures, string output)
    {
        string rejected = Path.Combine(output, "rejected");
        foreach (string name in new[] { "case-backing.qcow2", "truncated.vmdk", "missing/case.E01",
            "splitmissing/case.001", "case-corrupt-gpt.raw", "truncated.raw" })
        {
            string path = Path.Combine(fixtures, name);
            Require(File.Exists(path), "Missing negative fixture: " + path);
            await Reject(name, async () =>
            {
                var probe = await ImageEvidence.ProbeAsync(path);
                if (probe.Volumes.Count == 0 && probe.Warnings.Count > 0)
                    throw new InvalidDataException(string.Join("; ", probe.Warnings));
                foreach (var v in probe.Volumes)
                    await ImageEvidence.ExtractAsync(path, v.Id, rejected);
            });
        }

        string raw = Path.Combine(fixtures, "case-mbr.raw");
        string invalidQcow = Path.Combine(output, "missing-header.qcow3");
        File.Copy(raw, invalidQcow);
        await Reject("QCOW3 without a container header", () => ImageEvidence.ProbeAsync(invalidQcow));
        string id = (await ImageEvidence.ProbeAsync(raw)).Volumes.Single().Id;
        await Reject("unknown volume", () => ImageEvidence.ExtractAsync(raw, "absent", rejected));
        foreach (string[] invalid in new[] { Array.Empty<string>(), ["mft", "mft"], ["notAnArtifact"] })
        {
            try
            {
                await ImageEvidence.ExtractSelectedAsync(raw, id, rejected, invalid);
                throw new InvalidOperationException("Invalid selected keys were accepted");
            }
            catch (ArgumentException) { }
        }
        ArtifactRequest bitmap = new("bitmap", @"\$Bitmap", null, "bitmap.bin");
        await Reject("missing second artifact", () => ImageEvidence.ExtractArtifactsAsync(raw, id, rejected,
            [bitmap, new("absent", @"\absent-file", null, "absent.bin")]));
        await Reject("pre-cancelled extraction", () => ImageEvidence.ExtractAsync(raw, id, rejected, new CancellationToken(true)));
        using var cancellation = new CancellationTokenSource();
        await Reject("cancel during source hashing", () => ImageEvidence.ExtractAsync(raw, id, rejected,
            cancellation.Token, new CancelOnHash(cancellation)));
        Require(cancellation.IsCancellationRequested, "Hash progress was never reported");
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            ImageEvidence.ExtractArtifactsAsync(raw, id, output, [bitmap])));
        string oracle = Hash(Path.Combine(fixtures, "Alpha", "tsk-bitmap.bin"));
        Require(simultaneous[0].ManifestPath != simultaneous[1].ManifestPath
            && simultaneous.All(batch => Hash(batch.Files["bitmap"]) == oracle), "Concurrent artifacts were mixed");

        async Task Reject(string name, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or OperationCanceledException)
            {
                Require(!Directory.Exists(rejected) || !Directory.EnumerateFileSystemEntries(rejected).Any(),
                    "Partial publication after " + name);
                Console.WriteLine($"PASS rejected {name}: {ex.Message}");
                return;
            }
            throw new InvalidOperationException("Unexpectedly accepted " + name);
        }
    }

    private sealed class CancelOnHash(CancellationTokenSource cancellation) : IProgress<ImageReadProgress>
    {
        public void Report(ImageReadProgress progress)
        {
            if (progress.Stage == "hash")
                cancellation.Cancel();
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

