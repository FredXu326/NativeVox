namespace NativeVox.Tests;

using NativeVox.Infrastructure.Injection;
using Xunit;

public class DiffEngineTests
{
    [Fact]
    public void ComputeDiff_WhenPreviousIsEmpty_AppendsAllTextWithoutBackspaces()
    {
        var diff = DiffEngine.ComputeDiff(string.Empty, "Hello world");

        Assert.Equal(0, diff.BackspacesNeeded);
        Assert.Equal("Hello world", diff.TextToAppend);
    }

    [Fact]
    public void ComputeDiff_WhenNewTextIsEmpty_BackspacesAllCharacters()
    {
        var diff = DiffEngine.ComputeDiff("Hello", string.Empty);

        Assert.Equal(5, diff.BackspacesNeeded);
        Assert.Equal(string.Empty, diff.TextToAppend);
    }

    [Fact]
    public void ComputeDiff_WhenNewTextAppendsMoreWords_ZeroBackspacesAndAppendsOnlySuffix()
    {
        var diff = DiffEngine.ComputeDiff("Hello", "Hello world");

        Assert.Equal(0, diff.BackspacesNeeded);
        Assert.Equal(" world", diff.TextToAppend);
    }

    [Fact]
    public void ComputeDiff_WhenWhisperRevisesEnding_SendsCorrectBackspacesAndNewSuffix()
    {
        // Example: Whisper heard "their" then revised to "there is"
        var diff = DiffEngine.ComputeDiff("I went their", "I went there is");

        // Common prefix: "I went the" (length 10)
        // "I went their" has length 12 -> 2 backspaces ("ir")
        // Appends "re is"
        Assert.Equal(2, diff.BackspacesNeeded);
        Assert.Equal("re is", diff.TextToAppend);
    }

    [Fact]
    public void ComputeDiff_WhenStringsAreIdentical_ZeroBackspacesAndEmptyAppend()
    {
        var diff = DiffEngine.ComputeDiff("Hello world", "Hello world");

        Assert.Equal(0, diff.BackspacesNeeded);
        Assert.Equal(string.Empty, diff.TextToAppend);
    }
}
