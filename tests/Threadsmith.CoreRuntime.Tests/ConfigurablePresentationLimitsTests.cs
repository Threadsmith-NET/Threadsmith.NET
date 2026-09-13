namespace Threadsmith.CoreRuntime.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Tui;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Verifies configured bounds across admission and execution layers.</summary>
public sealed class ConfigurablePresentationLimitsTests
{
    /// <summary>Verifies the configured resource policy is honored.</summary>
    [Fact]
    public void ConfiguredMarkdownLimitsReachDocumentValidation()
    {
        var source = "| Name |\n| --- |\n" + string.Join('\n', Enumerable.Repeat("| entry |", 205));
        Assert.False(new MarkdownParser().Parse(source).Succeeded);
        var parsed = new MarkdownParser(new() { MaximumTableRows = 210 }).Parse(source);
        Assert.True(parsed.Succeeded);
        MarkdownValidator.Validate(parsed.Document!);
        Assert.False(new MarkdownParser(new() { MaximumSourceBytes = 10 }).Parse(source).Succeeded);
    }

    /// <summary>Verifies the configured resource policy is honored.</summary>
    [Fact]
    public void ComposerUsesConfiguredByteLimit()
    {
        // One grapheme still exceeds one MiB of UTF-8, without exercising the backend's
        // quadratic segmentation of a million separate graphemes in a limits test.
        var draft = "x" + new string('\u0301', 512 * 1024);
        Assert.Throws<InvalidOperationException>(() => new ComposerBuffer().Reset(draft));
        var composer = new ComposerBuffer(new TuiResourceLimits { MaximumDraftBytes = 2 * 1024 * 1024 });
        composer.Reset(draft);
        Assert.Equal(draft, composer.Text);
        Assert.Throws<InvalidOperationException>(() => new ComposerBuffer(new() { MaximumDraftBytes = 3 }).Reset("four"));
    }

    /// <summary>Verifies the configured resource policy is honored.</summary>
    [Fact]
    public void UnknownAndInvalidPresentationLimitsFailConfiguration()
    {
        var unknown = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:limits:maximumDraftByets"] = "100",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => TuiDisplayOptions.Load(unknown));
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:limits:maximumDraftBytes"] = "0",
        }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => TuiDisplayOptions.Load(invalid));
    }
}
