namespace NativeVox.Core.Models;

/// <summary>
/// Information and metadata regarding a downloadable Whisper model.
/// </summary>
public record ModelInfo(
    ModelSize Size,
    string DisplayName,
    string FileName,
    long ApproximateSizeBytes,
    string DownloadUrl,
    bool IsEnglishOnly,
    bool IsDownloaded = false,
    string? LocalFilePath = null
)
{
    public double SizeInMegabytes => Math.Round((double)ApproximateSizeBytes / (1024 * 1024), 1);
}
