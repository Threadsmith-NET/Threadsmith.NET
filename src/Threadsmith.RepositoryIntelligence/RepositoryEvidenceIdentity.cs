namespace Threadsmith.RepositoryIntelligence;

using System.Text.Json;

/// <summary>Shared deterministic source/excerpt identities for collection and retention validation.</summary>
internal static class RepositoryEvidenceIdentity
{
    /// <summary>Reproduces the collector's source identity independently of any excerpt.</summary>
    internal static string SourceId(RepositoryEvidenceSource source)
    {
        var fields = new
        {
            kind = source.Kind,
            source.RepositoryIdentity,
            source.CheckoutIdentity,
            revision = source.Revision,
            path = source.Path,
            identity = source.SourceIdentity,
            previousRevision = source.PreviousRevision,
            previousPath = source.PreviousPath,
        };
        return RepositoryProfileCollector.Digest(JsonSerializer.Serialize(fields));
    }

    /// <summary>Reproduces an excerpt identity without reading or substituting repository content.</summary>
    internal static string ExcerptId(RepositoryEvidenceSource source, string state, string? text, int? start, int? end)
        => RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { source.Id, start, end, state, Digest = text is null ? null : RepositoryProfileCollector.Digest(text) }));
}
