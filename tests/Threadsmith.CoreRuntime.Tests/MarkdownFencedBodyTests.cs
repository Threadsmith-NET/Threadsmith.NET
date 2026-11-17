namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Markdown;
using Xunit;

/// <summary>Verifies exact fenced-body source association at the parser boundary.</summary>
public static class MarkdownFencedBodyTests
{
    /// <summary>Source ranges preserve whitespace and endings while removing parser-established containers.</summary>
    [Theory]
    [InlineData("```text\r\nx\t  \r\n\r\n```", "x\t  \r\n\r\n")]
    [InlineData("> ```\n>   x\t \n> ```", "  x\t \n")]
    [InlineData("- ```\n  \tx  \n  ```", "\tx  \n")]
    [InlineData("```\nx\n```", "x\n")]
    [InlineData("```\n```", "")]
    [InlineData("```\n\n```", "\n")]
    [InlineData("~~~unknown attributes\nx\r\ny\rz\n~~~~", "x\r\ny\rz\n")]
    [InlineData("````markdown\n```\n**literal**\n````", "```\n**literal**\n")]
    [InlineData("```json\n{invalid\n```", "{invalid\n")]
    [InlineData("```\n\u001b[31m\t界😀  \n```", "\u001b[31m\t界😀  \n")]
    [InlineData("  ```\n  x\n  ```", "x\n")]
    [InlineData("> - ```\n>   x\n>   ```", "x\n")]
    [InlineData("- > ```\n  > x\n  > \n  > ```", "x\n\n")]
    [InlineData("```\nx\n````", "x\n")]
    [InlineData("````\n```\nx\n~~~~\n````", "```\nx\n~~~~\n")]
    [InlineData("```\n``` invalid\nx\n```", "``` invalid\nx\n")]
    [InlineData("- ```\n\tx\n  ```", "\tx\n")]
    public static void Parse_SourceMapping_PreservesExactBody(string source, string expected)
    {
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var block = Assert.Single(CodeBlocks(document.Blocks));
        var body = Assert.IsType<MarkdownFencedBody>(block.FencedBody);

        Assert.True(body.IsClosed);
        Assert.True(body.TryGetText(source, out var text));
        Assert.Equal(expected, text);
        Assert.False(body.TryGetText(new string(source.ToCharArray()), out _));
        Assert.DoesNotContain('\u001b', block.Code);
    }

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
