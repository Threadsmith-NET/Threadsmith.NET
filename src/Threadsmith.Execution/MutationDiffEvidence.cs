namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Retains original source and renders bounded net changes independently of execution scheduling.</summary>
internal sealed class MutationDiffEvidence
{
    private readonly IExecutionArtifactPublisher _artifacts;
    private readonly WorkspaceResourceLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="MutationDiffEvidence"/> class.</summary>
    public MutationDiffEvidence(IExecutionArtifactPublisher artifacts, WorkspaceResourceLimits limits)
    {
        _artifacts = artifacts;
        _limits = limits;
    }

    /// <summary>Captures only previously unseen endpoints from the immutable transaction baseline.</summary>
    public async Task<IReadOnlyDictionary<string, ExecutionArtifactReference?>> CaptureAsync(
        SessionId sessionId,
        ITransactionalWorkspace workspace,
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, ExecutionArtifactReference?> existing,
        CancellationToken cancellationToken)
    {
        var originals = new Dictionary<string, ExecutionArtifactReference?>(existing, RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath));
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            if (originals.ContainsKey(normalized))
            {
                continue;
            }

            var content = await workspace.ReadBaselineTextAsync(normalized, cancellationToken);

            // JSON supports empty files and distinguishes absent files from existing empty content.
            if (content is null)
            {
                originals[normalized] = null;
                continue;
            }

            var serialized = JsonSerializer.Serialize(content);
            var reference = await _artifacts.PublishAsync(sessionId, "mutationOriginalTextV1", serialized, cancellationToken);
            if (await _artifacts.ReadAsync(reference, cancellationToken) == serialized)
            {
                originals[normalized] = reference;
            }
        }

        return originals;
    }

    /// <summary>Publishes a net diff when every changed endpoint has retained original content and fits the existing bounds.</summary>
    public async Task<MutationDiffEvidenceResult> PublishAsync(
        SessionId sessionId,
        ITransactionalWorkspace workspace,
        IEnumerable<string> changedPaths,
        IReadOnlyDictionary<string, ExecutionArtifactReference?> originals,
        CancellationToken cancellationToken)
    {
        var comparer = RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath);
        var retained = new Dictionary<string, ExecutionArtifactReference?>(originals, comparer);
        var diff = new StringBuilder();
        foreach (var path in changedPaths.Distinct(comparer))
        {
            var normalized = path.Replace('\\', '/');
            if (!retained.TryGetValue(normalized, out var reference))
            {
                return new(null, false);
            }

            string? before = null;
            if (reference is not null)
            {
                var content = await _artifacts.ReadAsync(reference, cancellationToken)
                    ?? throw new InvalidDataException("The original mutation file artifact is missing or corrupt.");
                before = reference.Kind == "mutationOriginalTextV1" ? JsonSerializer.Deserialize<string>(content) : content;
            }

            var after = await workspace.ReadBaselineTextAsync(normalized, cancellationToken);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            var remaining = _limits.MaximumFinalDiffCharacters - diff.Length;
            if (remaining <= 0 || !UnifiedTextDiff.TryCreate(normalized, before, after, _limits.MaximumDiffLinesForLcs, remaining, out var fileDiff, out _, out _))
            {
                return new(null, false);
            }

            diff.Append(fileDiff);
        }

        if (diff.Length == 0)
        {
            return new(null, true);
        }

        var rendered = diff.ToString();
        var artifact = await _artifacts.PublishAsync(sessionId, "executionFinalDiff", rendered, cancellationToken);
        return new(artifact, await _artifacts.ReadAsync(artifact, cancellationToken) == rendered);
    }
}

/// <summary>Distinguishes an empty complete net diff from omitted or sanitized evidence.</summary>
internal sealed record MutationDiffEvidenceResult(ExecutionArtifactReference? Artifact, bool Complete);
