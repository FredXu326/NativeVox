namespace NativeVox.App;

using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Serilog;
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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logFile, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("NativeVox starting up on .NET 10 (Target OS: Windows)...");

        // 3. Instantiate Services
        _settings = AppPathResolver.LoadSettings();
        _modelManager = new ModelManager();
        _transcriptionService = new WhisperTranscriptionService();
        _audioCaptureService = new NAudioCaptureService();
        _textInjectionService = new Win32SendInputService();
        _hotkeyManager = new Win32HotkeyManager();

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
            await _transcriptionService.InitializeAsync(modelPath, _settings.Provider);
            Log.Information("Whisper initialized successfully. Active Provider: {ActiveProvider}", _transcriptionService.ActiveProvider);
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
        if (_settings.SelectedModel == newSize && _transcriptionService.IsInitialized) return;

        if (!_modelManager.IsDownloaded(newSize))
        {
            _trayManager.ShowNotification("Model Not Downloaded", $"Please download the {newSize} model from Settings first.", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        _settings.SelectedModel = newSize;
        AppPathResolver.SaveSettings(_settings);
        await LoadActiveModelAsync();
    }

    private async Task ToggleDictationAsync()
    {
        if (!_transcriptionService.IsInitialized)
        {
            _trayManager.ShowNotification("NativeVox", "Inference engine is not initialized.", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        if (!_isDictating)
        {
            // START DICTATION
            _isDictating = true;
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

                if (fullAudio.Length > 0)
                {
                    await _transcriptionLock.WaitAsync();
                    try
                    {
                        var finalText = await _transcriptionService.TranscribeAsync(fullAudio);
                        if (!string.IsNullOrWhiteSpace(finalText))
                        {
                            _textInjectionService.InjectProgressiveText(finalText);
                        }
                    }
                    finally
                    {
                        _transcriptionLock.Release();
                    }
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

            if (_transcriptionLock.CurrentCount == 0) continue; // Skip if previous inference is still processing

            await _transcriptionLock.WaitAsync(ct);
            try
            {
                var snapshot = _audioCaptureService.GetCurrentAudioSnapshot();
                // Whisper operates best with at least ~500ms of audio (16000 samples/sec * 2 bytes/sample * 0.5s = 16,000 bytes)
                if (snapshot.Length >= 16000 && _isDictating)
                {
                    var partialText = await _transcriptionService.TranscribeAsync(snapshot, ct);
                    if (!string.IsNullOrWhiteSpace(partialText) && _isDictating)
                    {
                        _textInjectionService.InjectProgressiveText(partialText);
                    }
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
    }

    private void OpenSettingsWindow()
    {
        var settingsWindow = new SettingsWindow(_modelManager, _settings, async () =>
        {
            await LoadActiveModelAsync();
            if (_messageWindow != null)
            {
                var helper = new WindowInteropHelper(_messageWindow);
                _hotkeyManager.Register(_settings.Hotkey, helper.Handle);
            }
        });

        settingsWindow.ShowDialog();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("NativeVox exiting. Disposing resources...");

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
