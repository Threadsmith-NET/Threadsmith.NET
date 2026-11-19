namespace Threadsmith.Planning.Tests;

using Threadsmith.Execution;
using Xunit;

/// <summary>Checks which C# searches qualify for semantic redirection.</summary>
public static class SemanticFirstSearchPolicyTests
{
    /// <summary>A C# glob does not turn a capitalized word in prose into a symbol query.</summary>
    [Theory]
    [InlineData("Optional applicability hints", false)]
    [InlineData("BuiltInTools", true)]
    public static void CapitalizationRequiresWholeSymbolQuery(string query, bool expected)
    {
        Assert.Equal(expected, SemanticFirstSearchPolicy.IsCSharpDiscoveryQuery(query, "*BuiltInTools.cs"));
    }
}
