namespace NativeVox.Core.Models;

/// <summary>
/// Hardware execution providers for transcription inference.
/// </summary>
public enum ExecutionProvider
{
    Auto,
    Cuda,
    Cpu
}
