# NativeVox agent instructions

## Project map and boundaries

NativeVox is a Windows desktop dictation app using .NET 10, WPF, NAudio, and
Whisper.net. `NativeVox.App` owns presentation and lifecycle coordination;
`NativeVox.Core` contains models and service contracts;
`NativeVox.Infrastructure` implements audio, inference, storage, and Windows
interop; `NativeVox.Tests` contains xUnit tests. Check code when
`architecture.md` or other documentation differs from behavior.

Keep Core independent of UI, infrastructure, and Windows APIs. Put OS and
hardware interop in Infrastructure and presentation concerns in App. Preserve
offline transcription and user privacy: model downloads use the network, but
do not add other network calls, telemetry, or sensitive audio/transcript logging
without explicit authorization. Treat settings, models, logs, and clipboard
contents as user data.

## C# and resource management

- Follow existing C# conventions and nullable annotations. Model invalid states
  explicitly, validate inputs at boundaries, and handle errors where recovery or
  useful context is possible. Do not silently swallow unexpected failures.
- Make ownership and lifetime of streams, native handles, audio devices, model
  processors, event subscriptions, and cancellation sources explicit. Dispose
  owned resources and unsubscribe handlers; use `IAsyncDisposable` when cleanup
  is asynchronous. Ensure shutdown works after partial initialization or failure.
- Consider allocations and latency in audio and inference paths. Use
  `Span<T>`, `Memory<T>`, or pooling when ownership is clear and measurement
  justifies it; never access pooled memory after returning it. Keep Native AOT
  compatibility in mind when changing dependencies or runtime behavior.
- Do not add placeholders or incomplete behavior. Keep interfaces cohesive and
  avoid new dependencies or broad refactoring unless the task warrants them.

## Concurrency and UI

- Define state ownership and valid transitions for recording, transcription,
  model changes, cancellation, and shutdown. Prefer immutable state and minimize
  shared mutation; synchronize every shared invariant consistently.
- Use async operations end to end. Propagate cancellation through supported async
  boundaries, distinguish cancellation from errors, and dispose linked token
  sources. Track and observe background tasks; coordinate cancellation, task
  completion, and disposal so work cannot outlive resources it uses.
- Prefer channels for suitable producer-consumer pipelines; define capacity,
  backpressure, completion, and cancellation. Use `Interlocked` for appropriate
  atomic transitions and short locks for compound invariants. Do not assume
  lock-free code is safer; avoid blocking waits and never await while holding a
  monitor lock. Handle channel closure and disposal races where expected without
  hiding unrelated errors.
- Marshal WPF UI access to the dispatcher. Keep inference and blocking work off
  the UI thread, observe async exceptions, and preserve focus and keyboard behavior.
  Avoid thread starvation and avoid holding synchronization primitives across
  slow I/O or callbacks unless required by a documented invariant.
- Test meaningful races and lifecycle paths with deterministic coordination where
  practical, including cancellation, stop, failure, and disposal. Avoid tests
  that depend on timing, a microphone, GPU, network, or an active desktop when
  isolation is possible; report required manual Windows checks.

## Build and verification

Run from the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet build NativeVox.sln
dotnet test NativeVox.sln
dotnet run --project NativeVox.App
```

Run checks appropriate to the change and report failures and limitations.
Running the app may require a microphone and a downloaded Whisper model.
Avoid modifying user data during verification without consent.
