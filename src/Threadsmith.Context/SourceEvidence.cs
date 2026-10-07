namespace Threadsmith.Context;

/// <summary>Normalizes source identity for evidence admission and invalidation.</summary>
public static class SourceEvidence
{
    /// <summary>Normalizes absolute and relative file identities using the repository root.</summary>
    public static string NormalizePath(string repositoryPath, string path)
    {
        return Path.GetRelativePath(repositoryPath, Path.GetFullPath(path.Replace('\\', '/'), repositoryPath)).Replace('\\', '/');
    }

    /// <summary>Includes legacy single-source provenance without claiming a verified file version.</summary>
    public static IReadOnlyList<EvidenceFileDependency> Dependencies(Evidence evidence)
    {
        return evidence.FileDependencies.Count > 0
            ? evidence.FileDependencies
            : (evidence.Kind == EvidenceKind.SourceExcerpt || evidence.Provenance.Source == "tool:read_file")
                && evidence.Provenance.SourcePath is { } path
                ? [new EvidenceFileDependency(path, null, evidence.Provenance.SourceRange)]
                : [];
    }
}
