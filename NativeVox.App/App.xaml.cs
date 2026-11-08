namespace NativeVox.App;

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Serilog;
using Serilog.Events;
using NativeVox.App.Tray;
using NativeVox.App.Views;
using NativeVox.Core.Models;
using NativeVox.Core.Services;
using NativeVox.Infrastructure.Audio;
using NativeVox.Infrastructure.Hotkey;
using NativeVox.Infrastructure.Injection;
using NativeVox.Infrastructure.Storage;
using NativeVox.Infrastructure.Whisper;

public partial class App : Application
{
    private const string MutexName = "NativeVox_SingleInstance_Mutex_2026";

    /// <summary>
    /// Environment variable that overrides the minimum log level (e.g. "Verbose", "Debug", "Information").
    /// The "--verbose" command-line switch is a shortcut for "Verbose".
    /// </summary>
    private const string LogLevelEnvVar = "NATIVEVOX_LOG_LEVEL";

    private Mutex? _singleInstanceMutex;

    private AppSettings _settings = new();
    private IModelManager _modelManager = null!;
    private ITranscriptionService _transcriptionService = null!;
    private IAudioCaptureService _audioCaptureService = null!;
    private ITextInjectionService _textInjectionService = null!;
    private IHotkeyManager _hotkeyManager = null!;
    private TrayIconManager _trayManager = null!;
    private OverlayWindow _overlayWindow = null!;
    private Window _messageWindow = null!;

    private bool _isDictating;
    private CancellationTokenSource? _dictationCts;
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private readonly Stopwatch _dictationStopwatch = new();
    private int _dictationSessionId;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single-Instance Check
        _singleInstanceMutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("NativeVox is already running in the Windows system tray.", "NativeVox", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. Initialize Serilog rolling text logs
        AppPathResolver.EnsureDirectoriesExist();
        var logFile = Path.Combine(AppPathResolver.LogsDirectory, "nativevox-.log");
        var minimumLevel = ResolveMinimumLogLevel(e.Args);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [T{ThreadId}] {Message:lj}{NewLine}{Exception}")
            .Enrich.With(new ThreadIdEnricher())
            .CreateLogger();

        RegisterGlobalExceptionHandlers();

        Log.Information("NativeVox starting up on .NET 10 (Target OS: Windows)...");
        Log.Debug("Runtime: {Framework}, OS: {OS}, 64-bit process: {Is64}, PID: {Pid}, Elevated: {Elevated}, Log level: {Level}",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.OSVersion.VersionString,
            Environment.Is64BitProcess,
            Environment.ProcessId,
            UIPIElevationDetector.IsCurrentProcessElevated(),
            minimumLevel);
        Log.Debug("Data directory: {Root}", AppPathResolver.RootDirectory);

        // 3. Instantiate Services
        _settings = AppPathResolver.LoadSettings();
        _modelManager = new ModelManager();
        _transcriptionService = new WhisperTranscriptionService();
        _audioCaptureService = new NAudioCaptureService();
        _textInjectionService = new Win32SendInputService();
        _hotkeyManager = new Win32HotkeyManager();
        Log.Debug("Core services instantiated.");

        // 4. Hook Audio Level & UIPI Notifications
        _audioCaptureService.AudioLevelChanged += (_, level) =>
        {
            if (_isDictating && _settings.EnablePulsingHUD)
            {
                _overlayWindow.UpdateAudioLevel(level);
            }
        };

        _audioCaptureService.CaptureError += (_, ex) =>
        {
            Log.Debug("Capture error surfaced to user: {Type}: {Message}", ex.GetType().Name, ex.Message);
            if (ex is UnauthorizedAccessException)
            {
                _trayManager.ShowMicrophonePermissionWarning();
            }
            else
            {
                _trayManager.ShowNotification("Audio Capture Warning", $"Microphone error: {ex.Message}", System.Windows.Forms.ToolTipIcon.Warning);
            }
        };

        _textInjectionService.FallbackToClipboardTriggered += (_, _) =>
        {
            _trayManager.ShowNotification(
                "Text Copied to Clipboard",
                "Direct typing was blocked by Windows UIPI (Administrator window). Transcribed text was copied to your clipboard (press Ctrl+V to paste).",
                System.Windows.Forms.ToolTipIcon.Info
            );
        };

        // 5. Initialize System Tray & UI
        _overlayWindow = new OverlayWindow();

        _trayManager = new TrayIconManager(
            _modelManager,
            _settings,
            onToggleDictation: () => Dispatcher.InvokeAsync(ToggleDictationAsync),
            onModelSelected: size => Dispatcher.InvokeAsync(() => SwitchModelAsync(size)),
            onOpenSettings: () => Dispatcher.Invoke(OpenSettingsWindow)
        );

        // 6. Check First-Run Experience / Model Availability
        if (!_settings.FirstRunCompleted || !_modelManager.IsDownloaded(_settings.SelectedModel))
        {
            Log.Debug("Showing setup wizard (FirstRunCompleted: {FirstRun}, model {Model} downloaded: {Downloaded}).",
                _settings.FirstRunCompleted, _settings.SelectedModel, _modelManager.IsDownloaded(_settings.SelectedModel));

            var setup = new SetupWindow(_modelManager, _settings);
            var result = setup.ShowDialog();
            if (result != true || !_modelManager.IsDownloaded(_settings.SelectedModel))
            {
                Log.Information("Setup cancelled or no model downloaded. Exiting NativeVox.");
                Shutdown();
                return;
            }
        }

        // 7. Load Active Whisper Model
        await LoadActiveModelAsync();

        // 8. Register Global Hotkey using hidden message window
        SetupHotkeyListener();

        _trayManager.ShowNotification(
            "NativeVox Ready",
            $"Offline dictation ready. Press {_settings.Hotkey} to start dictating.",
            System.Windows.Forms.ToolTipIcon.Info
        );
        Log.Debug("Startup complete.");
    }

    private static LogEventLevel ResolveMinimumLogLevel(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)))
        {
            return LogEventLevel.Verbose;
        }

        var fromEnv = Environment.GetEnvironmentVariable(LogLevelEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && Enum.TryParse<LogEventLevel>(fromEnv, ignoreCase: true, out var level))
        {
            return level;
        }

        return LogEventLevel.Debug;
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception on UI dispatcher thread.");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception in AppDomain (terminating: {IsTerminating}).", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private void SetupHotkeyListener()
    {
        _messageWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Visibility = Visibility.Hidden
        };
        _messageWindow.Show();
        _messageWindow.Hide();

        var helper = new WindowInteropHelper(_messageWindow);
        IntPtr hWnd = helper.EnsureHandle();
        Log.Debug("Hotkey message window created (hWnd: 0x{Handle:X}).", hWnd.ToInt64());

        _hotkeyManager.HotkeyTriggered += (_, _) => Dispatcher.InvokeAsync(ToggleDictationAsync);
        _hotkeyManager.Register(_settings.Hotkey, hWnd);
    }

    private async Task LoadActiveModelAsync()
    {
        var modelPath = _modelManager.GetModelFilePath(_settings.SelectedModel);
        if (!File.Exists(modelPath))
        {
            Log.Warning("Model file {Path} does not exist. Opening setup.", modelPath);
            OpenSettingsWindow();
            return;
        }

        try
        {
            Log.Information("Initializing Whisper model: {Model} with provider {Provider}", _settings.SelectedModel, _settings.Provider);
            var sw = Stopwatch.StartNew();
            await _transcriptionService.InitializeAsync(modelPath, _settings.Provider);
            Log.Information("Whisper initialized successfully. Active Provider: {ActiveProvider}", _transcriptionService.ActiveProvider);
            Log.Debug("Model load took {Elapsed} ms ({Path}).", sw.ElapsedMilliseconds, modelPath);
            _trayManager.RebuildModelMenu();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load Whisper model: {Message}", ex.Message);
            _trayManager.ShowNotification("Engine Error", $"Failed to load Whisper model: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    private async Task SwitchModelAsync(ModelSize newSize)
    {
        Log.Debug("Model switch requested: {From} -> {To}.", _settings.SelectedModel, newSize);
        if (_settings.SelectedModel == newSize && _transcriptionService.IsInitialized)
        {
            Log.Debug("Model {Model} already active; nothing to do.", newSize);
            return;
        }

        if (!_modelManager.IsDownloaded(newSize))
        {
            Log.Debug("Model switch aborted: {Model} is not downloaded.", newSize);
            _trayManager.ShowNotification("Model Not Downloaded", $"Please download the {newSize} model from Settings first.", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        _settings.SelectedModel = newSize;
        AppPathResolver.SaveSettings(_settings);
        await LoadActiveModelAsync();
    }

    private async Task ToggleDictationAsync()
    {
        Log.Debug("Toggle dictation requested (currently dictating: {IsDictating}).", _isDictating);

        if (!_transcriptionService.IsInitialized)
        {
            Log.Warning("Toggle ignored: inference engine is not initialized.");
            _trayManager.ShowNotification("NativeVox", "Inference engine is not initialized.", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        if (!_isDictating)
        {
            // START DICTATION
            _isDictating = true;
            _dictationSessionId++;
            _trayManager.SetRecordingState(true);

            if (_settings.EnablePulsingHUD)
            {
                _overlayWindow.ShowNearCursor();
            }

            _textInjectionService.Reset();
            _dictationCts = new CancellationTokenSource();

            try
            {
                await _audioCaptureService.StartAsync(_settings.InputDeviceIndex, _dictationCts.Token);
                _dictationStopwatch.Restart();
                Log.Information("Dictation session #{Session} started (input device index {Device}, progressive typing: {Progressive}).",
                    _dictationSessionId, _settings.InputDeviceIndex, _settings.DirectProgressiveTypingEnabled);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start audio recording.");
                _isDictating = false;
                _trayManager.SetRecordingState(false);
                _overlayWindow.HideOverlay();
                return;
            }

            // Progressive transcription background polling loop
            if (_settings.DirectProgressiveTypingEnabled)
            {
                _ = RunProgressiveTranscriptionLoopAsync(_dictationCts.Token);
            }
        }
        else
        {
            // STOP DICTATION & FINALIZE
            _isDictating = false;
            _trayManager.SetRecordingState(false);
            _overlayWindow.HideOverlay();

            _dictationCts?.Cancel();

            try
            {
                byte[] fullAudio = await _audioCaptureService.StopAsync();
                Log.Information("Dictation session #{Session} stopped after {Wall:F1}s. Captured {Bytes} bytes ({Seconds:F1}s of audio).",
                    _dictationSessionId, _dictationStopwatch.Elapsed.TotalSeconds, fullAudio.Length, fullAudio.Length / 32000.0);

                if (fullAudio.Length > 0)
                {
                    await _transcriptionLock.WaitAsync();
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var finalText = await _transcriptionService.TranscribeAsync(fullAudio);
                        Log.Debug("Final transcription took {Elapsed} ms ({Length} chars): \"{Text}\"", sw.ElapsedMilliseconds, finalText.Length, finalText);

                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            _textInjectionService.InjectProgressiveText(finalText);
                        }
                        else
                        {
                            Log.Debug("Final transcription was empty; nothing to inject.");
                        }
                    }
                    finally
                    {
                        _transcriptionLock.Release();
                    }
                }
                else
                {
                    Log.Warning("No audio was captured during session #{Session}.", _dictationSessionId);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error finalizing transcription: {Message}", ex.Message);
            }
            finally
            {
                _textInjectionService.FinalizeSession();
            }
        }
    }

    private async Task RunProgressiveTranscriptionLoopAsync(CancellationToken ct)
    {
        int passes = 0, skippedBusy = 0, skippedShort = 0;
        long totalInferenceMs = 0, maxInferenceMs = 0;
        Log.Debug("Progressive transcription loop started.");

        // Periodic chunk inference every 650ms for near real-time progressive typing
        while (!ct.IsCancellationRequested && _isDictating)
        {
            try
            {
                await Task.Delay(650, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_isDictating || ct.IsCancellationRequested) break;

            if (_transcriptionLock.CurrentCount == 0) // Skip if previous inference is still processing
            {
                skippedBusy++;
                Log.Verbose("Progressive tick skipped: previous inference still running.");
                continue;
            }

            try
            {
                await _transcriptionLock.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var snapshot = _audioCaptureService.GetCurrentAudioSnapshot();
                // Whisper operates best with at least ~500ms of audio (16000 samples/sec * 2 bytes/sample * 0.5s = 16,000 bytes)
                if (snapshot.Length >= 16000 && _isDictating)
                {
                    var sw = Stopwatch.StartNew();
                    var partialText = await _transcriptionService.TranscribeAsync(snapshot, ct);
                    sw.Stop();

                    passes++;
                    totalInferenceMs += sw.ElapsedMilliseconds;
                    maxInferenceMs = Math.Max(maxInferenceMs, sw.ElapsedMilliseconds);
                    Log.Verbose("Partial #{Pass}: {Seconds:F1}s audio -> {Elapsed} ms: \"{Text}\"",
                        passes, snapshot.Length / 32000.0, sw.ElapsedMilliseconds, partialText);

                    if (!string.IsNullOrWhiteSpace(partialText) && _isDictating)
                    {
                        _textInjectionService.InjectProgressiveText(partialText);
                    }
                }
                else
                {
                    skippedShort++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Progressive chunk transcription error: {Message}", ex.Message);
            }
            finally
            {
                _transcriptionLock.Release();
            }
        }

        Log.Debug("Progressive loop ended: {Passes} passes (avg {Avg} ms, max {Max} ms), skipped {Busy} busy / {Short} too-short ticks.",
            passes, passes > 0 ? totalInferenceMs / passes : 0, maxInferenceMs, skippedBusy, skippedShort);
    }

    private void OpenSettingsWindow()
    {
        Log.Debug("Opening settings window.");
        var settingsWindow = new SettingsWindow(_modelManager, _settings, async () =>
        {
            Log.Debug("Settings updated; reloading model and re-registering hotkey.");
            await LoadActiveModelAsync();
            if (_messageWindow != null)
            {
                var helper = new WindowInteropHelper(_messageWindow);
                _hotkeyManager.Register(_settings.Hotkey, helper.Handle);
            }
        });

        settingsWindow.ShowDialog();
        Log.Debug("Settings window closed.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("NativeVox exiting (exit code {Code}). Disposing resources...", e.ApplicationExitCode);

        _hotkeyManager?.Dispose();
        _audioCaptureService?.Dispose();
        _transcriptionService?.Dispose();
        _trayManager?.Dispose();
        _overlayWindow?.Close();
        _messageWindow?.Close();
        _singleInstanceMutex?.Dispose();

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

/// <summary>
/// Adds the managed thread id to every log event, which helps untangle UI-thread vs. background work.
/// </summary>
internal sealed class ThreadIdEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
