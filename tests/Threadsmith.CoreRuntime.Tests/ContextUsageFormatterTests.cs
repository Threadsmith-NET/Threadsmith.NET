namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Shared memory rendering preserves inspection data while neutralizing terminal controls.</summary>
public static class ContextUsageFormatterTests
{
    /// <summary>Each memory category has one heading, readable lists, and no retrieval diagnostics.</summary>
    [Fact]
    public static void MemoriesUseGroupedMarkdown()
    {
        var first = new RepositoryMemoryContextItemProjection
        {
            Id = RepositoryMemoryId.New(), Included = true, EstimatedTokens = 1,
            Ownership = RepositoryMemoryOwnership.Intelligence,
            Text = "Follow the existing service.", Rationale = "Included by qualified hybrid retrieval and context budget.",
            Supports = [new RepositoryMemorySupport { Path = "src/Service.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        };
        var output = ContextUsageFormatter.FormatMemoryContext([
            first,
            first with { Id = RepositoryMemoryId.New(), Text = "Reuse the shared store.", Supports = [] },
            first with { Id = RepositoryMemoryId.New(), Ownership = RepositoryMemoryOwnership.Ordinary, Text = "Situational note.", Supports = [] },
            first with { Id = RepositoryMemoryId.New(), Ownership = RepositoryMemoryOwnership.Ordinary, MemoryType = RepositoryMemoryType.StandingPreference, Text = "Standing preference.", Supports = [] },
            first with { Id = RepositoryMemoryId.New(), Included = false, Text = "Omitted note.", Supports = [] },
        ]);

        var expected = """
            Repository Intelligence that may be useful to guide exploration and/or implementation:

            - Follow the existing service.

              Supporting files:

              - src/Service.cs

            - Reuse the shared store.

            Situational memories that may be useful to guide exploration and/or implementation:

            - Situational note.

            Repository standing preferences to guide exploration and/or implementation:

            - Standing preference.

            Repository Intelligence omitted from this request:

            - Omitted note.
            """.ReplaceLineEndings("\n") + "\n\n";
        Assert.Equal(expected, output);
        Assert.Empty(ContextUsageFormatter.FormatMemoryContext([]));
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
        Assert.DoesNotContain(output, character => character is < ' ' and not '\n' and not '\t' or >= '\u007f' and <= '\u009f');
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
