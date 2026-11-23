namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks exact serialized output across code exploration budget boundaries.</summary>
public static class CodeExploreResultBudgetTests
{
    private const string BoundOmission = "Code exploration metadata was bounded to fit the effective output budget.";

    /// <summary>A result on the ceiling is returned intact.</summary>
    [Fact]
    public static void ExactFitPreservesResultBytes()
    {
        var input = CreateResult();
        AssertBoundedBytes(input, input);
    }

    /// <summary>Trimming stops once the summary removal makes the result fit.</summary>
    [Fact]
    public static void PresentationTrimPreservesRemainingBytes()
    {
        var input = CreateResult();
        AssertBoundedBytes(input, input with
        {
            Presentation = input.Presentation! with { ModelSummary = string.Empty },
            Omissions = [BoundOmission],
            Coverage = input.Coverage with { OutputComplete = false, Omissions = [BoundOmission] },
        });
    }

    /// <summary>The terminal fallback accounts for the omitted source continuation.</summary>
    [Fact]
    public static void TerminalFallbackPreservesResultBytes()
    {
        var input = CreateResult() with { FileSections = [], Omissions = [new string('z', 2048)] };
        var expected = new CodeExploreResult(
            1,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [],
            new CodeExploreCoverage(false, false, false, false, []),
            [BoundOmission],
            [])
        {
            OmittedSourceContinuationCount = 1,
        };
        AssertBoundedBytes(input, expected);
    }

    private static void AssertBoundedBytes(CodeExploreResult input, CodeExploreResult expected)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(expected);
        var actual = CodeExploreTool.BoundResultToMaximumBytes(input, bytes.Length, TestPromptLoader.Instance);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(actual));
    }

    private static CodeExploreResult CreateResult()
    {
        var hash = new string('a', 64);
        var source = new CodeExploreSourceRange(new SourceRange(3, 1, 3, 8), ["3: résumé"], hash, hash, CodeExploreSourceCompleteness.Complete, [], null);
        return new CodeExploreResult(
            1,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [new CodeExploreFileSection("A.cs", "Example", "net10.0", [], source, false, false, "match")],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            [new CodeExploreContinuationTarget(CodeExploreAnchorKind.SymbolName, "A", "A.cs", 4, 8, true, null, hash, 1, "more")])
        {
            Presentation = new CodeExplorePresentation(new string('é', 8192), [], [], []),
        };
    }
}
