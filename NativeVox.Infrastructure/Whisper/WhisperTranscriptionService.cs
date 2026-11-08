namespace NativeVox.Infrastructure.Whisper;

using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using global::Whisper.net;
using global::Whisper.net.LibraryLoader;
using global::Whisper.net.Logger;

public class WhisperTranscriptionService : ITranscriptionService
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    // Bridges native whisper.cpp / Whisper.net log output (runtime loading, CUDA probing) into Serilog.
    private static IDisposable? _nativeLogBridge;

    // Filters out Whisper hallucination/special tokens like [BLANK_AUDIO], [Silence], (applause), etc.
    private static readonly System.Text.RegularExpressions.Regex SpecialTokenRegex =
        new(@"\[[\w\s_-]+\]|\([\w\s_-]+\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public bool IsInitialized => _processor != null && !_disposed;

    public ExecutionProvider ActiveProvider { get; private set; } = ExecutionProvider.Cpu;

    public async Task InitializeAsync(string modelPath, ExecutionProvider requestedProvider, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model file not found at {modelPath}");
        }

        EnsureNativeLogBridge();

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await UnloadInternalAsync();

            // Configure runtime library order based on requested provider
            var runtimeOrder = new List<RuntimeLibrary>();
            switch (requestedProvider)
            {
                case ExecutionProvider.Cuda:
                    runtimeOrder.Add(RuntimeLibrary.Cuda);
                    runtimeOrder.Add(RuntimeLibrary.Cpu);
                    break;
                case ExecutionProvider.Cpu:
                    runtimeOrder.Add(RuntimeLibrary.Cpu);
                    break;
                case ExecutionProvider.Auto:
                default:
                    runtimeOrder.Add(RuntimeLibrary.Cuda);
                    runtimeOrder.Add(RuntimeLibrary.Cpu);
                    break;
            }

            RuntimeOptions.RuntimeLibraryOrder = runtimeOrder;
            Log.Debug("Whisper runtime library order: {Order}. Model file: {Path} ({SizeMB:F1} MB).",
                string.Join(" -> ", runtimeOrder), modelPath, new FileInfo(modelPath).Length / 1_048_576.0);

            // Load model factory
            _factory = WhisperFactory.FromPath(modelPath);

            // Configure processor: greedy sampling for ultra-low latency live dictation
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .Build();

            // Detect which runtime was actually loaded
            var loaded = RuntimeOptions.LoadedLibrary;
            Log.Debug("Whisper native runtime loaded: {Runtime}.", loaded);
            ActiveProvider = loaded switch
            {
                RuntimeLibrary.Cuda => ExecutionProvider.Cuda,
                _ => ExecutionProvider.Cpu
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string> TranscribeAsync(byte[] pcm16Bit16KhzAudio, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_processor == null)
        {
            throw new InvalidOperationException("Transcription service is not initialized with a model.");
        }

        if (pcm16Bit16KhzAudio == null || pcm16Bit16KhzAudio.Length == 0)
        {
            Log.Verbose("TranscribeAsync called with empty audio; returning empty string.");
            return string.Empty;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // The capture buffer is raw PCM (no RIFF/WAV header), so the Stream overload of
            // ProcessAsync cannot parse it. Convert to normalized float samples instead.
            var samples = ConvertPcm16ToFloat(pcm16Bit16KhzAudio);
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            int segmentCount = 0;

            await foreach (var segment in _processor.ProcessAsync(samples, cancellationToken))
            {
                segmentCount++;
                Log.Verbose("Whisper segment [{Start} -> {End}]: \"{Text}\"", segment.Start, segment.End, segment.Text);

                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    var cleanedSegment = SpecialTokenRegex.Replace(segment.Text, "").Trim();
                    if (!string.IsNullOrWhiteSpace(cleanedSegment))
                    {
                        sb.Append(segment.Text);
                    }
                }
            }

            var rawResult = sb.ToString().Trim();
            var result = SpecialTokenRegex.Replace(rawResult, "").Trim();
            Log.Verbose("Whisper processed {Samples} samples ({Seconds:F2}s) in {Elapsed} ms -> {Segments} segment(s), {Length} chars.",
                samples.Length, samples.Length / 16000.0, sw.ElapsedMilliseconds, segmentCount, result.Length);
            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Converts 16-bit little-endian mono PCM bytes into float samples in the range [-1, 1].
    /// </summary>
    internal static float[] ConvertPcm16ToFloat(byte[] pcm16)
    {
        var sampleCount = pcm16.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short value = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            samples[i] = value / 32768f;
        }
        return samples;
    }

    public async Task UnloadAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            await UnloadInternalAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private Task UnloadInternalAsync()
    {
        if (_processor != null || _factory != null)
        {
            Log.Debug("Unloading current Whisper model.");
        }

        if (_processor != null)
        {
            _processor.Dispose();
            _processor = null;
        }

        if (_factory != null)
        {
            _factory.Dispose();
            _factory = null;
        }

        ActiveProvider = ExecutionProvider.Cpu;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return Task.CompletedTask;
    }

    private static void EnsureNativeLogBridge()
    {
        if (_nativeLogBridge != null) return;

        _nativeLogBridge = LogProvider.AddLogger((level, message) =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            var text = message.TrimEnd();

            // whisper.cpp is chatty at info/debug level, so keep those at Verbose.
            switch (level.ToString())
            {
                case "Error":
                    Log.Warning("[whisper.cpp] {Message}", text);
                    break;
                case "Warning":
                    Log.Debug("[whisper.cpp] {Message}", text);
                    break;
                default:
                    Log.Verbose("[whisper.cpp] {Message}", text);
                    break;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnloadInternalAsync().GetAwaiter().GetResult();
        _semaphore.Dispose();
    }
}
