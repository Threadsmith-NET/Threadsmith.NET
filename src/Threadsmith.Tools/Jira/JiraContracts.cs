namespace Threadsmith.Tools.Jira;

/// <summary>Identifies a configured Jira account and issue to read.</summary>
public sealed record JiraInput
{
    /// <summary>Operation discriminator. The initial contract accepts only read.</summary>
    public required string Kind { get; init; }

    /// <summary>Jira issue key or supported HTTPS browse URL.</summary>
    public required string Issue { get; init; }

    /// <summary>Optional configured account ID, required when selection is ambiguous.</summary>
    public string? Provider { get; init; }
}

/// <summary>Readable Jira issue description and its provider identity.</summary>
public sealed record JiraReadOutput
{
    /// <summary>Completed operation discriminator.</summary>
    public required string Kind { get; init; }

    /// <summary>Selected configured account ID.</summary>
    public required string Provider { get; init; }

    /// <summary>Normalized issue key supplied by the caller.</summary>
    public required string RequestedIssue { get; init; }

    /// <summary>Provider issue ID.</summary>
    public required string Id { get; init; }

    /// <summary>Current provider issue key.</summary>
    public required string Key { get; init; }

    /// <summary>Canonical browse URL on the configured Jira site.</summary>
    public required string Url { get; init; }

    /// <summary>Bounded issue summary for attribution.</summary>
    public required string Summary { get; init; }

    /// <summary>Provider-reported update time.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Host retrieval time.</summary>
    public DateTimeOffset RetrievedAt { get; init; }

    /// <summary>Readable plain-text issue description.</summary>
    public required string Body { get; init; }

    /// <summary>Stable readable-body format.</summary>
    public string BodyFormat { get; init; } = "plainText";

    /// <summary>Whether the description was absent or present.</summary>
    public required string BodyState { get; init; }

    /// <summary>Whether all supported textual description content was represented.</summary>
    public bool BodyComplete { get; init; }

    /// <summary>Bounded fixed-code description limitations.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Validated Jira issue input bound to one trusted account.</summary>
internal sealed record JiraResolvedIssue(
    string ProviderId,
    JiraProviderOptions Provider,
    string RequestedKey);

/// <summary>Provider-returned issue fields before host result framing.</summary>
internal sealed record JiraIssueData(
    string Id,
    string Key,
    string Summary,
    DateTimeOffset? UpdatedAt,
    string Body,
    string BodyState,
    bool BodyComplete,
    IReadOnlyList<string> Limitations,
    bool IsTruncated);

/// <summary>Plain-text ADF projection and explicit coverage state.</summary>
internal sealed record JiraDescriptionResult(
    string Body,
    string BodyState,
    bool BodyComplete,
    IReadOnlyList<string> Limitations,
    bool IsTruncated);
