namespace Threadsmith.Tools.Jira;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Reads Jira issue descriptions through ordinary governed tool execution.</summary>
public sealed class JiraTool : Tool<JiraInput, JiraReadOutput>, IPostSanitizationToolOutputBoundary
{
    private const int DefaultMaximumOutputBytes = 256 * 1024;

    private readonly JiraCloudClient _client;
    private readonly JiraOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="JiraTool"/> class.</summary>
    public JiraTool(
        JiraCloudClient client,
        JiraOptions options,
        IPromptLoader prompts,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        JiraOptions.Validate(options);
        _client = client;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var providers = options.Providers
            .Where(pair => pair.Value.Enabled)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} ({JiraAddress.ValidateSiteUrl(pair.Value.SiteUrl).IdnHost})");
        var providerDescription = string.Join(", ", providers);
        if (Encoding.UTF8.GetByteCount(providerDescription) > JiraOptions.MaximumProviderDescriptionBytes)
        {
            throw new InvalidOperationException("The Jira provider description exceeds its aggregate bound.");
        }

        Definition = ToolDefinitionFactory.WithStringEnum(
            ToolDefinitionFactory.Create<JiraInput, JiraReadOutput>(
                "jira",
                prompts.Render(PromptFileNames.ToolJiraDescription, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Providers"] = providerDescription,
                }),
                ToolCategory.ExternalSearch,
                RepositoryTrustLevel.UntrustedInspection,
                ApprovalLevel.None,
                ToolSideEffect.ReadOnly,
                TimeSpan.FromSeconds(options.TimeoutSeconds),
                DefaultMaximumOutputBytes) with
            {
                DisplayName = "Jira",
                EnabledByDefault = false,
                RequiresOutboundConsent = true,
                ReadOnlySubagentNetworkAvailable = true,
            },
            "kind",
            "read",
            "read");
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<JiraReadOutput>> ExecuteAsync(
        JiraInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(input);
        var issue = await _client.ReadIssueAsync(
            resolved.ProviderId,
            resolved.Provider,
            resolved.RequestedKey,
            cancellationToken);
        var site = JiraAddress.ValidateSiteUrl(resolved.Provider.SiteUrl);
        var output = new JiraReadOutput
        {
            Kind = "read",
            Provider = resolved.ProviderId,
            RequestedIssue = resolved.RequestedKey,
            Id = issue.Id,
            Key = issue.Key,
            Url = new Uri(site, $"browse/{issue.Key}").AbsoluteUri,
            Summary = issue.Summary,
            UpdatedAt = issue.UpdatedAt,
            RetrievedAt = _timeProvider.GetUtcNow(),
            Body = issue.Body,
            BodyState = issue.BodyState,
            BodyComplete = issue.BodyComplete,
            Limitations = issue.Limitations,
        };
        var bounded = BoundOutput(
            output,
            context.MaximumOutputBytes ?? Definition.MaximumOutputBytes);
        var completion = bounded.Output.BodyState == "absent"
            ? "no description"
            : bounded.Output.BodyComplete ? "description retrieved" : "description retrieved with limitations";
        return new ToolExecution<JiraReadOutput>(
            bounded.Output,
            [new ToolProvenanceSource(
                "jira-issue-untrusted",
                bounded.Output.Url,
                $"id={bounded.Output.Id};updated={bounded.Output.UpdatedAt:O};retrieved={bounded.Output.RetrievedAt:O}")],
            issue.IsTruncated || bounded.WasTruncated,
            TransientActivityDetail: $"read · {FormatIdentity(bounded.Output.RequestedIssue, bounded.Output.Key)} · {bounded.Output.Provider} · {completion}");
    }

    /// <inheritdoc />
    PostSanitizationToolOutput IPostSanitizationToolOutputBoundary.BoundSanitizedOutput(
        string resultJson,
        string? modelResultContent,
        ToolInvocationContext context,
        int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = JsonSerializer.Deserialize<JiraReadOutput>(resultJson)
            ?? throw new InvalidOperationException("The sanitized Jira result could not be deserialized.");
        var bounded = BoundOutput(output, maximumOutputBytes);
        return new PostSanitizationToolOutput(
            JsonSerializer.Serialize(bounded.Output),
            modelResultContent,
            bounded.WasTruncated);
    }

    /// <summary>Validates the bounded Jira project-key and issue-number grammar.</summary>
    internal static bool IsValidIssueKey(string value)
    {
        if (value.Length is < 3 or > 128)
        {
            return false;
        }

        var separator = value.LastIndexOf('-');
        if (separator < 1 || separator == value.Length - 1
            || !char.IsAsciiLetter(value[0])
            || value[1..separator].Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_')
            || value[(separator + 1)..].Any(character => !char.IsAsciiDigit(character))
            || value[separator + 1] == '0')
        {
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    protected override void ValidateInput(JiraInput input) => _ = Resolve(input);

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetSecretReferences(JiraInput input)
        => [Resolve(input).Provider.Authentication.SecretReference];

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(JiraInput input)
    {
        var provider = Resolve(input).Provider;
        return provider.EndpointMode == "scopedGateway"
            ? ["api.atlassian.com"]
            : [JiraAddress.ValidateSiteUrl(provider.SiteUrl).IdnHost];
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(JiraInput input)
    {
        var resolved = Resolve(input);
        return $"read · {resolved.RequestedKey} · {resolved.ProviderId}";
    }

    private static string FormatIdentity(string requested, string returned)
        => requested.Equals(returned, StringComparison.Ordinal)
            ? returned
            : $"{returned} (requested {requested})";

    private static OutputBoundaryResult BoundOutput(JiraReadOutput output, int maximumOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputBytes);
        if (JsonSerializer.SerializeToUtf8Bytes(output).Length <= maximumOutputBytes)
        {
            return new OutputBoundaryResult(output, false);
        }

        var limitations = new SortedSet<string>(output.Limitations, StringComparer.Ordinal)
        {
            "tool-output-limit",
        };
        var bounded = output with
        {
            Body = string.Empty,
            Limitations = BoundLimitations(limitations),
        };
        if (JsonSerializer.SerializeToUtf8Bytes(bounded).Length > maximumOutputBytes)
        {
            limitations.Add("summary-truncated");
            bounded = bounded with
            {
                Summary = string.Empty,
                Limitations = BoundLimitations(limitations),
            };
        }

        var emptySize = JsonSerializer.SerializeToUtf8Bytes(bounded).Length;
        if (emptySize > maximumOutputBytes)
        {
            throw new ToolExecutionException(
                "The Jira identity and coverage envelope exceeded the configured tool output bound.",
                ToolErrorClassification.OutputLimitExceeded);
        }

        var candidateBytes = Math.Max(0, maximumOutputBytes - emptySize);
        var body = JiraTextBounds.Utf8Prefix(output.Body, candidateBytes, out var bodyTruncated);
        bounded = bounded with
        {
            Body = body,
            BodyComplete = !bodyTruncated && output.BodyComplete,
        };
        while (body.Length > 0 && JsonSerializer.SerializeToUtf8Bytes(bounded).Length > maximumOutputBytes)
        {
            candidateBytes = candidateBytes * 3 / 4;
            body = JiraTextBounds.Utf8Prefix(output.Body, candidateBytes, out bodyTruncated);
            bounded = bounded with
            {
                Body = body,
                BodyComplete = !bodyTruncated && output.BodyComplete,
            };
        }

        if (JsonSerializer.SerializeToUtf8Bytes(bounded).Length > maximumOutputBytes)
        {
            throw new ToolExecutionException(
                "The Jira result exceeded the configured tool output bound.",
                ToolErrorClassification.OutputLimitExceeded);
        }

        return new OutputBoundaryResult(bounded, true);
    }

    private static IReadOnlyList<string> BoundLimitations(IEnumerable<string> limitations)
    {
        var ordered = limitations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return ordered.Length <= 16
            ? ordered
            : [.. ordered.Take(15), "additional-limitations-omitted"];
    }

    private JiraResolvedIssue Resolve(JiraInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(input.Kind, "read", StringComparison.Ordinal))
        {
            throw new ToolArgumentValidationException("Jira kind must be the string \"read\".");
        }

        if (string.IsNullOrWhiteSpace(input.Issue) || input.Issue.Length > 2048)
        {
            throw new ToolArgumentValidationException("Provide a bounded Jira issue key or HTTPS /browse/{key} URL.");
        }

        var parsed = ParseIssue(input.Issue);
        var candidates = _options.Providers
            .Where(pair => pair.Value.Enabled
                && (input.Provider is null || pair.Key.Equals(input.Provider, StringComparison.OrdinalIgnoreCase))
                && (parsed.Host is null || MatchesBrowseHost(pair.Value, parsed.Host)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new ToolArgumentValidationException(
                "No enabled configured Jira account matches this issue. Configure a matching account or pass its provider ID.");
        }

        if (candidates.Length > 1)
        {
            var ids = string.Join(", ", candidates.Take(3).Select(pair => pair.Key));
            var remainder = candidates.Length > 3 ? $" (+{candidates.Length - 3} more)" : string.Empty;
            throw new ToolArgumentValidationException(
                $"Pass provider to choose a Jira account. Matching providers: {ids}{remainder}.");
        }

        return new JiraResolvedIssue(candidates[0].Key, candidates[0].Value, parsed.Key);
    }

    private static (string Key, string? Host) ParseIssue(string issue)
    {
        var normalizedKey = issue.ToUpperInvariant();
        if (IsValidIssueKey(normalizedKey))
        {
            return (normalizedKey, null);
        }

        if (!Uri.TryCreate(issue, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath.Contains('\\', StringComparison.Ordinal)
            || uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolArgumentValidationException("Provide an HTTPS Jira /browse/{key} URL without credentials.");
        }

        var path = uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsolutePath[..^1]
            : uri.AbsolutePath;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("browse", StringComparison.Ordinal)
            || !IsValidIssueKey(parts[1].ToUpperInvariant()))
        {
            throw new ToolArgumentValidationException(
                "The Jira URL must use the supported /browse/{key} route; otherwise provide the issue key.");
        }

        return (parts[1].ToUpperInvariant(), uri.IdnHost);
    }

    private static bool MatchesBrowseHost(JiraProviderOptions provider, string host)
    {
        if (JiraAddress.ValidateSiteUrl(provider.SiteUrl).IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return provider.BrowseHostAliases.Any(alias =>
            JiraAddress.ValidateBrowseHost(alias).Equals(host, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record OutputBoundaryResult(JiraReadOutput Output, bool WasTruncated);
}
