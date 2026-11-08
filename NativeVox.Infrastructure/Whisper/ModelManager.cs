namespace NativeVox.Infrastructure.Whisper;

using System.IO;
using System.Net.Http;
using Serilog;
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log.Information("Downloading model {Model} from {Url} to {Path}.", size, def.Url, destinationPath);

        try
        {
            using var response = await _httpClient.GetAsync(def.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Log.Debug("Model download response: HTTP {Status}, Content-Length {Length}.",
                (int)response.StatusCode, response.Content.Headers.ContentLength);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? def.SizeBytes;
            long totalRead = 0;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                int lastLoggedDecile = 0;

                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        var percentage = (double)totalRead / totalBytes * 100.0;
                        progress?.Report(Math.Min(100.0, percentage));

                        int decile = (int)(percentage / 10);
                        if (decile > lastLoggedDecile && decile < 10)
                        {
                            lastLoggedDecile = decile;
                            Log.Verbose("Model {Model} download progress: {Percent}% ({ReadMB:F1} MB).", size, decile * 10, totalRead / 1_048_576.0);
                        }
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                Log.Debug("Replacing existing model file {Path}.", destinationPath);
                File.Delete(destinationPath);
            }
            File.Move(tempPath, destinationPath);
            progress?.Report(100.0);
            Log.Information("Model {Model} downloaded: {SizeMB:F1} MB in {Seconds:F1}s.", size, totalRead / 1_048_576.0, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Model {Model} download cancelled after {Seconds:F1}s.", size, sw.Elapsed.TotalSeconds);
            TryDeleteTemp(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Model {Model} download failed: {Message}", size, ex.Message);
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        if (!File.Exists(tempPath)) return;
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not delete partial download {Path}.", tempPath);
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
                Log.Information("Deleted model {Model} ({Path}).", size, filePath);
            }
            else
            {
                Log.Debug("Delete requested for model {Model}, but {Path} does not exist.", size, filePath);
            }
        }
        return Task.CompletedTask;
    }
}
