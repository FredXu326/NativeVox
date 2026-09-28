namespace NativeVox.Infrastructure.Whisper;

using System.IO;
using System.Net.Http;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using NativeVox.Infrastructure.Storage;

public class ModelManager : IModelManager
{
    private static readonly Dictionary<ModelSize, (string FileName, long SizeBytes, string Url, bool IsEnglish)> ModelDefinitions = new()
    {
        {
            ModelSize.Tiny,
            ("ggml-tiny.en.bin", 77_700_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin", true)
        },
        {
            ModelSize.Base,
            ("ggml-base.en.bin", 147_900_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin", true)
        },
        {
            ModelSize.Small,
            ("ggml-small.en.bin", 488_000_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin", true)
        },
        {
            ModelSize.Medium,
            ("ggml-medium.en.bin", 1_530_000_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin", true)
        },
        {
            ModelSize.Large,
            ("ggml-large-v3.bin", 3_100_000_000, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin", false)
        }
    };

    private readonly HttpClient _httpClient;

    public ModelManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public IReadOnlyList<ModelInfo> GetModels()
    {
        var models = new List<ModelInfo>();
        foreach (var (size, (fileName, sizeBytes, url, isEnglish)) in ModelDefinitions)
        {
            var filePath = Path.Combine(AppPathResolver.ModelsDirectory, fileName);
            var isDownloaded = File.Exists(filePath);
            var displayName = size switch
            {
                ModelSize.Tiny => "Tiny (Fastest, ~75MB)",
                ModelSize.Base => "Base (Recommended, ~142MB)",
                ModelSize.Small => "Small (Balanced, ~466MB)",
                ModelSize.Medium => "Medium (High Accuracy, ~1.5GB)",
                ModelSize.Large => "Large v3 (Maximum Accuracy, ~3.1GB)",
                _ => size.ToString()
            };

            models.Add(new ModelInfo(
                Size: size,
                DisplayName: displayName,
                FileName: fileName,
                ApproximateSizeBytes: sizeBytes,
                DownloadUrl: url,
                IsEnglishOnly: isEnglish,
                IsDownloaded: isDownloaded,
                LocalFilePath: isDownloaded ? filePath : null
            ));
        }

        return models;
    }

    public bool IsDownloaded(ModelSize size)
    {
        if (!ModelDefinitions.TryGetValue(size, out var def)) return false;
        var filePath = Path.Combine(AppPathResolver.ModelsDirectory, def.FileName);
        return File.Exists(filePath);
    }

    public string GetModelFilePath(ModelSize size)
    {
        if (!ModelDefinitions.TryGetValue(size, out var def))
            throw new ArgumentOutOfRangeException(nameof(size), $"Model {size} is not recognized.");

        return Path.Combine(AppPathResolver.ModelsDirectory, def.FileName);
    }

    public async Task DownloadModelAsync(ModelSize size, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!ModelDefinitions.TryGetValue(size, out var def))
            throw new ArgumentOutOfRangeException(nameof(size), $"Model {size} is not recognized.");

        AppPathResolver.EnsureDirectoriesExist();
        var destinationPath = Path.Combine(AppPathResolver.ModelsDirectory, def.FileName);
        var tempPath = destinationPath + ".downloading";

        try
        {
            using var response = await _httpClient.GetAsync(def.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? def.SizeBytes;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;

                    if (totalBytes > 0 && progress != null)
                    {
                        var percentage = (double)totalRead / totalBytes * 100.0;
                        progress.Report(Math.Min(100.0, percentage));
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempPath, destinationPath);
            progress?.Report(100.0);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
            throw;
        }
    }

    public Task DeleteModelAsync(ModelSize size)
    {
        if (ModelDefinitions.TryGetValue(size, out var def))
        {
            var filePath = Path.Combine(AppPathResolver.ModelsDirectory, def.FileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        return Task.CompletedTask;
    }
}
