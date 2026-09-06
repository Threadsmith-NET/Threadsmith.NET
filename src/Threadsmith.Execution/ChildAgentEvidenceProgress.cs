namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Tools;

/// <summary>Tracks whether child tool rounds expand source or result coverage.</summary>
internal sealed class ChildAgentEvidenceProgressTracker
{
    private readonly HashSet<string> _contentDigests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);

    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private int _evidenceItems;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentEvidenceProgressTracker" /> class.</summary>
    internal ChildAgentEvidenceProgressTracker(IReadOnlyList<Evidence> initialEvidence)
    {
        ArgumentNullException.ThrowIfNull(initialEvidence);
        foreach (var evidence in initialEvidence)
        {
            _evidenceItems++;
            _ = _contentDigests.Add(CreateDigest(evidence.Content));
            if (!string.IsNullOrWhiteSpace(evidence.Provenance.SourcePath))
            {
                _ = _files.Add(NormalizePath(evidence.Provenance.SourcePath));
            }

            _ = _sources.Add(CreateSourceIdentity(
                evidence.Provenance.Source,
                evidence.Provenance.SourcePath,
                evidence.Provenance.SourceRange?.ToString()));
        }
    }

    /// <summary>Captures cumulative coverage before or after a tool batch.</summary>
    internal ChildAgentEvidenceCoverage Capture()
    {
        return new(_files.Count, _sources.Count, _contentDigests.Count, _evidenceItems);
    }

    /// <summary>Adds one exact sanitized tool result and its attributable sources.</summary>
    internal void Observe(ToolInvocationResult result, string content)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(content);
        _evidenceItems++;
        _ = _contentDigests.Add(CreateDigest(content));
        foreach (var source in result.Sources)
        {
            if (string.Equals(source.Kind, "file", StringComparison.OrdinalIgnoreCase))
            {
                _ = _files.Add(NormalizePath(source.Identifier));
            }

            _ = _sources.Add(CreateSourceIdentity(source.Kind, source.Identifier, source.Range));
        }
    }

    /// <summary>Measures coverage added since the supplied snapshot.</summary>
    internal ChildAgentEvidenceProgress Measure(ChildAgentEvidenceCoverage before)
    {
        var after = Capture();
        return new(
            after.FileCount - before.FileCount,
            after.SourceCount - before.SourceCount,
            after.ContentCount - before.ContentCount,
            after.FileCount,
            after.SourceCount,
            after.EvidenceItemCount);
    }

    private static string CreateDigest(string content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static string CreateSourceIdentity(string kind, string? identifier, string? range)
    {
        return $"{kind.Trim()}\u001f{identifier?.Trim()}\u001f{range?.Trim()}";
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }
}

/// <summary>One immutable evidence-coverage measurement.</summary>
internal readonly record struct ChildAgentEvidenceCoverage(
    int FileCount,
    int SourceCount,
    int ContentCount,
    int EvidenceItemCount);

/// <summary>Coverage added by one child tool round plus cumulative totals.</summary>
internal readonly record struct ChildAgentEvidenceProgress(
    int NewFiles,
    int NewSources,
    int NewContentPayloads,
    int TotalFiles,
    int TotalSources,
    int TotalEvidenceItems)
{
    /// <summary>Gets whether the tool round expanded attributable source coverage.</summary>
    internal bool ExpandedAttributedCoverage => NewFiles > 0 || NewSources > 0;

    /// <summary>Gets whether the tool round returned content not seen in an earlier result.</summary>
    internal bool AddedDistinctPayload => NewContentPayloads > 0;
}
