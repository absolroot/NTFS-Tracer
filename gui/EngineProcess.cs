using System.Diagnostics;
using System.IO;
using System.Text;

namespace JournalDesk;

internal static class EngineProcess
{
    internal static async Task RunAsync(string? mode, string source, string output,
        CancellationToken token, Action<string>? onProgress = null)
    {
        token.ThrowIfCancellationRequested();
        string executable = Path.Combine(AppContext.BaseDirectory, "ntfstracer-cli.exe");
        string legacy = Path.Combine(AppContext.BaseDirectory, "journal-index.exe");
        if (!File.Exists(executable) && File.Exists(legacy))
            executable = legacy;
        var options = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (mode != null)
            options.ArgumentList.Add(mode);
        options.ArgumentList.Add(source);
        options.ArgumentList.Add(output);
        using var child = Process.Start(options) ?? throw new IOException(L.T("분석 엔진을 실행할 수 없습니다.", "Could not start the analysis engine."));
        using var cancel = token.Register(() =>
        {
            try
            {
                if (!child.HasExited)
                    child.Kill(true);
            }
            catch (InvalidOperationException) { }
        });
        var stderr = child.StandardError.ReadToEndAsync();
        if (onProgress == null)
            await child.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        else
            while (await child.StandardOutput.ReadLineAsync() is { } line)
                onProgress(line);
        await child.WaitForExitAsync();
        string error = (await stderr).Trim();
        token.ThrowIfCancellationRequested();
        if (child.ExitCode != 0)
            throw new IOException(error.Length == 0
            ? L.T("엔진이 자료를 읽지 못했습니다.", "The engine could not read the source.")
            : EngineDiagnostic.Display(error));
    }
}
