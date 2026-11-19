namespace Threadsmith.NativeTools.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

public sealed partial class Plan89CodeExploreTests
{
    /// <summary>The outer deadline leaves enough time for the default semantic timeout to return a partial result.</summary>
    [Fact]
    public void CodeExplore_DefaultTimeouts_PreserveGracefulSemanticTimeout()
    {
        var options = new CodeExploreOptions();

        Assert.Equal(60_000, options.Limits.TimeoutMilliseconds);
        Assert.Equal(75_000, options.OuterTimeoutMilliseconds);
        Assert.True(options.OuterTimeoutMilliseconds > options.Limits.TimeoutMilliseconds);
    }

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

        if (total == 0)
        {
            var formatter = new CodeExploreOutputFormattingTool(
                fixture.Tool, new CodeExploreOutputOptions(), TestPromptLoader.Instance, options);
            var output = await formatter.ExecuteAsync(new CodeExploreInput { Query = "Large.cs" }, fixture.Context, TestContext.Current.CancellationToken);
            var result = Assert.IsType<CodeExploreResult>(output.Value);
            Assert.Equal(int.MaxValue, fixture.Tool.Definition.MaximumOutputBytes);
            Assert.Equal(int.MaxValue, result.EffectiveMaximumMarkdownBytes);
            Assert.Equal(CodeExploreSourceCompleteness.Complete, Assert.Single(result.FileSections).Source.Completeness);
            var json = JsonSerializer.Serialize(result);
            var markdown = Assert.IsType<string>(output.ModelResultContent);
            const int cap = 1024;
            Assert.True(Encoding.UTF8.GetByteCount(json) > cap);
            Assert.True(Encoding.UTF8.GetByteCount(markdown) > cap);

            var cappedOptions = options with { MaximumResultBytes = cap, MaximumMarkdownBytes = cap };
            var cappedFormatter = new CodeExploreOutputFormattingTool(
                new CodeExploreTool(fixture.Service, TestPromptLoader.Instance, options: cappedOptions),
                new CodeExploreOutputOptions(),
                TestPromptLoader.Instance,
                cappedOptions);
            var capped = await cappedFormatter.ExecuteAsync(new CodeExploreInput { Query = "Large.cs" }, fixture.Context, TestContext.Current.CancellationToken);

            Assert.True(capped.IsTruncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(capped.Value)), 1, cap);
            Assert.InRange(Encoding.UTF8.GetByteCount(Assert.IsType<string>(capped.ModelResultContent)), 1, cap);
        }
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
