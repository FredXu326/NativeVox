namespace NativeVox.Core.Services;

/// <summary>
/// Service responsible for capturing 16kHz, 16-bit mono audio from input devices.
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    bool IsRecording { get; }

    /// <summary>
    /// Event raised when a chunk of audio is captured (for progressive streaming transcription).
    /// </summary>
    event EventHandler<byte[]>? AudioChunkAvailable;

    /// <summary>
    /// Event raised when the underlying audio driver encounters an error or disconnection.
    /// </summary>
    event EventHandler<Exception>? CaptureError;

    /// <summary>
    /// Event raised with the current audio peak level (0.0 to 1.0) for visual pulsing meters.
    /// </summary>
    event EventHandler<float>? AudioLevelChanged;

    Task StartAsync(int deviceIndex = -1, CancellationToken cancellationToken = default);

    Task<byte[]> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a snapshot of the audio recorded so far in the current session without stopping.
    /// </summary>
    byte[] GetCurrentAudioSnapshot();
}
