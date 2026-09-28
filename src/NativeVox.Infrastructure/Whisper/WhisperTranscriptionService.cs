namespace NativeVox.Infrastructure.Whisper;

using System.IO;
using System.Text;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using global::Whisper.net;
using global::Whisper.net.LibraryLoader;

public class WhisperTranscriptionService : ITranscriptionService
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public bool IsInitialized => _processor != null && !_disposed;

    public ExecutionProvider ActiveProvider { get; private set; } = ExecutionProvider.Cpu;

    public async Task InitializeAsync(string modelPath, ExecutionProvider requestedProvider, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model file not found at {modelPath}");
        }

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

            // Load model factory
            _factory = WhisperFactory.FromPath(modelPath);

            // Configure processor: greedy sampling for ultra-low latency live dictation
            _processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .Build();

            // Detect which runtime was actually loaded
            var loaded = RuntimeOptions.LoadedLibrary;
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
            return string.Empty;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            using var stream = new MemoryStream(pcm16Bit16KhzAudio);
            var sb = new StringBuilder();

            await foreach (var segment in _processor.ProcessAsync(stream, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    sb.Append(segment.Text);
                }
            }

            return sb.ToString().Trim();
        }
        finally
        {
            _semaphore.Release();
        }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnloadInternalAsync().GetAwaiter().GetResult();
        _semaphore.Dispose();
    }
}
