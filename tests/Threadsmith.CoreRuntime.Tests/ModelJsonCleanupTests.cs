namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Verifies common cleanup of model-authored JSON before schema validation.</summary>
public static class ModelJsonCleanupTests
{
    /// <summary>Prose and Markdown framing do not conceal a complete JSON object.</summary>
    [Theory]
    [InlineData("Here is the result: {\"value\":1}", "{\"value\":1}")]
    [InlineData("```json\n{\"value\":1}\n```", "{\"value\":1}")]
    [InlineData("{\"value\":1}\nDone.", "{\"value\":1}")]
    [InlineData("Example: {}. Actual: {\"value\":1}", "{\"value\":1}")]
    [InlineData("{\"nested\":{\"value\":1}}", "{\"nested\":{\"value\":1}}")]
    public static void Clean_ExtractsJsonValue(string input, string expected)
    {
        Assert.Equal(expected, ModelJsonCleanup.Clean(input));
    }

    /// <summary>Unrecoverable text remains available for the existing validation error path.</summary>
    [Fact]
    public static void Clean_LeavesInvalidTextForValidator()
    {
        Assert.Equal("not JSON", ModelJsonCleanup.Clean(" not JSON "));
    }
}
