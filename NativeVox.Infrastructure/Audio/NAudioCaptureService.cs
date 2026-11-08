namespace NativeVox.Infrastructure.Audio;

using System.IO;
using NAudio.Wave;
using Polly;
using Polly.Retry;
using Serilog;
using NativeVox.Core.Services;

public class NAudioCaptureService : IAudioCaptureService
{
    private WaveInEvent? _waveIn;
    private readonly MemoryStream _accumulatedBuffer = new();
    private readonly object _lock = new();
    private bool _isRecording;
    private bool _disposed;
    private int _currentDeviceIndex = -1;

    // Per-session capture statistics (used for diagnostics only).
    private const int ChunksPerVerboseLog = 10; // 10 x 100ms buffers = ~1s
    private const float SilencePeakThreshold = 0.01f;
    private int _chunkCount;
    private float _peakLevel;
    private double _levelSum;
    private float _windowPeakLevel;

    public bool IsRecording
    {
        get { lock (_lock) return _isRecording; }
        private set { lock (_lock) _isRecording = value; }
    }

    public event EventHandler<byte[]>? AudioChunkAvailable;
    public event EventHandler<Exception>? CaptureError;
    public event EventHandler<float>? AudioLevelChanged;

    private readonly AsyncRetryPolicy _retryPolicy;

    public NAudioCaptureService()
    {
        _retryPolicy = Policy
            .Handle<Exception>(ex => ex is not UnauthorizedAccessException)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromMilliseconds(250 * Math.Pow(2, retryAttempt - 1)),
                (exception, timeSpan, retryCount, _) =>
                {
                    Log.Warning("Audio capture exception on attempt {Count}. Retrying in {Delay}ms: {Message}",
                        retryCount, timeSpan.TotalMilliseconds, exception.Message);
                });
    }

    public async Task StartAsync(int deviceIndex = -1, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRecording)
        {
            Log.Debug("Audio StartAsync ignored: already recording.");
            return;
        }

        _currentDeviceIndex = deviceIndex;

        try
        {
            await _retryPolicy.ExecuteAsync(() =>
            {
                InitializeWaveIn(_currentDeviceIndex);
                _waveIn!.StartRecording();
                IsRecording = true;
                return Task.CompletedTask;
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "Microphone access denied by Windows Privacy Settings.");
            CaptureError?.Invoke(this, ex);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize or start NAudio recording after retries.");
            CaptureError?.Invoke(this, ex);
            throw;
        }
    }

    private void InitializeWaveIn(int deviceIndex)
    {
        CleanupWaveIn();

        lock (_lock)
        {
            _accumulatedBuffer.SetLength(0);
            _chunkCount = 0;
            _peakLevel = 0f;
            _levelSum = 0;
            _windowPeakLevel = 0f;
        }

        int deviceCount = WaveInEvent.DeviceCount;
        int deviceNumber = deviceIndex >= 0 && deviceIndex < deviceCount ? deviceIndex : 0;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit Mono required by Whisper
            BufferMilliseconds = 100,
            DeviceNumber = deviceNumber
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        LogDeviceSelection(deviceIndex, deviceNumber, deviceCount);
    }

    private static void LogDeviceSelection(int requestedIndex, int deviceNumber, int deviceCount)
    {
        if (deviceCount == 0)
        {
            Log.Warning("No audio input devices detected by WaveIn.");
            return;
        }

        try
        {
            var caps = WaveInEvent.GetCapabilities(deviceNumber);
            Log.Debug("Using audio input device #{Device} \"{Name}\" (requested index {Requested}, {Count} device(s) available, {Channels} channel(s)).",
                deviceNumber, caps.ProductName, requestedIndex, deviceCount, caps.Channels);

            for (int i = 0; i < deviceCount; i++)
            {
                Log.Verbose("  Input device #{Index}: \"{Name}\"", i, WaveInEvent.GetCapabilities(i).ProductName);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not query capabilities for audio device #{Device}.", deviceNumber);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        byte[] chunk = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, chunk, 0, e.BytesRecorded);

        lock (_lock)
        {
            _accumulatedBuffer.Write(chunk, 0, chunk.Length);
        }

        // Calculate RMS peak for pulsing microphone visual feedback
        float rms = CalculateRmsLevel(e.Buffer, e.BytesRecorded);
        TrackLevelStatistics(rms);
        AudioLevelChanged?.Invoke(this, rms);

        // Notify progressive stream listeners
        AudioChunkAvailable?.Invoke(this, chunk);
    }

    private void TrackLevelStatistics(float rms)
    {
        int chunk;
        float windowPeak;
        lock (_lock)
        {
            _chunkCount++;
            _levelSum += rms;
            _peakLevel = Math.Max(_peakLevel, rms);
            _windowPeakLevel = Math.Max(_windowPeakLevel, rms);
            chunk = _chunkCount;
            windowPeak = _windowPeakLevel;
            if (chunk % ChunksPerVerboseLog == 0) _windowPeakLevel = 0f;
        }

        if (chunk % ChunksPerVerboseLog == 0)
        {
            Log.Verbose("Audio level: {Seconds:F1}s captured, peak RMS over last second {Peak:F3}.", chunk / 10.0, windowPeak);
        }
    }

    private static float CalculateRmsLevel(byte[] buffer, int bytesRecorded)
    {
        long sum = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i < bytesRecorded; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sum += sample * sample;
        }

        if (sampleCount == 0) return 0f;
        double rms = Math.Sqrt((double)sum / sampleCount);
        // Normalize 16-bit PCM (max 32767) to range [0.0, 1.0]
        return Math.Clamp((float)(rms / 32767.0), 0f, 1f);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Log.Verbose("WaveIn RecordingStopped event (abnormal: {Abnormal}).", e.Exception != null);
        if (e.Exception != null)
        {
            Log.Error(e.Exception, "Audio recording stopped abnormally.");
            CaptureError?.Invoke(this, e.Exception);

            // Attempt auto-recovery if still marked as recording (e.g., device fluctuation)
            if (IsRecording && !_disposed)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Log.Information("Attempting auto-recovery of audio capture stream...");
                        await Task.Delay(500);
                        await StartAsync(_currentDeviceIndex);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Audio stream auto-recovery failed.");
                    }
                });
            }
        }
    }

    public Task<byte[]> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRecording)
        {
            Log.Debug("Audio StopAsync called while not recording; returning empty buffer.");
            return Task.FromResult(Array.Empty<byte>());
        }

        IsRecording = false;

        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Exception when stopping waveIn recording.");
        }

        byte[] fullAudio;
        int chunks;
        float peak;
        double avg;
        lock (_lock)
        {
            fullAudio = _accumulatedBuffer.ToArray();
            _accumulatedBuffer.SetLength(0);
            chunks = _chunkCount;
            peak = _peakLevel;
            avg = _chunkCount > 0 ? _levelSum / _chunkCount : 0;
        }

        Log.Debug("Audio capture stopped: {Chunks} buffer(s), {Bytes} bytes, peak RMS {Peak:F3}, average RMS {Avg:F3}.",
            chunks, fullAudio.Length, peak, avg);

        if (chunks > 0 && peak < SilencePeakThreshold)
        {
            Log.Warning("Captured audio is near-silent (peak RMS {Peak:F4}). The microphone may be muted, the wrong device may be selected, or input volume is too low.", peak);
        }

        CleanupWaveIn();
        return Task.FromResult(fullAudio);
    }

    public byte[] GetCurrentAudioSnapshot()
    {
        lock (_lock)
        {
            return _accumulatedBuffer.ToArray();
        }
    }

    private void CleanupWaveIn()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IsRecording = false;
        CleanupWaveIn();

        lock (_lock)
        {
            _accumulatedBuffer.Dispose();
        }
    }
}
