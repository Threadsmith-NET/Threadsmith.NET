namespace Threadsmith.Tools.PullRequests;

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Retrieves provider-authoritative PR evidence through ordinary governed tool execution.</summary>
public sealed class PrFetchTool : Tool<PrFetchInput, PrFetchOutput>
{
    private const int MaximumModelOutputBytes = 256 * 1024;
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
            MaximumModelOutputBytes) with
        {
            DisplayName = "Fetch Pull Request",
            EnabledByDefault = false,
            RequiresOutboundConsent = true,
            SubagentAvailable = false,
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
        if (context.Invocation.ModelRemainingInputBudgetTokens is <= 1_024)
        {
            throw new ToolArgumentValidationException("The selected model has too little remaining context for PR evidence. Compact the conversation or start a fresh request before fetching this PR.");
        }

        var handoff = scope.GetOrCreate(PrEvidenceRegistry.ScopeKey, static () => new PrEvidenceRegistry());
        var deliveryBudgetBytes = ModelDeliveryBudgetBytes(context.Invocation);
        if (input.SnapshotId is { } snapshotId)
        {
            var captured = handoff.Snapshot(context.SessionId, context.RunId)
                .SingleOrDefault(item => item.SnapshotId == snapshotId
                    && item.Provider.Equals(providerId, StringComparison.Ordinal)
                    && item.Metadata.Url.Equals(target.Url, StringComparison.Ordinal)
                    && item.Kind == input.Kind)
                ?? throw new ToolArgumentValidationException("The requested PR snapshot is unavailable for this run, URL, and kind.");
            var maximumCharacters = ModelDeliveryMaximumCharacters(captured, deliveryBudgetBytes);
            var read = handoff.Read(
                context.SessionId,
                context.RunId,
                snapshotId,
                context.Invocation,
                input.StartLine ?? 1,
                input.EndLine,
                input.StartColumn ?? 1,
                maximumCharacters);
            var segment = CreateBoundedOutput(captured, read);
            EnsureFitsDeliveryBudget(segment, deliveryBudgetBytes);
            return new ToolExecution<PrFetchOutput>(
                segment,
                [new ToolProvenanceSource("pull-request-untrusted", target.Url, $"snapshot={snapshotId:N};source={captured.Metadata.SourceCommit};destination={captured.Metadata.DestinationCommit}")],
                IsTruncated: read.NextLine is not null,
                TransientActivityDetail: $"{providerId} · captured evidence · line {read.StartLine} · {target.Url}");
        }

        if (input.Refresh)
        {
            handoff.Clear(context.SessionId, context.RunId, providerId, target.Url);
        }

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
                PrEvidencePathScope.IsRestricted(invocation) ? file => PrEvidencePathScope.IsAllowed(file, invocation) : null,
                cancellationToken),
            invocation);
        handoff.Register(context.SessionId, context.RunId, result);
        var delivered = FitsModelOutput(result, deliveryBudgetBytes)
            ? result with { ChangedFileCount = result.Page.Files.Count, DiffCharacterCount = result.Page.Diff.Length }
            : CreateBoundedOutput(result);
        EnsureFitsDeliveryBudget(delivered, deliveryBudgetBytes);
        return new ToolExecution<PrFetchOutput>(
            delivered,
            [new ToolProvenanceSource("pull-request-untrusted", target.Url, $"snapshot={result.SnapshotId:N};source={result.Metadata.SourceCommit};destination={result.Metadata.DestinationCommit}")],
            IsTruncated: delivered.EvidenceReadRequired,
            TransientActivityDetail: FormatCompletedActivityDetail(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(PrFetchInput input)
    {
        ResolveProvider(input);
        if (input.SnapshotId is { } snapshotId)
        {
            if (snapshotId == Guid.Empty || input.Refresh)
            {
                throw new ToolArgumentValidationException("A captured-snapshot read needs a nonempty snapshot ID and cannot refresh.");
            }
        }
        else if (input.StartLine is not null || input.EndLine is not null || input.StartColumn is not null)
        {
            throw new ToolArgumentValidationException("Evidence line and column ranges require snapshotId.");
        }

        if (input.StartLine is < 1 || input.EndLine is < 1 || input.StartColumn is < 1
            || (input.StartLine is { } first && input.EndLine is { } last && last < first))
        {
            throw new ToolArgumentValidationException("Evidence lines and columns must be positive and ordered.");
        }
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
        var refreshState = input.Refresh ? " · refresh" : string.Empty;
        return $"{providerId} · {FormatKind(input.Kind)} · acquiring{refreshState} · {url}";
    }

    private static string FormatCompletedActivityDetail(PrFetchOutput result)
    {
        return $"{result.Provider} · {FormatKind(result.Kind)} · acquisition complete · {result.Page.Files.Count} files · {result.Metadata.Url}";
    }

    private static string FormatKind(PrFetchKind kind) => kind.ToString().ToLowerInvariant();

    private static int ModelDeliveryBudgetBytes(ToolInvocationContext context) =>
        (context.ModelRemainingInputBudgetTokens ?? context.ModelEffectiveInputBudgetTokens) is { } effective && effective > 0
            ? (int)Math.Min(MaximumModelOutputBytes, (long)effective * 3)
            : MaximumModelOutputBytes;

    private static int ModelDeliveryMaximumCharacters(PrFetchOutput captured, int deliveryBudgetBytes)
    {
        var emptyRead = new TextEvidenceReadResult(
            string.Empty,
            1,
            1,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(CreateBoundedOutput(captured, emptyRead)).Length;
        var maximumCharacters = TextEvidenceDocument.GetMaximumReadCharacters(deliveryBudgetBytes, envelopeBytes);
        if (maximumCharacters < 1)
        {
            throw new ToolArgumentValidationException("The selected model has too little remaining context for a PR evidence segment. Compact the conversation or start a fresh request before continuing this snapshot.");
        }

        return maximumCharacters;
    }

    private static void EnsureFitsDeliveryBudget(PrFetchOutput output, int deliveryBudgetBytes)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(output).Length > deliveryBudgetBytes)
        {
            throw new InvalidOperationException("The bounded PR evidence result exceeded its model-delivery byte allowance.");
        }
    }

    private static bool FitsModelOutput(PrFetchOutput result, int deliveryBudgetBytes)
    {
        long characters = result.Page.Diff.Length
            + result.Provider.Length
            + result.Metadata.Url.Length
            + result.Metadata.Repository.Length
            + result.Metadata.Number.Length
            + result.Metadata.Title.Length
            + result.Metadata.Description.Length
            + result.Metadata.State.Length
            + result.Metadata.SourceRepository.Length
            + result.Metadata.SourceCommit.Length
            + result.Metadata.DestinationRepository.Length
            + result.Metadata.DestinationCommit.Length
            + result.Metadata.Revision.Length;
        foreach (var file in result.Page.Files)
        {
            characters += file.Path.Length + (file.PreviousPath?.Length ?? 0) + file.Status.Length + (file.Limitation?.Length ?? 0);
        }

        foreach (var limitation in result.Page.Limitations)
        {
            characters += limitation.Length;
        }

        if (characters > 48_000)
        {
            return false;
        }

        return JsonSerializer.SerializeToUtf8Bytes(result).Length <= deliveryBudgetBytes;
    }

    private static PrFetchOutput CreateBoundedOutput(PrFetchOutput captured, TextEvidenceReadResult? read = null)
    {
        static string Shorten(string value) => value.Length <= 48 ? value : value[..48] + " [see captured evidence]";

        var metadata = captured.Metadata with
        {
            Url = Shorten(captured.Metadata.Url),
            Repository = Shorten(captured.Metadata.Repository),
            Number = Shorten(captured.Metadata.Number),
            Title = Shorten(captured.Metadata.Title),
            Description = Shorten(captured.Metadata.Description),
            State = Shorten(captured.Metadata.State),
            SourceRepository = Shorten(captured.Metadata.SourceRepository),
            SourceCommit = Shorten(captured.Metadata.SourceCommit),
            DestinationRepository = Shorten(captured.Metadata.DestinationRepository),
            DestinationCommit = Shorten(captured.Metadata.DestinationCommit),
            Revision = Shorten(captured.Metadata.Revision),
        };
        var limitation = read is null
            ? "Complete evidence is retained under snapshotId. Call pr_fetch with the same url and kind plus snapshotId and optional startLine/endLine/startColumn to read bounded portions; start at line 1."
            : "This is a bounded read of a completed snapshot. Continue with nextLine and nextColumn when present.";
        return captured with
        {
            Metadata = metadata,
            Page = new PullRequestPage(read is null ? "manifest" : "evidence", [], read?.Content ?? string.Empty, [limitation]),
            CacheHit = read is not null || captured.CacheHit,
            EvidenceReadRequired = true,
            ChangedFileCount = captured.Page.Files.Count,
            DiffCharacterCount = captured.Page.Diff.Length,
            TotalEvidenceLines = read?.TotalLines,
            NextLine = read?.NextLine,
            NextColumn = read?.NextColumn,
        };
    }

    private static PrFetchOutput Confine(PrFetchOutput result, ToolInvocationContext context)
    {
        if (!PrEvidencePathScope.IsRestricted(context) || result.Page.Files.Count == 0)
        {
            return result;
        }

        var files = result.Page.Files.Where(file => PrEvidencePathScope.IsAllowed(file, context)).ToArray();
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
