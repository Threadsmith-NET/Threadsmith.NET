namespace Threadsmith.Interaction.Presentation;

using Threadsmith.Core;

/// <summary>Stored memory display fields, without vectors or storage implementation details.</summary>
public sealed record RepositoryMemoryListItem(
    RepositoryMemoryId Id,
    string Text,
    RepositoryMemoryOrigin Origin,
    RepositoryMemoryType MemoryType,
    RepositoryMemoryOwnership Ownership)
{
    /// <summary>Observed content revision used to reject stale edits.</summary>
    public long? Revision { get; init; }
}

/// <summary>One immutable read-only memory list and its storage warnings.</summary>
public sealed record RepositoryMemoryListSnapshot(
    IReadOnlyList<RepositoryMemoryListItem> Entries,
    IReadOnlyList<string> Warnings);
