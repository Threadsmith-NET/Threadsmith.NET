namespace Threadsmith.Models.Anthropic;

/// <summary>Complete-snapshot discovery bounded across pages by one deadline.</summary>
public sealed class AnthropicModelDiscoveryService
{
    private readonly AnthropicResourceLimits _limits;
    private readonly IAnthropicModelDiscoveryClient _client;
    private readonly TimeSpan _deadline;

    /// <summary>Initializes a new instance of the <see cref="AnthropicModelDiscoveryService"/> class with a bounded total deadline.</summary>
    public AnthropicModelDiscoveryService(IAnthropicModelDiscoveryClient client, TimeSpan? deadline = null, AnthropicResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _limits = limits ?? new();
        _limits.Validate();
        _client = client;
        _deadline = deadline ?? TimeSpan.FromMilliseconds(_limits.DiscoveryTimeoutMilliseconds);
        if (_deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }
    }

    /// <summary>Returns every detached page or a safe failure; no partial result is published.</summary>
    public async Task<IReadOnlyList<AnthropicDiscoveredModel>> DiscoverAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_deadline);
        try
        {
            var models = new List<AnthropicDiscoveredModel>();
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            long bytes = 0;
            for (var pageNumber = 0; pageNumber < _limits.MaximumDiscoveryPages; pageNumber++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var page = await _client.ListAsync(after, 100, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (page.Models.Count > 100 || page.ResponseBytes < 0 || page.ResponseBytes > _limits.MaximumMetadataBytes)
                {
                    throw new AnthropicDiscoveryException("Anthropic discovery exceeded a page resource limit.");
                }

                bytes = checked(bytes + page.ResponseBytes);
                if (bytes > _limits.MaximumMetadataBytes || (long)models.Count + page.Models.Count > _limits.MaximumDiscoveredModels)
                {
                    throw new AnthropicDiscoveryException("Anthropic discovery exceeded its aggregate resource limit.");
                }

                foreach (var model in page.Models)
                {
                    if (!AnthropicCatalogHydrator.IsSafeMetadata(model) || !identifiers.Add(model.ModelId))
                    {
                        throw new AnthropicDiscoveryException("Anthropic discovery returned unsafe or duplicate model metadata.");
                    }

                    models.Add(model);
                }

                if (!page.HasMore)
                {
                    return models.AsReadOnly();
                }

                if (page.LastId is not { } lastId || !AnthropicCatalogHydrator.IsSafeIdentity(lastId) || page.Models.Count == 0
                    || !string.Equals(page.LastId, page.Models[^1].ModelId, StringComparison.Ordinal)
                    || !cursors.Add(lastId))
                {
                    throw new AnthropicDiscoveryException("Anthropic discovery returned an invalid or repeated cursor.");
                }

                after = page.LastId;
            }

            throw new AnthropicDiscoveryException("Anthropic discovery exceeded its page limit.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AnthropicDiscoveryException("Anthropic discovery exceeded its total deadline.", transient: true);
        }
    }
}
