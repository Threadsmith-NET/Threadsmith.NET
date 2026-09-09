namespace Threadsmith.NativeTools.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

public sealed partial class Plan89CodeExploreTests
{
    /// <summary>Configured higher and disabled caps flow through the public adapter and semantic projection.</summary>
    [Theory]
    [InlineData(200_000, 80_000)]
    [InlineData(0, 0)]
    public async Task CodeExplore_ConfiguredSourceCaps_AreNotReplacedByCompiledCeilings(int total, int perFile)
    {
        var settings = new Dictionary<string, string?>
        {
            ["tools:codeExplore:adaptiveSizingEnabled"] = "false",
            ["tools:codeExplore:limits:maximumSourceCharacters"] = total.ToString(),
            ["tools:codeExplore:limits:maximumPerFileSourceCharacters"] = perFile.ToString(),
            ["tools:codeExplore:limits:maximumFiles"] = "32",
            ["tools:codeExplore:limits:timeoutMilliseconds"] = "0",
            ["tools:codeExplore:outerTimeoutMilliseconds"] = "0",
            ["tools:codeExplore:maximumQueryCharacters"] = "0",
            ["tools:codeExplore:maximumResultBytes"] = "0",
            ["tools:codeExplore:maximumMarkdownBytes"] = "0",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        using var ownedConfiguration = configuration as IDisposable;
        var options = CodeExploreConfiguration.FromConfiguration(configuration);
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var execution = await fixture.Tool.ExecuteAsync(new CodeExploreInput { Query = "Small.cs Large.cs", MaxFiles = 24 }, fixture.Context, TestContext.Current.CancellationToken);
        Assert.Equal(Timeout.InfiniteTimeSpan, fixture.Tool.Definition.Timeout);
        Assert.Equal(24, execution.Value.AdaptiveBudget?.EffectiveMaximumFiles);
        Assert.Equal(total == 0 ? int.MaxValue : total, execution.Value.AdaptiveBudget?.EffectiveMaximumSourceCharacters);
        Assert.Equal(perFile == 0 ? int.MaxValue : perFile, execution.Value.AdaptiveBudget?.EffectiveMaximumPerFileSourceCharacters);
        Assert.False(execution.Value.AdaptiveBudget?.AdaptiveDefaultsApplied);
        Assert.All(execution.Value.FileSections, section => Assert.Equal(CodeExploreSourceCompleteness.Complete, section.Source.Completeness));
        _ = fixture.Tool.DeserializeInput(JsonSerializer.Serialize(new CodeExploreInput { Query = new string('x', 2049) }));
    }

    /// <summary>A disabled controlling cap cannot be restored by a smaller adaptive tier.</summary>
    [Fact]
    public async Task CodeExplore_DisabledCapAndNarrowHint_SurviveAdaptation()
    {
        var options = new CodeExploreOptions
        {
            Limits = new CodeExploreLimits { MaximumSourceCharacters = 0, MaximumFiles = 32 },
            Tiny = new CodeExploreAdaptiveOptions { MaximumFiles = 4, MaximumSourceCharacters = 50, MaximumPerFileSourceCharacters = 0 },
        };
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var input = new CodeExploreInput { Query = "Small.cs", MaxFiles = 1 };
        var result = (await fixture.Tool.ExecuteAsync(input, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(int.MaxValue, result.AdaptiveBudget?.EffectiveMaximumSourceCharacters);
        Assert.Equal(1, result.AdaptiveBudget?.EffectiveMaximumFiles);
        Assert.Equal(16_384, result.AdaptiveBudget?.EffectiveMaximumPerFileSourceCharacters);
        Assert.True(result.AdaptiveBudget?.AdaptiveDefaultsApplied);
    }

    /// <summary>Explicit host limits equal to old defaults do not accidentally trigger adaptation.</summary>
    [Fact]
    public async Task CodeExplore_ExplicitDefaultValues_RetainTheirOrigin()
    {
        await using var fixture = await AllocationFixture.CreateAsync();
        var request = new CodeExploreRequest { Query = "Small.cs", Limits = new CodeExploreLimits() };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(50_000, result.AdaptiveBudget?.EffectiveMaximumSourceCharacters);
        Assert.False(result.AdaptiveBudget?.AdaptiveDefaultsApplied);
    }

    /// <summary>Explicit host requests normalize disabled values and retain narrower caller settings within administration.</summary>
    [Fact]
    public async Task CodeExplore_InternalRequest_UsesConfiguredCeilingsAndZeroSemantics()
    {
        var options = new CodeExploreOptions { AdaptiveSizingEnabled = false, Limits = new CodeExploreLimits { MaximumFiles = 1, MaximumSourceCharacters = 5000 } };
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var request = new CodeExploreRequest
        {
            Query = "Small.cs Large.cs",
            Limits = new CodeExploreLimits { MaximumFiles = 2, MaximumSourceCharacters = 0, MaximumPerFileSourceCharacters = 2000 },
        };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(1, result.AdaptiveBudget?.EffectiveMaximumFiles);
        Assert.Equal(5000, result.AdaptiveBudget?.EffectiveMaximumSourceCharacters);
        Assert.Equal(2000, result.AdaptiveBudget?.EffectiveMaximumPerFileSourceCharacters);
        Assert.Single(result.FileSections, section => section.Source.NumberedLines.Count > 0);
        Assert.False(result.AdaptiveBudget?.AdaptiveDefaultsApplied);
    }

    /// <summary>Compact presentation respects a configured smaller summary cap.</summary>
    [Fact]
    public async Task CodeExplore_CompactSummary_UsesConfiguredCap()
    {
        var options = new CodeExploreOptions { MaximumPresentationSummaryCharacters = 100 };
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var result = (await fixture.Tool.ExecuteAsync(new CodeExploreInput { Query = "Small.cs Large.cs" }, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.True(result.Presentation?.ModelSummary.Length <= 100);
    }

    /// <summary>Optional summary limits do not displace exact source when multiple anchors are selected.</summary>
    [Fact]
    public async Task CodeExplore_LowSummaryCap_RetainsExactSource()
    {
        var options = new CodeExploreOptions { AdaptiveSizingEnabled = false, MaximumNaturalLanguageCandidateSummaries = 1 };
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var result = (await fixture.Tool.ExecuteAsync(new CodeExploreInput { Query = "Small.cs Large.cs" }, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(2, result.FileSections.Count);
        Assert.True(result.CandidateSummaries?.Count <= 1);
    }

    /// <summary>Structured trimming cannot erase the tier's independently configured Markdown allowance.</summary>
    [Fact]
    public async Task CodeExplore_StructuredTrim_PreservesMarkdownTierCap()
    {
        var options = new CodeExploreOptions
        {
            MaximumResultBytes = 2500,
            Tiny = new CodeExploreAdaptiveOptions { MaximumMarkdownBytes = 500 },
        };
        await using var fixture = await AllocationFixture.CreateAsync(options);
        var tool = new CodeExploreOutputFormattingTool(fixture.Tool, new CodeExploreOutputOptions(), TestPromptLoader.Instance, options);
        var execution = await tool.ExecuteAsync(new CodeExploreInput { Query = "Small.cs Large.cs" }, fixture.Context, TestContext.Current.CancellationToken);
        Assert.Equal(500, Assert.IsType<CodeExploreResult>(execution.Value).EffectiveMaximumMarkdownBytes);
        Assert.True(Encoding.UTF8.GetByteCount(execution.ModelResultContent ?? string.Empty) <= 500);
    }

    /// <summary>Negative operational values fail binding even when enforcement is disabled.</summary>
    [Theory]
    [InlineData("maximumQueryCharacters")]
    [InlineData("limits:maximumFiles")]
    [InlineData("tiny:maximumSourceCharacters")]
    [InlineData("outerTimeoutMilliseconds")]
    public void CodeExplore_NegativeConfiguration_IsRejected(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:codeExplore:" + key] = "-1",
            ["tools:codeExplore:enforceOperationalLimits"] = "false",
        }).Build();
        using var ownedConfiguration = configuration as IDisposable;
        Assert.Throws<ArgumentOutOfRangeException>(() => CodeExploreConfiguration.FromConfiguration(configuration));
    }

    /// <summary>Small source does not reserve capacity that a larger exact target needs.</summary>
    [Fact]
    public async Task CodeExplore_SmallFile_ReleasesSpaceAndCompletesNearbyFile()
    {
        await using var fixture = await AllocationFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "Small.cs Large.cs",
            Limits = new CodeExploreLimits { MaximumSourceCharacters = 2400, MaximumPerFileSourceCharacters = 2400, MaximumFiles = 2 },
        };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        Assert.Equal(2, result.FileSections.Count);
        Assert.All(result.FileSections, section => Assert.Equal(CodeExploreSourceCompleteness.Complete, section.Source.Completeness));
        Assert.Empty(result.ContinuationTargets);
        Assert.True(result.Allocation?.SpentSourceCharacters <= 2400);
    }

    /// <summary>Exact one-line source and small partial ranges remain useful under tight caps.</summary>
    [Fact]
    public async Task CodeExplore_TinyExactRange_RemainsAvailable()
    {
        await using var fixture = await AllocationFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "requested source",
            PathAnchors = [new CodeExplorePathAnchor { Path = "Small.cs", Line = 4, SelectionMode = CodeExplorePathSelectionMode.SingleLine }],
            Limits = new CodeExploreLimits { MaximumSourceCharacters = 40, MaximumPerFileSourceCharacters = 40 },
        };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        var section = Assert.Single(result.FileSections);
        Assert.Equal(4, section.Source.Range.StartLine);
        Assert.Equal(4, section.Source.Range.EndLine);
        Assert.Single(section.Source.NumberedLines);
        Assert.True(Assert.Single(result.Allocation?.Files ?? []).UsefulSection);
    }

    /// <summary>Actual model capacity remains enforced when all operational caps are disabled.</summary>
    [Fact]
    public async Task CodeExplore_DisabledOperations_StillRespectModelCapacityAndCancellation()
    {
        await using var fixture = await AllocationFixture.CreateAsync(new CodeExploreOptions { EnforceOperationalLimits = false, AdaptiveSizingEnabled = false });
        var context = fixture.Context with { Invocation = fixture.Context.Invocation with { ModelEffectiveInputBudgetTokens = 1500 } };
        var input = new CodeExploreInput { Query = "Small.cs Large.cs" };
        var result = (await fixture.Tool.ExecuteAsync(input, context, TestContext.Current.CancellationToken)).Value;
        Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) <= 4500);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Tool.ExecuteAsync(input, fixture.Context, cancelled.Token));
    }
}
