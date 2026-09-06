namespace Threadsmith.Validation;

using Threadsmith.Core;

/// <summary>Calculates directly changed projects and their transitive dependents.</summary>
public sealed class AffectedProjectCalculator
{
    /// <summary>Calculates the affected graph from changed repository paths and semantic project metadata.</summary>
    /// <param name="repositoryPath">Normalized repository root.</param>
    /// <param name="changedFiles">Repository-relative or confined absolute changed paths.</param>
    /// <param name="projects">Semantic project inventory.</param>
    /// <returns>Deterministically ordered directly changed projects and dependents.</returns>
    public static AffectedProjectSet Calculate(
        string repositoryPath,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<SemanticProjectInfo> projects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(projects);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var projectByPath = projects
            .GroupBy(project => Path.GetFullPath(project.FilePath), comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        var direct = new HashSet<string>(comparer);
        var unmapped = new List<string>();
        foreach (var configuredPath in changedFiles)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
            var fullPath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(configuredPath.Replace('/', Path.DirectorySeparatorChar), root);
            if (!IsWithinRoot(fullPath, root))
            {
                throw new InvalidOperationException($"Changed path '{configuredPath}' is outside the repository root.");
            }

            var owner = projects
                .Where(project =>
                {
                    var projectPath = Path.GetFullPath(project.FilePath);
                    var directory = Path.GetDirectoryName(projectPath);
                    return comparer.Equals(projectPath, fullPath)
                        || (directory is not null && IsWithinRoot(fullPath, directory));
                })
                .OrderByDescending(project => Path.GetDirectoryName(Path.GetFullPath(project.FilePath))?.Length ?? 0)
                .FirstOrDefault();
            if (owner is null)
            {
                unmapped.Add(Path.GetRelativePath(root, fullPath).Replace('\\', '/'));
                continue;
            }

            direct.Add(Path.GetFullPath(owner.FilePath));
        }

        var dependents = projectByPath.Keys.ToDictionary(path => path, _ => new HashSet<string>(comparer), comparer);
        foreach (var project in projects)
        {
            var projectPath = Path.GetFullPath(project.FilePath);
            var projectDirectory = Path.GetDirectoryName(projectPath) ?? root;
            foreach (var configuredReference in project.ProjectReferences)
            {
                if (string.IsNullOrWhiteSpace(configuredReference))
                {
                    continue;
                }

                string? referencedPath = null;
                if (configuredReference.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                {
                    referencedPath = Path.IsPathRooted(configuredReference)
                        ? Path.GetFullPath(configuredReference)
                        : Path.GetFullPath(configuredReference.Replace('/', Path.DirectorySeparatorChar), projectDirectory);
                }
                else
                {
                    referencedPath = projects
                        .FirstOrDefault(candidate => comparer.Equals(candidate.Name, configuredReference))
                        ?.FilePath;
                    if (referencedPath is not null)
                    {
                        referencedPath = Path.GetFullPath(referencedPath);
                    }
                }

                if (referencedPath is not null && dependents.TryGetValue(referencedPath, out var referencingProjects))
                {
                    referencingProjects.Add(projectPath);
                }
            }
        }

        var affected = new HashSet<string>(direct, comparer);
        var queue = new Queue<string>(direct.OrderBy(path => path, comparer));
        while (queue.TryDequeue(out var path))
        {
            if (!dependents.TryGetValue(path, out var projectDependents))
            {
                continue;
            }

            foreach (var dependent in projectDependents.OrderBy(item => item, comparer))
            {
                if (affected.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        AffectedProject[] result = [.. affected
            .Select(path => projectByPath[path])
            .OrderByDescending(project => direct.Contains(Path.GetFullPath(project.FilePath)))
            .ThenBy(project => project.Name, comparer)
            .Select(project => new AffectedProject(
                project.Name,
                Path.GetFullPath(project.FilePath),
                project.TargetFrameworks,
                project.Confidence,
                direct.Contains(Path.GetFullPath(project.FilePath))))];
        return new AffectedProjectSet(
            result,
            unmapped.Distinct(comparer).OrderBy(path => path, comparer).ToArray());
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(root, path);
        return !relative.Equals("..", comparison)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison)
            && !Path.IsPathRooted(relative);
    }
}
