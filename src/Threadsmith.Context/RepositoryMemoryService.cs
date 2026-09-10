namespace Threadsmith.Context;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Shared explicit-operation policy; inference always finishes before transactional persistence.</summary>
public sealed class RepositoryMemoryService : IManagedRepositoryMemoryService
{
    private const int MaximumTextCharacters = 2_000;
    private readonly ILogger<RepositoryMemoryService> _logger;
    private readonly ITextEmbeddingGenerator _generator;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IManagedRepositoryMemoryStore _store;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryService"/> class.</summary>
    public RepositoryMemoryService(
        IManagedRepositoryMemoryStore store,
        ITextEmbeddingGenerator generator,
        IOutputSanitizer sanitizer,
        ILogger<RepositoryMemoryService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _store = store;
        _generator = generator;
        _sanitizer = sanitizer;
        _logger = logger ?? NullLogger<RepositoryMemoryService>.Instance;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryOperationResult> ExecuteAsync(
        RepositoryMemoryOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryIdentity);
        request.Options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateArguments(request);
        if (request.Action == "remove" && request.Id is { } removeId)
        {
            var removed = await _store.RemoveAsync(request.RepositoryIdentity, removeId, cancellationToken);
            return new RepositoryMemoryOperationResult(removed is null ? "absent" : "removed", removeId, removed, [], []);
        }

        var snapshot = await _store.GetSnapshotAsync(request.RepositoryIdentity, [], cancellationToken);
        if (request.Action == "list")
        {
            return new RepositoryMemoryOperationResult("listed", null, null, snapshot.Entries, []);
        }

        var text = Normalize(_sanitizer.Sanitize(request.Text ?? string.Empty));
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > MaximumTextCharacters)
        {
            throw new ArgumentException("Memory text must fit 2,000 characters after sanitization. Shorten the memory and retry.");
        }

        var existing = request.Id is { } id ? snapshot.Entries.FirstOrDefault(entry => entry.Id == id) : null;
        if (request.Action == "update" && existing is null)
        {
            return new RepositoryMemoryOperationResult("notFound", request.Id, null, [], []);
        }

        var memoryType = request.MemoryType
            ?? existing?.MemoryType
            ?? RepositoryMemoryType.Situational;
        var duplicate = snapshot.Entries.FirstOrDefault(entry => string.Equals(entry.Text, text, StringComparison.Ordinal));
        if (duplicate is not null
            && (duplicate.Id != request.Id || duplicate.MemoryType == memoryType))
        {
            return new RepositoryMemoryOperationResult(
                duplicate.Id == request.Id ? "unchanged" : "duplicate", duplicate.Id, duplicate, [], []);
        }

        var write = new RepositoryMemoryWrite
        {
            Text = text,
            Origin = request.Origin,
            Sensitivity = request.Sensitivity,
            SourceSessionId = request.SourceSessionId,
            SourceRunId = request.SourceRunId,
            SourceInvocationId = request.SourceInvocationId,
            MemoryType = memoryType,
        };
        RepositoryMemoryWriteResult result;
        if (existing is not null && existing.Text == text)
        {
            // A metadata-only change retains the complete stored vector without running inference.
            result = await _store.UpdateAsync(request.RepositoryIdentity, existing.Id, existing.Revision, write, null, null, request.Options, cancellationToken);
        }
        else
        {
            var model = _generator.Model;
            var embedding = await _generator.GenerateAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (embedding.WasTruncated || embedding.InputTokenCount > model.MaxInputTokens)
            {
                throw new ArgumentException(
                    $"Memory uses {embedding.InputTokenCount} tokens; the embedding limit is {model.MaxInputTokens} including boundary tokens. Shorten the memory and retry.");
            }

            if (!EmbeddingValidation.IsValid(model, embedding))
            {
                throw new InvalidOperationException("Embedding generation returned an invalid vector. No memory was changed; retry after fixing local embeddings.");
            }

            result = existing is null
                ? await _store.AddAsync(request.RepositoryIdentity, write, model, embedding, request.Options, cancellationToken)
                : await _store.UpdateAsync(request.RepositoryIdentity, existing.Id, existing.Revision, write, model, embedding, request.Options, cancellationToken);
        }

        LogEvictions(result.EvictedIds);
        return new RepositoryMemoryOperationResult(
            result.Status.ToString().ToLowerInvariant(), result.Entry?.Id ?? request.Id, result.Entry, [], result.EvictedIds)
        {
            StandingPreferenceCount = result.StandingPreferenceCount,
        };
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        return _store.GetSnapshotAsync(repositoryIdentity, [], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
        string repositoryIdentity,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default)
    {
        var evicted = await _store.EnforceCapacityAsync(repositoryIdentity, options, cancellationToken);
        LogEvictions(evicted);
        return evicted;
    }

    /// <inheritdoc />
    public Task RecordInclusionsAsync(
        string repositoryIdentity,
        RunId runId,
        IReadOnlyList<RepositoryMemoryInclusion> inclusions,
        CancellationToken cancellationToken = default)
    {
        return _store.RecordInclusionsAsync(repositoryIdentity, runId, inclusions, cancellationToken);
    }

    private void LogEvictions(IReadOnlyList<RepositoryMemoryId> evictedIds)
    {
        foreach (var id in evictedIds)
        {
            _logger.LogDebug("Repository memory {MemoryId} evicted by the configured capacity and deterministic recency/frequency policy.", id.Value);
        }
    }

    private static string Normalize(string text) => text.ReplaceLineEndings("\n").Trim();

    private static void ValidateArguments(RepositoryMemoryOperationRequest request)
    {
        var validType = request.MemoryType is null || Enum.IsDefined(request.MemoryType.Value);
        var valid = request.Action switch
        {
            "add" => request.Id is null && request.Text is not null && validType,
            "update" => request.Id is not null && request.Text is not null && validType,
            "remove" => request.Id is not null && request.Text is null && request.MemoryType is null,
            "list" => request.Id is null && request.Text is null && request.MemoryType is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Use add with text; update with id and text; remove with id; or list with neither id nor text.");
        }
    }
}

/// <summary>Validates detached vectors without depending on an inference implementation.</summary>
internal static class EmbeddingValidation
{
    /// <summary>Checks finite nonzero vector components and model dimensions.</summary>
    internal static bool IsValid(TextEmbeddingModelDescriptor model, TextEmbeddingResult result)
    {
        if (string.IsNullOrWhiteSpace(model.SpaceId) || model.Dimensions <= 0 || model.MaxInputTokens <= 0
            || result.InputTokenCount < 0 || result.Vector.Length != model.Dimensions)
        {
            return false;
        }

        var norm = 0d;
        foreach (var value in result.Vector.Span)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            norm += (double)value * value;
        }

        return norm > 0 && double.IsFinite(norm);
    }
}
