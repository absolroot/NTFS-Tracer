using System.IO;
using EvidenceImage;

namespace JournalDesk;

public partial class MainWindow
{
    internal Task<ExtractionResult> ExtractImageAsync(JournalImageSession image, string volume, string folder, IReadOnlyCollection<string> keys)
    {
        var ui = Dispatcher;
        return ReadImageAsync(L.T("원본 파일을 추출하고 있습니다…", "Extracting source files…"), ct =>
        {
            var progress = new ImageProgress(value => ui.InvokeAsync(() =>
            {
                if (closed || import == null || import.Token != ct)
                    return;
                string item = value.Item switch
                {
                    "journal" => "$J",
                    "mft" => "$MFT",
                    "logFile" => "$LogFile",
                    _ => Path.GetFileName(value.Item)
                };
                string stage = value.Stage switch
                {
                    "hash" => L.T("SHA-256 계산", "Computing SHA-256"),
                    "verify" => L.T("추출물 검증", "Verifying extracted files"),
                    _ => L.T("추출", "Extracting")
                };
                string percent = value.Total == 0 ? "" : $"   ·   {(double)value.Completed / value.Total:P0}";
                Status.Text = $"{stage}   {item}{percent}   ·   {value.Completed:N0} / {value.Total:N0} B";
            }));
            return image.ExtractAsync(volume, folder, keys, progress, ct);
        });
    }

    private sealed class ImageProgress(Action<ImageReadProgress> onProgress) : IProgress<ImageReadProgress>
    {
        public void Report(ImageReadProgress value) => onProgress(value);
    }
}
