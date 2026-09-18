namespace Threadsmith.Tools.PullRequests;

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Retrieves provider-authoritative PR evidence through ordinary governed tool execution.</summary>
public sealed class PrFetchTool : Tool<PrFetchInput, PrFetchOutput>
{
    private readonly PrFetchOptions _options;
    private readonly IReadOnlyDictionary<string, IPullRequestProvider> _providers;

    /// <summary>Initializes a new instance of the <see cref="PrFetchTool"/> class with compiled adapters and trusted accounts.</summary>
    public PrFetchTool(IEnumerable<IPullRequestProvider> providers, PrFetchOptions options, IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _providers = providers.ToDictionary(provider => provider.Type, StringComparer.OrdinalIgnoreCase);
        foreach (var account in options.Providers.Values)
        {
            if (!_providers.TryGetValue(account.Type, out var provider))
            {
                throw new InvalidOperationException("A configured PR provider type is not compiled into this host.");
            }

            provider.ValidateConfiguration(account);
        }

        _options = options with
        {
            Providers = options.Providers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    UrlPatterns = pair.Value.UrlPatterns.Count > 0 ? pair.Value.UrlPatterns : _providers[pair.Value.Type].DefaultUrlPatterns,
                },
                StringComparer.OrdinalIgnoreCase),
        };
        var descriptions = _options.Providers.Where(pair => pair.Value.Enabled)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key} ({pair.Value.Type}; {_providers[pair.Value.Type].WebHost})");
        Definition = ToolDefinitionFactory.Create<PrFetchInput, PrFetchOutput>(
            "pr_fetch",
            prompts.Render(PromptFileNames.ToolPrFetchDescription, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Providers"] = string.Join(", ", descriptions),
            }),
            ToolCategory.ExternalSearch,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            options.TimeoutSeconds == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(options.TimeoutSeconds),
            256 * 1024) with
        {
            DisplayName = "Fetch Pull Request",
            EnabledByDefault = false,
            RequiresOutboundConsent = true,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<PrFetchOutput>> ExecuteAsync(PrFetchInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var (providerId, account, provider, target) = ResolveProvider(input);
        var scope = context.Invocation.OperationScope
            ?? throw new InvalidOperationException("PR fetching requires an active tool operation scope.");
        var cache = scope.GetOrCreate(this, () => new PrFetchCache(_options, scope.CancellationToken));
        var invocation = context.Invocation;
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            context.SessionId,
            invocation.RepositoryPath,
            invocation.TrustLevel,
            invocation.Sensitivity,
            Provider = providerId.ToUpperInvariant(),
            target.Url,
            input.Kind,
            Hosts = invocation.AllowedNetworkHosts.Order(StringComparer.OrdinalIgnoreCase),
            ApprovedRoots = invocation.ApprovedRoots.Order(StringComparer.OrdinalIgnoreCase),
            ProhibitedPaths = invocation.ProhibitedPaths.Order(StringComparer.OrdinalIgnoreCase),
        })));
        var result = Confine(
            await cache.ReadAsync(
                key,
                providerId,
                input,
                target,
                provider,
                account,
                IsPathScopeRestricted(invocation) ? file => IsAllowed(file, invocation) : null,
                cancellationToken),
            invocation);
        return new ToolExecution<PrFetchOutput>(
            result,
            [new ToolProvenanceSource("pull-request-untrusted", target.Url, $"snapshot={result.SnapshotId:N};source={result.Metadata.SourceCommit};destination={result.Metadata.DestinationCommit}")],
            IsTruncated: false,
            TransientActivityDetail: FormatCompletedActivityDetail(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(PrFetchInput input)
    {
        if ((input.Refresh && input.Cursor is not null) || input.Cursor?.Length > 128)
        {
            throw new ArgumentException("Refresh cannot accompany a cursor; use only the cursor returned by pr_fetch.");
        }

        ResolveProvider(input);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetSecretReferences(PrFetchInput input)
        => ResolveProvider(input).Account.Authentication.SecretReference is { } reference ? [reference] : [];

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(PrFetchInput input)
        => [ResolveProvider(input).Provider.ApiHost];

    /// <inheritdoc />
    protected override string? DescribeActivity(PrFetchInput input)
    {
        var resolved = ResolveProvider(input);
        return FormatStartedActivityDetail(resolved.Id, resolved.Target.Url, input);
    }

    private static string FormatStartedActivityDetail(string providerId, string url, PrFetchInput input)
    {
        var pageState = input.Cursor is null ? "first page" : "continuation";
        var refreshState = input.Refresh ? " · refresh" : string.Empty;
        return $"{providerId} · {FormatKind(input.Kind)} · {pageState}{refreshState} · {url}";
    }

    private static string FormatCompletedActivityDetail(PrFetchOutput result)
    {
        var pageState = result.IsContinuation ? "continuation" : "first page";
        var cursorState = result.Cursor is null ? "no cursor" : "cursor returned";
        var acquisitionState = result.AcquisitionComplete ? "acquisition complete" : "acquisition pending";
        var deliveryState = result.DeliveryComplete ? "delivery complete" : "delivery pending";
        return $"{result.Provider} · {FormatKind(result.Kind)} · {result.Page.Kind} · {pageState} · {cursorState} · {acquisitionState} · {deliveryState} · {result.Metadata.Url}";
    }

    private static string FormatKind(PrFetchKind kind) => kind.ToString().ToLowerInvariant();

    private static PrFetchOutput Confine(PrFetchOutput result, ToolInvocationContext context)
    {
        if (!IsPathScopeRestricted(context) || result.Page.Files.Count == 0)
        {
            return result;
        }

        var files = result.Page.Files.Where(file => IsAllowed(file, context)).ToArray();
        if (files.Length == result.Page.Files.Count)
        {
            return result;
        }

        return result with
        {
            Page = result.Page with
            {
                Files = files,
                Limitations =
                [
                    .. result.Page.Limitations,
                    "Some PR file entries were omitted because they are outside the caller's approved repository path scope.",
                ],
            },
        };
    }

    private static bool IsAllowed(PullRequestFile file, ToolInvocationContext context)
    {
        return IsAllowed(file.Path, context)
            && (file.PreviousPath is null || IsAllowed(file.PreviousPath, context));
    }

    private static bool IsAllowed(string path, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(path, context, inspectFileSystem: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPathScopeRestricted(ToolInvocationContext context)
    {
        if (context.ProhibitedPaths.Count > 0)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        return !context.ApprovedRoots.Any(root =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot))
                .Equals(repositoryRoot, comparison));
    }

    private (string Id, PullRequestProviderOptions Account, IPullRequestProvider Provider, PullRequestTarget Target) ResolveProvider(PrFetchInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Url);
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Provide an absolute HTTPS pull request URL.");
        }

        var candidates = _options.Providers.Where(pair => pair.Value.Enabled
            && (input.Provider is not null
                ? pair.Key.Equals(input.Provider, StringComparison.OrdinalIgnoreCase)
                : _providers[pair.Value.Type].WebHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (Id: pair.Key, Account: pair.Value, Provider: _providers[pair.Value.Type], Target: _providers[pair.Value.Type].ParseUrl(input.Url)))
            .Where(candidate => input.Provider is not null || candidate.Account.UrlPatterns
                .Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, candidate.Target.Url, ignoreCase: true)))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new ToolArgumentValidationException("No enabled configured PR account matches this URL or provider. Configure a matching account or choose one from the tool description.");
        }

        if (candidates.Length > 1)
        {
            throw new ToolArgumentValidationException("Multiple configured PR accounts match this URL. Ask the user which account to use, then pass provider: " + string.Join(", ", candidates.Select(candidate => candidate.Id)) + ".");
        }

        return candidates[0];
    }
}
