namespace NativeVox.Core.Services;

using NativeVox.Core.Models;

/// <summary>
/// Service managing local Whisper models, metadata, and background downloads.
/// </summary>
public interface IModelManager
{
    IReadOnlyList<ModelInfo> GetModels();

    bool IsDownloaded(ModelSize size);

    string GetModelFilePath(ModelSize size);

    Task DownloadModelAsync(ModelSize size, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task DeleteModelAsync(ModelSize size);
}
