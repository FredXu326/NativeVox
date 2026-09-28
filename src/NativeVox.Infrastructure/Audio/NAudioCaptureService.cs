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

        if (IsRecording) return;

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
        }

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit Mono required by Whisper
            BufferMilliseconds = 100,
            DeviceNumber = deviceIndex >= 0 && deviceIndex < WaveInEvent.DeviceCount ? deviceIndex : 0
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
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
        AudioLevelChanged?.Invoke(this, rms);

        // Notify progressive stream listeners
        AudioChunkAvailable?.Invoke(this, chunk);
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
        lock (_lock)
        {
            fullAudio = _accumulatedBuffer.ToArray();
            _accumulatedBuffer.SetLength(0);
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
