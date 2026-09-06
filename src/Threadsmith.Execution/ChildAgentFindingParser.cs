namespace Threadsmith.Execution;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Parses and validates the bounded model-authored portion of an Explorer result.</summary>
internal sealed class ChildAgentFindingParser
{
    private const int MaximumCategoryCharacters = 128;
    private const int MaximumDetailCharacters = 4_096;
    private const int MaximumLocationCharacters = 1_024;
    private const int MaximumMetadataItems = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly DelegateAgentsOptions _options;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentFindingParser"/> class.</summary>
    public ChildAgentFindingParser(
        DelegateAgentsOptions options,
        IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _options = options;
        _sanitizer = sanitizer;
    }

    /// <summary>Creates a host-identified finding set from exact response JSON.</summary>
    public AgentFindingSet Parse(
        string json,
        DelegationPlan plan,
        AgentAssignment assignment,
        ToolInvocationContext childContext)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(childContext);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The child finding response was empty.");
        }

        ChildFindingResponse response;
        try
        {
            response = JsonSerializer.Deserialize<ChildFindingResponse>(json, JsonOptions)
                ?? throw new InvalidDataException("The child finding response was empty.");
        }
        catch (JsonException exception)
        {
            var path = string.IsNullOrWhiteSpace(exception.Path)
                ? string.Empty
                : $" at {exception.Path}";
            throw new InvalidDataException(
                "The child response does not match exact agent-findings/1 JSON"
                    + path
                    + ". unresolvedQuestions and coverageNotes must contain strings, not objects.",
                exception);
        }

        var summary = SanitizeRequired(
            response.Summary,
            "summary",
            _options.MaximumSummaryCharacters);
        if (response.Findings is null)
        {
            throw new InvalidDataException("The child findings array is required.");
        }

        var pathPolicy = CreatePathPolicy(childContext);
        AgentFinding[] findings =
        [
            .. response.Findings.Select(finding => ParseFinding(finding, pathPolicy)),
        ];
        return new AgentFindingSet
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Generation = plan.Provenance.Generation,
            Summary = summary,
            Findings = findings,
            UnresolvedQuestions = SanitizeList(
                response.UnresolvedQuestions,
                "unresolvedQuestions"),
            CoverageNotes = SanitizeList(response.CoverageNotes, "coverageNotes"),
        };
    }

    private AgentFinding ParseFinding(
        ChildFinding response,
        FindingPathPolicy pathPolicy)
    {
        if (response is null)
        {
            throw new InvalidDataException("A child finding cannot be null.");
        }

        if (response.Confidence is null or < 0 or > 1)
        {
            throw new InvalidDataException("Child finding confidence must be between zero and one.");
        }

        if (response.EvidenceIds is null
            || response.EvidenceIds.Count is < 1 or > MaximumMetadataItems)
        {
            throw new InvalidDataException("Each child finding requires bounded evidence citations.");
        }

        EvidenceId[] evidenceIds =
        [
            .. response.EvidenceIds.Select(value => Guid.TryParse(value, out var parsed)
                && parsed != Guid.Empty
                    ? new EvidenceId(parsed)
                    : throw new InvalidDataException("A child evidence citation is invalid.")),
        ];
        return new AgentFinding
        {
            FindingId = Guid.NewGuid(),
            Category = ParseCategory(response.Category),
            Summary = SanitizeRequired(response.Summary, "finding summary", MaximumDetailCharacters),
            EvidenceIds = evidenceIds,
            Locations = ValidateLocations(response.Locations, pathPolicy),
            Symbols = SanitizeList(response.Symbols, "symbols"),
            Confidence = response.Confidence.Value,
            Uncertainty = SanitizeOptional(response.Uncertainty, "uncertainty"),
            Risk = SanitizeOptional(response.Risk, "risk"),
            Recommendation = SanitizeOptional(response.Recommendation, "recommendation"),
        };
    }

    private IReadOnlyList<string> ValidateLocations(
        IReadOnlyList<string>? values,
        FindingPathPolicy policy)
    {
        var locations = SanitizeList(values, "locations", MaximumLocationCharacters);
        foreach (var location in locations)
        {
            if (Path.IsPathRooted(location)
                || location.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or ".."
                        || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                || RepositoryPathPolicy.IsProhibited(location, policy.ProhibitedPaths))
            {
                throw new UnauthorizedAccessException("A child finding location is outside policy.");
            }

            var fullPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(location, policy.RepositoryRoot));
            if (!IsWithin(fullPath, policy.RepositoryRoot, policy.Comparison)
                || !policy.ApprovedRoots.Any(root => IsWithin(
                    fullPath,
                    root,
                    policy.Comparison)))
            {
                throw new UnauthorizedAccessException("A child finding location is outside policy.");
            }
        }

        return locations.Select(location => location.Replace('\\', '/')).ToArray();
    }

    private static FindingPathPolicy CreatePathPolicy(ToolInvocationContext context)
    {
        var repositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(context.RepositoryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string[] approvedRoots =
        [
            .. context.ApprovedRoots.DefaultIfEmpty(".")
                .Select(root => Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(root, repositoryRoot))),
        ];
        if (approvedRoots.Any(root => !IsWithin(root, repositoryRoot, comparison)))
        {
            throw new UnauthorizedAccessException("A child approved root is outside policy.");
        }

        return new FindingPathPolicy(
            repositoryRoot,
            approvedRoots,
            context.ProhibitedPaths,
            comparison);
    }

    private string ParseCategory(string? value)
    {
        var category = SanitizeRequired(value, "category", MaximumCategoryCharacters);
        return category is "behavior" or "risk" or "test" or "architecture" or "other"
            ? category
            : throw new InvalidDataException("Child finding category is outside the supported set.");
    }

    private IReadOnlyList<string> SanitizeList(
        IReadOnlyList<string>? values,
        string name,
        int maximumCharacters = MaximumDetailCharacters)
    {
        if (values is null || values.Count > MaximumMetadataItems)
        {
            throw new InvalidDataException($"Child {name} must be a bounded array.");
        }

        return values.Select(value => SanitizeRequired(value, name, maximumCharacters)).ToArray();
    }

    private string SanitizeRequired(string? value, string name, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
        {
            throw new InvalidDataException($"Child {name} is missing or exceeds its host bound.");
        }

        var sanitized = _sanitizer.Sanitize(value).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? throw new InvalidDataException($"Child {name} is empty after sanitization.")
            : sanitized;
    }

    private string? SanitizeOptional(string? value, string name)
    {
        return value is null ? null : SanitizeRequired(value, name, MaximumDetailCharacters);
    }

    private static bool IsWithin(string candidate, string root, StringComparison comparison)
    {
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record FindingPathPolicy(
        string RepositoryRoot,
        IReadOnlyList<string> ApprovedRoots,
        IReadOnlyList<string> ProhibitedPaths,
        StringComparison Comparison);

    private sealed record ChildFindingResponse
    {
        [JsonPropertyName("summary")]
        public required string Summary { get; init; }

        [JsonPropertyName("findings")]
        public required IReadOnlyList<ChildFinding> Findings { get; init; }

        [JsonPropertyName("unresolvedQuestions")]
        public required IReadOnlyList<string> UnresolvedQuestions { get; init; }

        [JsonPropertyName("coverageNotes")]
        public required IReadOnlyList<string> CoverageNotes { get; init; }
    }

    private sealed record ChildFinding
    {
        [JsonPropertyName("category")]
        public required string Category { get; init; }

        [JsonPropertyName("summary")]
        public required string Summary { get; init; }

        [JsonPropertyName("evidenceIds")]
        public required IReadOnlyList<string> EvidenceIds { get; init; }

        [JsonPropertyName("locations")]
        public required IReadOnlyList<string> Locations { get; init; }

        [JsonPropertyName("symbols")]
        public required IReadOnlyList<string> Symbols { get; init; }

        [JsonPropertyName("confidence")]
        public required double? Confidence { get; init; }

        [JsonPropertyName("uncertainty")]
        public string? Uncertainty { get; init; }

        [JsonPropertyName("risk")]
        public string? Risk { get; init; }

        [JsonPropertyName("recommendation")]
        public string? Recommendation { get; init; }
    }
}
