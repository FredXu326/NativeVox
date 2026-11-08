namespace NativeVox.Core.Services;

using NativeVox.Core.Models;

/// <summary>
/// Pluggable abstraction for offline audio transcription engines.
/// </summary>
public interface ITranscriptionService : IDisposable
{
    bool IsInitialized { get; }

    ExecutionProvider ActiveProvider { get; }

    Task InitializeAsync(string modelPath, ExecutionProvider requestedProvider, CancellationToken cancellationToken = default);

    Task<string> TranscribeAsync(byte[] pcm16Bit16KhzAudio, CancellationToken cancellationToken = default);

    Task UnloadAsync();
}
