namespace Threadsmith.Tools.PullRequests;

using System.Text.Json.Serialization;

/// <summary>Provider evidence requested by one PR fetch operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrFetchKind>))]
public enum PrFetchKind
{
    /// <summary>PR metadata and the complete changed-file inventory without patch content.</summary>
    [JsonStringEnumMemberName("inventory")]
    Inventory,

    /// <summary>PR metadata, the complete changed-file inventory and provider diff content.</summary>
    [JsonStringEnumMemberName("diff")]
    Diff,
}

/// <summary>Identifies a configured account and a provider-hosted pull request.</summary>
public sealed record PrFetchInput
{
    /// <summary>Optional configured account ID; inferred from the URL when exactly one enabled account matches.</summary>
    public string? Provider { get; init; }

    /// <summary>Pull request web URL.</summary>
    public required string Url { get; init; }

    /// <summary>Evidence scope: inventory for file identities/statuses, or diff for patch content and review.</summary>
    public required PrFetchKind Kind { get; init; }

    /// <summary>Explicitly replaces the captured PR.</summary>
    public bool Refresh { get; init; }

    /// <summary>Completed operation-scoped snapshot to read without another provider request.</summary>
    public Guid? SnapshotId { get; init; }

    /// <summary>One-based evidence line for a captured-snapshot read.</summary>
    public int? StartLine { get; init; }

    /// <summary>Optional inclusive final evidence line.</summary>
    public int? EndLine { get; init; }

    /// <summary>One-based column for continuing a long evidence line.</summary>
    public int? StartColumn { get; init; }
}

/// <summary>Provider-validated web and API identity; no model-supplied API endpoint.</summary>
public sealed record PullRequestTarget(string Url, string ApiPath, string Repository, string Number);

/// <summary>Captured PR identity and commit provenance. DestinationCommit is not a merge base.</summary>
public sealed record PullRequestMetadata(
    string Url,
    string Repository,
    string Number,
    string Title,
    string Description,
    string State,
    string SourceRepository,
    string SourceCommit,
    string DestinationRepository,
    string DestinationCommit,
    string Revision,
    int? ExpectedFiles);

/// <summary>One provider-reported changed file, independent of local checkout contents.</summary>
public sealed record PullRequestFile(string Path, string? PreviousPath, string Status, string? Limitation = null);

/// <summary>A bounded portion of untrusted PR evidence. Completion is an explicit terminal page.</summary>
public sealed record PullRequestPage(string Kind, IReadOnlyList<PullRequestFile> Files, string Diff, IReadOnlyList<string> Limitations);

/// <summary>Complete PR evidence after acquisition consistency checks.</summary>
public sealed record PrFetchOutput(
    string Provider,
    PrFetchKind Kind,
    Guid SnapshotId,
    DateTimeOffset CapturedAt,
    PullRequestMetadata Metadata,
    PullRequestPage Page,
    bool CacheHit,
    bool AcquisitionComplete)
{
    /// <summary>Whether the complete captured evidence requires bounded follow-up reads.</summary>
    public bool EvidenceReadRequired { get; init; }

    /// <summary>Total changed files in the completed snapshot, including omitted manifest entries.</summary>
    public int ChangedFileCount { get; init; }

    /// <summary>Number of characters in the captured provider diff.</summary>
    public int DiffCharacterCount { get; init; }

    /// <summary>Total line count of the line-addressable evidence document, when read.</summary>
    public int? TotalEvidenceLines { get; init; }

    /// <summary>Next one-based evidence line when a bounded read has more content.</summary>
    public int? NextLine { get; init; }

    /// <summary>Next one-based column when a bounded read has more content.</summary>
    public int? NextColumn { get; init; }
}

/// <summary>Compiled provider contract; all returned values are host-owned evidence DTOs.</summary>
public interface IPullRequestProvider
{
    /// <summary>Configuration discriminator for this adapter.</summary>
    string Type { get; }

    /// <summary>Recognized web host for descriptions and input validation.</summary>
    string WebHost { get; }

    /// <summary>Default URL globs exposed by the adapter for automatic account selection.</summary>
    IReadOnlyList<string> DefaultUrlPatterns { get; }

    /// <summary>Credential destination declared to ordinary tool policy.</summary>
    string ApiHost { get; }

    /// <summary>Validates authentication settings without resolving secrets.</summary>
    void ValidateConfiguration(PullRequestProviderOptions options);

    /// <summary>Validates a PR URL and constructs its canonical provider identity.</summary>
    PullRequestTarget ParseUrl(string url);

    /// <summary>Retrieves metadata used to detect a change during acquisition.</summary>
    Task<PullRequestMetadata> GetMetadataAsync(PullRequestTarget target, PullRequestProviderOptions options, CancellationToken cancellationToken = default);

    /// <summary>Streams changed-file and requested diff pages, preserving provider PR comparison semantics.</summary>
    IAsyncEnumerable<PullRequestPage> ReadPagesAsync(PullRequestTarget target, PullRequestProviderOptions options, PrFetchKind kind, CancellationToken cancellationToken = default);
}
