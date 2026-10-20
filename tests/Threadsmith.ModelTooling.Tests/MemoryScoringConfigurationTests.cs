namespace Threadsmith.ModelTooling.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks independent weighted scoring configuration at the actual layered binding boundary.</summary>
public sealed class MemoryScoringConfigurationTests
{
    /// <summary>Rejects invalid admission policy without silently altering configuration.</summary>
    [Theory]
    [InlineData("Recall")]
    [InlineData("Reconciliation")]
    [InlineData("Intelligence:Recall")]
    [InlineData("Intelligence:Reconciliation")]
    public static void Routes_are_independent_and_require_explicit_admission(string route)
    {
        var values = new Dictionary<string, string?>
        {
            [$"tools:config:memories:{route}:RerankerEnabled"] = "true",
            [$"tools:config:memories:{route}:Scoring:Mode"] = "Weighted",
            [$"tools:config:memories:{route}:Scoring:FinalThreshold"] = "0.3",
            [$"tools:config:memories:{route}:Scoring:LexicalCandidateLimit"] = "7",
        };
        var options = Read(values);
        var routes = new Dictionary<string, RepositoryMemorySearchOptions>
        {
            ["Recall"] = RepositoryMemorySearchOptions.ForRecall(options),
            ["Reconciliation"] = RepositoryMemorySearchOptions.ForReconciliation(options),
            ["Intelligence:Recall"] = options.Intelligence.Recall,
            ["Intelligence:Reconciliation"] = options.Intelligence.Reconciliation,
        };
        Assert.All(routes, item => Assert.Equal(item.Key == route ? RepositoryMemoryScoringMode.Weighted : RepositoryMemoryScoringMode.Prior, item.Value.Scoring.Mode));
        Assert.Equal(0.3, routes[route].Scoring.FinalThreshold);
        Assert.Equal(7, routes[route].Scoring.LexicalCandidateLimit);
        Assert.Null(routes[route].Scoring.SemanticDiscoveryMinimum);
        values.Remove($"tools:config:memories:{route}:Scoring:FinalThreshold");
        Assert.Throws<ArgumentException>(() => Read(values));
        values[$"tools:config:memories:{route}:Scoring:FinalThreshold"] = "0";
        values[$"tools:config:memories:{route}:RerankerEnabled"] = "false";
        Assert.Throws<ArgumentException>(() => Read(values));
    }

    /// <summary>Rejects invalid admission policy without silently altering configuration.</summary>
    [Theory]
    [InlineData("SemanticWeight", "NaN")]
    [InlineData("LexicalWeight", "-0.1")]
    [InlineData("CrossEncoderWeight", "Infinity")]
    [InlineData("SemanticWeight", "0.5")]
    [InlineData("FinalThreshold", "-0.1")]
    [InlineData("FinalThreshold", "1.1")]
    [InlineData("FinalThreshold", "NaN")]
    [InlineData("SemanticDiscoveryMinimum", "NaN")]
    [InlineData("SemanticCandidateLimit", "0")]
    [InlineData("LexicalCandidateLimit", "129")]
    [InlineData("ConceptCandidateLimit", "-1")]
    [InlineData("Mode", "5")]
    public static void Invalid_configuration_is_rejected(string setting, string value)
    {
        Assert.Throws<ArgumentException>(() => Read(new()
        {
            [$"tools:config:memories:Recall:Scoring:{setting}"] = value,
        }));
    }

    /// <summary>Preserves old configuration and documents the numeric weight tolerance.</summary>
    [Fact]
    public static void Old_configuration_keeps_prior_mode_and_weight_tolerance_is_explicit()
    {
        Assert.Equal(RepositoryMemoryScoringMode.Prior, Read([]).RecallScoring.Mode);
        new RepositoryMemoryScoringOptions { SemanticWeight = 0.25 + 1e-10 }.Validate(false);
        Assert.Throws<ArgumentException>(() => new RepositoryMemoryScoringOptions { SemanticWeight = 0.25 + 1e-8 }.Validate(false));
    }

    private static RepositoryMemoryOptions Read(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new RepositoryMemoryConfiguration(config, new ConfigurationBuilder().Build(), Path.GetTempPath()).CaptureCurrent();
    }
}
