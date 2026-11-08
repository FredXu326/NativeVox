namespace NativeVox.Infrastructure.Injection;

/// <summary>
/// Result of calculating the delta between previously injected text and newly transcribed text.
/// </summary>
public record TextDiffResult(int BackspacesNeeded, string TextToAppend);

/// <summary>
/// Calculates character-level diffs for real-time progressive typing.
/// </summary>
public static class DiffEngine
{
    public static TextDiffResult ComputeDiff(string previousText, string newText)
    {
        if (string.IsNullOrEmpty(previousText))
        {
            return new TextDiffResult(0, newText ?? string.Empty);
        }

        if (string.IsNullOrEmpty(newText))
        {
            return new TextDiffResult(previousText.Length, string.Empty);
        }

        int minLen = Math.Min(previousText.Length, newText.Length);
        int commonPrefixLen = 0;

        while (commonPrefixLen < minLen && previousText[commonPrefixLen] == newText[commonPrefixLen])
        {
            commonPrefixLen++;
        }

        int backspaces = previousText.Length - commonPrefixLen;
        string appendText = newText.Substring(commonPrefixLen);

        return new TextDiffResult(backspaces, appendText);
    }
}
