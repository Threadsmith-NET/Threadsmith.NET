namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Shared memory rendering preserves inspection data while neutralizing terminal controls.</summary>
public static class ContextUsageFormatterTests
{
    /// <summary>Automatic output shows situational and intelligence notes while inspection also shows preferences.</summary>
    [Fact]
    public static void AutomaticMemoryOutputOmitsOnlyStandingPreferences()
    {
        var situational = new RepositoryMemoryContextItemProjection
        {
            Id = RepositoryMemoryId.New(),
            Included = true,
            EstimatedTokens = 1,
            Text = "situational-fixture",
            Rationale = string.Empty,
        };
        var preference = situational with
        {
            Id = RepositoryMemoryId.New(),
            MemoryType = RepositoryMemoryType.StandingPreference,
            Text = "preference-fixture",
        };
        var intelligence = situational with
        {
            Id = RepositoryMemoryId.New(),
            Ownership = RepositoryMemoryOwnership.Intelligence,
            Text = "intelligence-fixture",
        };
        RepositoryMemoryContextItemProjection[] items = [situational, preference, intelligence];

        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(new MemoriesIncluded(SessionId.New(), DateTimeOffset.UnixEpoch, RunId.New(), items)));
        var automatic = transcript.Text;

        Assert.Contains(situational.Text, automatic, StringComparison.Ordinal);
        Assert.Contains(intelligence.Text, automatic, StringComparison.Ordinal);
        Assert.DoesNotContain(preference.Text, automatic, StringComparison.Ordinal);
        Assert.Contains(preference.Text, ContextUsageFormatter.FormatMemoryContext(items), StringComparison.Ordinal);
    }

    /// <summary>Durable inclusion events retain raw sanitized content; presentation encodes controls only at output.</summary>
    [Fact]
    public static void MemoryContentEncodesControlsAndPreservesMultilineUnicode()
    {
        var item = new RepositoryMemoryContextItemProjection
        {
            Id = RepositoryMemoryId.New(),
            Included = true,
            EstimatedTokens = 10,
            Ownership = RepositoryMemoryOwnership.Intelligence,
            Text = "first\r\nsecond λ\u009dtitle\u009c\u001b[2J",
            Rationale = "selected\u009b31m",
            Supports =
            [
                new RepositoryMemorySupport
                {
                    Path = "src/\u009b2J.cs",
                    ContentHash = "hash",
                    ObservedAt = DateTimeOffset.UtcNow,
                    IsDirty = true,
                    IsUnknown = true,
                },
            ],
        };

        var output = ContextUsageFormatter.FormatMemoryContext([item]);

        Assert.Contains("first\n  second λ\\u009Dtitle\\u009C\\u001B[2J", output, StringComparison.Ordinal);
        Assert.DoesNotContain("selected", output, StringComparison.Ordinal);
        Assert.Contains("- src/\\u009B2J.cs (changed since collection) (observation gap; current state uncertain)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", output, StringComparison.Ordinal);
        Assert.DoesNotContain(output, character => character is (< ' ' and not '\n' and not '\t') or (>= '\u007f' and <= '\u009f'));
        Assert.Equal("first\r\nsecond λ\u009dtitle\u009c\u001b[2J", item.Text);

        IDomainEvent expected = new MemoriesIncluded(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), [item]);
        var durable = Assert.IsType<MemoriesIncluded>(DomainEventJson.Deserialize(
            DomainEventJson.GetDiscriminator(expected), expected.SchemaVersion, DomainEventJson.Serialize(expected)));
        var polymorphic = Assert.IsType<MemoriesIncluded>(JsonSerializer.Deserialize<IDomainEvent>(JsonSerializer.Serialize(expected)));
        foreach (var restored in new[] { durable, polymorphic })
        {
            var actual = Assert.Single(restored.Items);
            Assert.Equal(item.Id, actual.Id);
            Assert.Equal(item.Text, actual.Text);
            Assert.Equal(item.Ownership, actual.Ownership);
            Assert.Equal(item.Included, actual.Included);
            Assert.Equal(item.Supports, actual.Supports);
            Assert.Equal(output, ContextUsageFormatter.FormatMemoryContext([actual]));
        }
    }
}
