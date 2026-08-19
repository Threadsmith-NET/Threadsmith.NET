namespace Threadsmith.Core;

/// <summary>Pinned compatibility contract for standard Agent Skills and Claude extensions.</summary>
public enum ClaudeSkillCompatibilityVersion
{
    /// <summary>Agent Skills specification pinned on 2026-08-10 with documented Claude extensions.</summary>
    AgentSkills202508AndClaudeCode202508,
}

/// <summary>Closed compatibility outcome for an adapted skill directory.</summary>
public enum ClaudeSkillCompatibilityStatus
{
    /// <summary>The instruction-only skill maps without known semantic loss.</summary>
    Compatible,

    /// <summary>The skill remains usable but contains inert or unavailable features.</summary>
    CompatibleWithRestrictions,

    /// <summary>Required behavior cannot be represented safely.</summary>
    Unsupported,
}

/// <summary>Scope-qualified immutable identity for one Claude-style skill source.</summary>
public sealed record ClaudeSkillSourceIdentity
{
    /// <summary>Owning host scope.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>Bounded source label safe for ordinary diagnostics.</summary>
    public required string Source { get; init; }

    /// <summary>Normalized skill name.</summary>
    public required string Name { get; init; }

    /// <summary>Canonical lowercase SHA-256 digest, when activated.</summary>
    public string? Digest { get; init; }

    /// <summary>Catalog generation.</summary>
    public long Generation { get; init; }
}

/// <summary>Metadata-only Claude-style skill candidate.</summary>
public sealed record ClaudeSkillCandidate
{
    /// <summary>Immutable source identity.</summary>
    public required ClaudeSkillSourceIdentity Identity { get; init; }

    /// <summary>Bounded human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Pinned parser/adapter contract.</summary>
    public ClaudeSkillCompatibilityVersion Version { get; init; }

    /// <summary>Compatibility projection.</summary>
    public ClaudeSkillCompatibilityStatus Status { get; init; }

    /// <summary>Stable bounded reason codes.</summary>
    public IReadOnlyList<string> ReasonCodes { get; init; } = [];

    /// <summary>Mapped host tool ids.</summary>
    public IReadOnlyList<string> MappedTools { get; init; } = [];

    /// <summary>Unresolved advisory tool names.</summary>
    public IReadOnlyList<string> UnavailableTools { get; init; } = [];

    /// <summary>Bounded inert unknown metadata keys.</summary>
    public IReadOnlyList<string> UnknownMetadataKeys { get; init; } = [];
}

/// <summary>One immutable file identity included in a Claude-style activation snapshot.</summary>
public sealed record ClaudeSkillSnapshotFile
{
    /// <summary>Normalized skill-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>Exact file length.</summary>
    public long Bytes { get; init; }

    /// <summary>Lowercase SHA-256 hash of raw bytes.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>One immutable bounded compatibility snapshot loaded for governed adaptation.</summary>
public sealed record ClaudeSkillSnapshot
{
    /// <summary>Candidate with exact digest identity.</summary>
    public required ClaudeSkillCandidate Candidate { get; init; }

    /// <summary>Canonical package root retained only in host state.</summary>
    public required string PackageRoot { get; init; }

    /// <summary>Exact file identities contributing to the snapshot digest.</summary>
    public IReadOnlyList<ClaudeSkillSnapshotFile> Files { get; init; } = [];

    /// <summary>Untrusted Markdown instructions.</summary>
    public required string Instructions { get; init; }

    /// <summary>Confined strict-UTF-8 supporting text keyed by relative path.</summary>
    public IReadOnlyDictionary<string, string> Resources { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>Host-owned metadata and immutable activation boundary for Claude-style skills.</summary>
public interface IClaudeSkillCompatibilityCatalog
{
    /// <summary>Gets the latest immutable metadata-only candidates.</summary>
    IReadOnlyList<ClaudeSkillCandidate> Candidates { get; }

    /// <summary>Refreshes metadata-only discovery.</summary>
    Task<IReadOnlyList<ClaudeSkillCandidate>> RefreshAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Loads one bounded exact snapshot after revalidation.</summary>
    Task<ClaudeSkillSnapshot> ActivateAsync(
        ClaudeSkillCandidate candidate,
        CancellationToken cancellationToken = default);
}
