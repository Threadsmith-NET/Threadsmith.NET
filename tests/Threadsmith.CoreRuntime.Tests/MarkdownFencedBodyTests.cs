namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Markdown;
using Xunit;

/// <summary>Verifies exact fenced-body source association at the parser boundary.</summary>
public static class MarkdownFencedBodyTests
{
    /// <summary>Unfinished fences remain marked incomplete and indented code never acquires a fence action.</summary>
    [Theory]
    [InlineData("```\nx", "x")]
    [InlineData("```\nx\n", "x\n")]
    public static void Parse_UnclosedFence_RetainsIncompleteMapping(string source, string expected)
    {
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var body = Assert.IsType<MarkdownFencedBody>(Assert.Single(CodeBlocks(document.Blocks)).FencedBody);
        Assert.False(body.IsClosed);
        Assert.True(body.TryGetText(source, out var text));
        Assert.Equal(expected, text);
        var indented = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse("    code").Document);
        Assert.Null(Assert.Single(CodeBlocks(indented.Blocks)).FencedBody);
    }

    private static IEnumerable<MarkdownCodeBlock> CodeBlocks(IEnumerable<MarkdownBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is MarkdownCodeBlock code)
            {
                yield return code;
            }
            else if (block is MarkdownQuote quote)
            {
                foreach (var child in CodeBlocks(quote.Blocks))
                {
                    yield return child;
                }
            }
            else if (block is MarkdownList list)
            {
                foreach (var child in CodeBlocks(list.Items.SelectMany(item => item.Blocks)))
                {
                    yield return child;
                }
            }
        }
    }
}
