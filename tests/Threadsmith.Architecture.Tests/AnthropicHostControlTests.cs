namespace Threadsmith.Architecture.Tests;

using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Xunit;

/// <summary>Checks host controls and capability compatibility independent of provider networking.</summary>
public static class AnthropicHostControlTests
{
    /// <summary>Thinking inclusion is explicit, bounded syntax and never part of user request text.</summary>
    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public static void HeadlessThinking_IsCapturedOutsideRequest(string setting, bool expected)
    {
        var parsed = CommandLineParser.Parse(["--thinking", setting, "hello"]);
        Assert.Null(parsed.Error);
        var options = Assert.IsType<CommandLineOptions>(parsed.Options);
        Assert.Equal(expected, options.IncludeReasoningText);
        Assert.Equal(["hello"], options.RequestArguments);
        Assert.False(Assert.IsType<CommandLineOptions>(CommandLineParser.Parse(["hello"]).Options).IncludeReasoningText);
        Assert.NotNull(CommandLineParser.Parse(["--thinking"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["--thinking", "maybe"]).Error);
        Assert.NotNull(CommandLineParser.Parse(["--thinking", "on", "--thinking", "off"]).Error);
    }

    /// <summary>Summary display changes independently of effort and the captured in-flight generation.</summary>
    [Fact]
    public static void ThinkingPreference_DoesNotChangeReasoningOrSelectionGeneration()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var preferences = new SessionModelPreferences(profileId, ReasoningLevel.High);
        var before = preferences.Capture();
        preferences.SetIncludeReasoningText(true);
        Assert.True(preferences.IncludeReasoningText);
        Assert.Equal(before, preferences.Capture());
        preferences.SetIncludeReasoningText(false);
        Assert.Equal(ReasoningLevel.High, preferences.Reasoning);
        Assert.Equal(before.Generation, preferences.Generation);
    }

    /// <summary>Selectable always-thinking models require an explicit capability and a valid effort default.</summary>
    [Fact]
    public static void ReasoningWithoutOff_RequiresExplicitCapabilityAndRejectsNone()
    {
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "reviewed-model",
            ModelId = "reviewed-model",
            Provider = "test",
            Endpoint = new Uri("https://example.invalid/v1/messages"),
            ContextWindow = 10000,
            MaximumOutputTokens = 2000,
            DefaultReasoningLevel = ReasoningLevel.Medium,
            SupportedReasoningLevels = [ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High],
            ReasoningCapability = new EffectiveReasoningCapability
            {
                SchemaVersion = 1,
                Controllability = ReasoningControllability.Selectable,
                SupportsReasoningOff = false,
                DefaultLevel = ReasoningLevel.Medium,
                SupportedLevels = [ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High],
            },
        };
        var catalog = new ConfiguredModelCatalog([profile]);
        Assert.False(catalog.Get(profile.Id).SupportsReasoningLevel(ReasoningLevel.None));
        Assert.True(catalog.Get(profile.Id).SupportsReasoningLevel(ReasoningLevel.High));
        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([profile with
        {
            ReasoningCapability = profile.ReasoningCapability with { SupportsReasoningOff = null },
        }]));
        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([profile with { DefaultReasoningLevel = ReasoningLevel.None }]));
        var preferences = new SessionModelPreferences(new ModelProfileId(Guid.NewGuid()), ReasoningLevel.None);
        Assert.Equal(ReasoningLevel.Medium, preferences.ResolveFor(profile.Id, profile.DefaultReasoningLevel));
    }
}
