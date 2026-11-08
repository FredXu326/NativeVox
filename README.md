# NativeVox 🎙️

**NativeVox** is a lightweight, low-latency, 100% on-device speech-to-text dictation utility for Windows, powered by **Whisper.net** and **WPF (.NET 10)**.

Designed for frictionless offline dictation, NativeVox lives quietly in your system tray, captures audio with a global toggle hotkey, and injects transcribed text directly into any active application in real time.

---

## 🌟 Key Features

* **100% Offline & Private**: Zero cloud API calls or network telemetry. All audio processing runs locally on your machine.
* **Direct Progressive Typing**: Real-time diff-and-backspace algorithm progressively types words into your active document/editor as you speak, dynamically adjusting when Whisper refines sentence context.
* **Global Toggle Hotkey**: Instant push-to-toggle (`Ctrl + Space` by default) via standard Win32 hooks that avoid antivirus false positives.
* **Cursor-Anchored Pulsing HUD**: A lightweight, borderless floating visual indicator with an audio-reactive pulsing ring positioned adjacent to your mouse cursor while dictating. Does not steal window focus (`WS_EX_NOACTIVATE`).
* **Hardware Acceleration**: Automatic GPU execution provider detection (CUDA) with seamless fallback to optimized CPU inference.
* **UIPI Elevation Guard**: If dictating into an elevated Administrator window while running as a standard user, NativeVox automatically diverts the transcribed text to your Windows Clipboard (`Ctrl+V`) and alerts you via the tray.
* **Pluggable Architecture**: Modular clean architecture separating domain abstractions (`ITranscriptionService`), infrastructure drivers (NAudio with Polly resilience policies, Win32 SendInput), and the WPF presentation layer.
* **First-Run Onboarding**: Integrated setup wizard to download your preferred Whisper model (Tiny, Base, Small, Medium, Large) directly from Hugging Face.

---

## 🏗️ Architecture

```
NativeVox/
├── NativeVox.Core/               # Domain interfaces, models, abstractions
│   ├── Models/                   # AppSettings, ModelInfo, ModelSize, HotkeyConfig
│   └── Services/                 # Service interfaces
├── NativeVox.Infrastructure/     # Implementations & hardware interop
│   ├── Whisper/                  # Whisper.net integration, model manager, CUDA/CPU
│   ├── Audio/                    # NAudio capture and recovery
│   ├── Injection/                # Win32 SendInput, DiffEngine, elevation detector
│   ├── Hotkey/                   # Win32 RegisterHotKey manager
│   └── Storage/                  # AppPathResolver (%AppData%\NativeVox\)
├── NativeVox.App/                # WPF application (.NET 10)
│   ├── Views/                    # Overlay, setup, and settings
│   ├── Tray/                     # Windows system tray
│   └── App.xaml                  # Application entry point
├── NativeVox.Tests/              # xUnit tests for DiffEngine
└── NativeVox.sln
```

---

## 🚀 Getting Started

### Prerequisites
* **Windows 10 / 11** (x64)
* **.NET 10 SDK** (Installed locally in `%LocalAppData%\dotnet` or system path)

### Building from Source

```powershell
# Restore and build the complete solution
dotnet build NativeVox.sln

# Run unit tests
dotnet test NativeVox.sln

# Launch the application
dotnet run --project NativeVox.App
```

---

## ⚙️ Configuration & Data Storage

All runtime data is unified under `%AppData%\NativeVox\`:
* **Settings**: `%AppData%\NativeVox\settings.json`
* **Models**: `%AppData%\NativeVox\models\` (GGML binary models)
* **Rolling Logs**: `%AppData%\NativeVox\logs\nativevox-.log` (Serilog 7-day retention)

---

## 📜 License
Open Source under the [MIT License](LICENSE).
