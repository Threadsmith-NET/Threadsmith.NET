namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Xunit;

/// <summary>Checks default and explicit frontend selection without starting a terminal.</summary>
public static class FrontendSelectionTests
{
    /// <summary>Bare TUI selection uses TUIKit while the original frontend remains explicit.</summary>
    [Theory]
    [InlineData("--tui", true)]
    [InlineData("--tui=tuikit", true)]
    [InlineData("--TUI=TUIKit", true)]
    [InlineData("--tui=original", false)]
    [InlineData("--TUI=Original", false)]
    public static void SelectsFrontend(string argument, bool expectedTuiKit)
    {
        var parsed = CommandLineParser.Parse([argument, "request"]);
        Assert.Null(parsed.Error);
        var options = Assert.IsType<CommandLineOptions>(parsed.Options);
        Assert.True(options.UseInteractiveTerminal);
        Assert.Equal(
            expectedTuiKit ? InteractiveFrontendKind.TuiKit : InteractiveFrontendKind.Original,
            options.InteractiveFrontend);
        Assert.Equal(["request"], options.RequestArguments);
    }

    /// <summary>Invalid and conflicting selections fail before startup side effects.</summary>
    [Theory]
    [InlineData("--tui=")]
    [InlineData("--tui=pretty")]
    [InlineData("--tui=unknown")]
    public static void RejectsUnknownFrontend(string argument) => Assert.NotNull(CommandLineParser.Parse([argument]).Error);

    /// <summary>Duplicate TUI switches fail and MCP retains precedence over either frontend.</summary>
    [Fact]
    public static void RejectsConflictingModes()
    {
        Assert.NotNull(CommandLineParser.Parse(["--tui", "--tui=tuikit"]).Error);
        var mcp = Assert.IsType<CommandLineOptions>(CommandLineParser.Parse(["--tui=tuikit", "--mcp", "list"]).Options);
        Assert.Equal("list", mcp.McpAction);
        Assert.False(IntegrationComposition.ShouldComposeExtensionHost(mcp));
        Assert.False(Assert.IsType<CommandLineOptions>(CommandLineParser.Parse([]).Options).UseInteractiveTerminal);
    }
}
