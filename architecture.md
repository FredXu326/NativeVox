# NativeVox Architecture & User Flows

This document details the system architecture and interactive user flows for **NativeVox**, rendered in Mermaid format.

---

## 1. System Architecture

NativeVox employs a **Clean, Layered Architecture** separating the WPF presentation layer, core domain abstractions, and OS/hardware interop infrastructure.

```mermaid
flowchart TD
    subgraph UI ["Presentation Layer (NativeVox.App)"]
        APP["App Orchestrator (App.xaml.cs)"]
        TRAY["TrayIconManager (WinForms NotifyIcon)"]
        HUD["OverlayWindow (Cursor Pulsing HUD)"]
        SETUP["Setup & Settings Views (WPF)"]
    end

    subgraph CORE ["Domain Abstractions (NativeVox.Core)"]
        I_TRANS["ITranscriptionService"]
        I_AUDIO["IAudioCaptureService"]
        I_INJECT["ITextInjectionService"]
        I_HOTKEY["IHotkeyManager"]
        I_MODEL["IModelManager"]
        MODELS["Models (AppSettings, ModelInfo, ExecutionProvider)"]
    end

    subgraph INFRA ["Infrastructure & OS Interop (NativeVox.Infrastructure)"]
        WHISPER["WhisperTranscriptionService (Whisper.net)"]
        MODEL_MGR["ModelManager (Hugging Face GGML Downloader)"]
        NAUDIO["NAudioCaptureService (16kHz PCM, Polly Resilience)"]
        HOTKEY_MGR["Win32HotkeyManager (RegisterHotKey / HWND)"]
        
        subgraph INJECTION ["Text Injection Engine"]
            SEND_INPUT["Win32SendInputService"]
            DIFF["DiffEngine (Prefix Matching & Backspaces)"]
            UIPI["UIPIElevationDetector (Win32 Token Check)"]
        end

        STORAGE["AppPathResolver (%AppData%/NativeVox)"]
    end

    subgraph OS_HW ["Windows OS & Hardware"]
        WIN_INPUT["Win32 SendInput / Foreground Window"]
        WIN_CLIP["Windows Clipboard (UIPI Fallback)"]
        MIC["Microphone Hardware (WASAPI/WaveIn)"]
        GPU_CPU["Inference Hardware (CUDA / CPU AVX2)"]
        FS["Local AppData Storage"]
    end

    %% Wiring
    APP --> TRAY
    APP --> HUD
    APP --> SETUP
    APP --> CORE

    I_TRANS -.-> WHISPER
    I_AUDIO -.-> NAUDIO
    I_INJECT -.-> SEND_INPUT
    I_HOTKEY -.-> HOTKEY_MGR
    I_MODEL -.-> MODEL_MGR

    WHISPER --> GPU_CPU
    MODEL_MGR --> FS
    NAUDIO --> MIC
    HOTKEY_MGR --> APP
    
    SEND_INPUT --> DIFF
    SEND_INPUT --> UIPI
    UIPI -- "Elevated Window" --> WIN_CLIP
    SEND_INPUT -- "Standard Window" --> WIN_INPUT
    STORAGE --> FS
```

### Component Breakdown

| Layer | Key Components | Responsibility |
| :--- | :--- | :--- |
| **Presentation** | `App.xaml.cs`, `TrayIconManager`, `OverlayWindow` | Single-instance Mutex lifecycle, global state coordination, non-activating floating HUD (`WS_EX_NOACTIVATE`), system tray icon with dynamic model menu. |
| **Core** | `ITranscriptionService`, `IAudioCaptureService`, `ITextInjectionService` | Inversion of control interfaces, immutable domain models, and settings state. Zero dependencies on OS APIs. |
| **Whisper Engine** | `WhisperTranscriptionService`, `ModelManager` | Manages native Whisper runtime lifecycle, CUDA execution provider initialization with CPU fallback, and asynchronous model downloading from Hugging Face. |
| **Audio Engine** | `NAudioCaptureService` | 16kHz mono 16-bit PCM streaming, continuous audio RMS level calculation (driving the pulsing HUD), and Polly fault recovery policies. |
| **Injection Engine** | `Win32SendInputService`, `DiffEngine`, `UIPIElevationDetector` | Progressive diff-and-backspace algorithm (`DiffEngine`), native Unicode input simulation (`SendInput`), and Windows UIPI elevation detection with clipboard fallback. |

---

## 2. User Flows

### 2.1 First-Run Onboarding Flow

Triggered on fresh installation or when no Whisper GGML model is detected.

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant App as NativeVox App
    participant Setup as SetupWindow
    participant ModelMgr as ModelManager
    participant Tray as TrayIconManager

    User->>App: Launch NativeVox.exe
    App->>App: Check single instance mutex
    App->>App: Check FirstRunCompleted & Model existence
    Note over App: No model found on disk
    App->>Setup: Display SetupWindow (Wizard)
    Setup->>User: Prompt model selection (Base recommended)
    User->>Setup: Click "Download & Continue"
    Setup->>ModelMgr: DownloadModelAsync(Base, progressCallback)
    ModelMgr-->>Setup: Stream download progress (0% - 100%)
    ModelMgr-->>App: Save GGML model to %AppData%/NativeVox/models/
    Setup->>App: Mark FirstRunCompleted = true
    App->>App: Initialize Whisper engine with downloaded model
    App->>Tray: Show "NativeVox Ready" tray notification
```

---

### 2.2 Live Dictation & Progressive Typing Flow

The primary interactive loop when dictating into any document, IDE, or chat app.

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant Hotkey as Win32HotkeyManager
    participant App as App.xaml.cs
    participant HUD as OverlayWindow
    participant Audio as NAudioCaptureService
    participant Whisper as WhisperTranscriptionService
    participant Diff as DiffEngine
    participant Inject as Win32SendInputService
    participant Target as Active Window (Editor/Notepad)

    User->>Hotkey: Press Toggle Hotkey (Ctrl + Space)
    Hotkey->>App: HotkeyTriggered event

    rect rgb(235, 245, 255)
        Note over App,HUD: Dictation Session Starts
        App->>HUD: ShowNearCursor() (Pulsing Red Dot)
        App->>Inject: Reset() session state
        App->>Audio: StartAsync() 16kHz audio capture
    end

    loop Every Audio Buffer (~100ms)
        Audio->>App: AudioLevelChanged(rms)
        App->>HUD: UpdateAudioLevel(scale ring)
    end

    loop Progressive Inference (~650ms intervals)
        App->>Audio: GetCurrentAudioSnapshot()
        App->>Whisper: TranscribeAsync(snapshot)
        Whisper-->>App: Return intermediate text: "Hello"
        App->>Inject: InjectProgressiveText("Hello")
        Inject->>Diff: CalculateDiff(previousText, newText)
        Diff-->>Inject: Backspaces: 0, AppendText: "Hello"
        Inject->>Target: SendInput(VK_UNICODE: "Hello")
        
        Note over Whisper,Inject: As user continues speaking: "Hello world"
        App->>Whisper: TranscribeAsync(longer snapshot)
        Whisper-->>App: Refined text: "Hello world,"
        App->>Inject: InjectProgressiveText("Hello world,")
        Inject->>Diff: CalculateDiff("Hello", "Hello world,")
        Diff-->>Inject: Backspaces: 0, AppendText: " world,"
        Inject->>Target: SendInput(VK_UNICODE: " world,")
    end

    User->>Hotkey: Press Toggle Hotkey again (Stop)
    Hotkey->>App: HotkeyTriggered event

    rect rgb(255, 245, 235)
        Note over App,Target: Finalization & Commit
        App->>HUD: HideOverlay()
        App->>Audio: StopAsync() returns full recorded PCM
        App->>Whisper: TranscribeAsync(fullAudio)
        Whisper-->>App: Final high-accuracy transcription
        App->>Inject: InjectProgressiveText(finalTranscription)
        Inject->>Diff: CalculateDiff(lastPartial, finalText)
        alt Whisper context correction required backspaces
            Inject->>Target: SendInput(VK_BACK x N)
        end
        Inject->>Target: SendInput(VK_UNICODE: remaining text)
        Inject->>Inject: FinalizeSession()
    end
```

---

### 2.3 UIPI Elevation Guard Flow

Handles edge cases such as typing into Administrator consoles (Task Manager, Admin Command Prompts, Registry Editor).

```mermaid
flowchart TD
    START["User Dictates into Target Window"] --> DETECT["UIPIElevationDetector: Check target window HWND"]
    DETECT --> ELEVATED{"Is Target Window Elevated AND NativeVox Non-Elevated?"}

    ELEVATED -- No (Safe) --> SEND["Win32 SendInput (Direct Keystrokes)"]
    SEND --> DONE["Text appears at active cursor"]

    ELEVATED -- Yes (Blocked by Windows UIPI) --> CLIPBOARD["Divert text to Windows Clipboard via WPF Dispatcher"]
    CLIPBOARD --> TOAST["Tray Notification: 'Direct typing blocked by UIPI. Text copied to clipboard (Ctrl+V).'"]
    TOAST --> PASTE["User presses Ctrl+V to paste into admin window"]
```

---

### 2.4 Runtime Model Switching Flow

Accessible on the fly via the Windows System Tray without restarting the application.

```mermaid
flowchart LR
    CLICK["Right-click Tray Icon"] --> MENU["Context Menu: Models (Tiny, Base, Small, Medium, Large)"]
    MENU --> SELECT{"Is selected model downloaded?"}
    
    SELECT -- Yes --> SWITCH["SwitchModelAsync() -> Re-initialize Whisper.net runtime"]
    SWITCH --> READY["Tray Notification: 'Switched to [Model] (CUDA/CPU)'"]
    
    SELECT -- No --> PROMPT["Open Settings Window -> Download model with progress bar"]
```
