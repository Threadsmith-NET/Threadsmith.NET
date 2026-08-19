namespace Threadsmith.DotNet;

using System.Xml.Linq;
using Threadsmith.Core;

/// <summary>Projects the loaded semantic workspace into normalized .NET repository inventory.</summary>
public sealed class DotNetInventoryService : IDotNetInventoryService
{
    private const int MaximumProjects = 2000;
    private readonly IGitQueryService _gitQueries;
    private readonly SemanticEngineRegistry _registry;

    /// <summary>Initializes a new instance of the <see cref="DotNetInventoryService"/> class.</summary>
    public DotNetInventoryService(
        SemanticEngineRegistry registry,
        IGitQueryService gitQueries)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gitQueries);
        _registry = registry;
        _gitQueries = gitQueries;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(DotNetInventoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        SemanticLoadRequest loaded = GetAuthoritativeLoadRequest(request.WorkspaceId, repositoryRoot);
        IReadOnlyList<SemanticProjectInfo> projects = _registry.GetProjects(request.WorkspaceId);
        return
        [
            loaded.SolutionPath,
            Path.Combine(repositoryRoot, "Directory.Packages.props"),
            .. projects.Select(project => project.FilePath),
        ];
    }

    /// <inheritdoc />
    public async Task<DotNetInventoryResult> GetInventoryAsync(
        DotNetInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SelectedSolutionPath);
        cancellationToken.ThrowIfCancellationRequested();
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        SemanticLoadRequest loaded = GetAuthoritativeLoadRequest(request.WorkspaceId, repositoryRoot);
        var selectedSolution = NormalizeUnderRoot(repositoryRoot, loaded.SolutionPath);
        IReadOnlyList<SemanticProjectInfo> semanticProjects = _registry.GetProjects(request.WorkspaceId);
        SemanticConfidenceLevel confidence = _registry.GetConfidence(request.WorkspaceId);
        var omissions = new List<string>();
        if (semanticProjects.Count > MaximumProjects)
        {
            omissions.Add($"Project inventory was limited to {MaximumProjects} entries.");
        }

        IReadOnlyDictionary<string, string> centralVersions = ReadCentralVersions(repositoryRoot, omissions);
        ProjectInventory[] projects = [.. semanticProjects
            .Take(MaximumProjects)
            .Select(project => CreateProject(repositoryRoot, project, centralVersions, omissions, cancellationToken))
            .OrderBy(project => project.Path, StringComparer.OrdinalIgnoreCase)];
        if (projects.Length == 0)
        {
            omissions.Add("No projects are loaded for the selected semantic workspace.");
        }

        var repositoryRevision = await _gitQueries.GetRevisionAsync(
            repositoryRoot,
            cancellationToken);
        var result = new DotNetInventoryResult(
            new SolutionInventory(ToRelative(repositoryRoot, selectedSolution), projects),
            repositoryRevision,
            confidence,
            omissions.Distinct(StringComparer.Ordinal).ToArray(),
            confidence >= SemanticConfidenceLevel.ProjectGraphOnly,
            false);
        return result;
    }

    private static ProjectInventory CreateProject(
        string repositoryRoot,
        SemanticProjectInfo semantic,
        IReadOnlyDictionary<string, string> centralVersions,
        List<string> omissions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectPath = NormalizeUnderRoot(repositoryRoot, semantic.FilePath);
        var packages = new Dictionary<string, PackageReferenceInventory>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(projectPath) && new FileInfo(projectPath).Length <= 1024 * 1024)
        {
            try
            {
                XDocument document = XDocument.Load(projectPath, LoadOptions.None);
                foreach (XElement element in document.Descendants().Where(item => item.Name.LocalName == "PackageReference"))
                {
                    var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var version = element.Attribute("Version")?.Value
                        ?? element.Elements().FirstOrDefault(item => item.Name.LocalName == "Version")?.Value;
                    PackageVersionSource source = PackageVersionSource.Project;
                    if (string.IsNullOrWhiteSpace(version) && centralVersions.TryGetValue(id, out var centralVersion))
                    {
                        version = centralVersion;
                        source = PackageVersionSource.Central;
                    }
                    else if (string.IsNullOrWhiteSpace(version))
                    {
                        source = PackageVersionSource.Unknown;
                    }

                    packages[id] = new PackageReferenceInventory(id, version, source);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                omissions.Add($"Could not inspect package metadata for {ToRelative(repositoryRoot, projectPath)}.");
            }
        }
        else
        {
            omissions.Add($"Project file is missing or exceeds 1 MiB: {ToRelative(repositoryRoot, projectPath)}.");
        }

        foreach (var package in semantic.PackageReferences.Where(package => !string.IsNullOrWhiteSpace(package)))
        {
            PackageVersionSource source = centralVersions.ContainsKey(package)
                ? PackageVersionSource.Central
                : PackageVersionSource.Unknown;
            packages.TryAdd(
                package,
                new PackageReferenceInventory(
                    package,
                    centralVersions.GetValueOrDefault(package),
                    source));
        }

        var relative = ToRelative(repositoryRoot, projectPath);
        var isTest = semantic.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || packages.ContainsKey("Microsoft.NET.Test.Sdk")
            || packages.Keys.Any(id => id is "xunit" or "NUnit" or "MSTest.TestFramework");
        return new ProjectInventory(
            semantic.Name,
            relative,
            semantic.TargetFrameworks.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new TargetFrameworkInventory(value)).ToArray(),
            semantic.ProjectReferences.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new ProjectReferenceInventory(NormalizeReference(repositoryRoot, projectPath, value))).ToArray(),
            packages.Values.OrderBy(value => value.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            isTest,
            semantic.Confidence);
    }

    private static IReadOnlyDictionary<string, string> ReadCentralVersions(
        string repositoryRoot,
        List<string> omissions)
    {
        var path = Path.Combine(repositoryRoot, "Directory.Packages.props");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            if (new FileInfo(path).Length > 1024 * 1024)
            {
                omissions.Add("Directory.Packages.props exceeds the 1 MiB inventory limit.");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            XDocument document = XDocument.Load(path, LoadOptions.None);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "PackageVersion")
                .Select(element => new
                {
                    Id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
                    Version = element.Attribute("Version")?.Value,
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Version))
                .GroupBy(item => item.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Version ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            omissions.Add("Directory.Packages.props could not be inspected.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeReference(string repositoryRoot, string projectPath, string reference)
    {
        var candidate = Path.IsPathFullyQualified(reference)
            ? reference
            : Path.GetFullPath(reference, Path.GetDirectoryName(projectPath) ?? repositoryRoot);
        return ToRelative(repositoryRoot, NormalizeUnderRoot(repositoryRoot, candidate));
    }

    private SemanticLoadRequest GetAuthoritativeLoadRequest(
        WorkspaceId workspaceId,
        string repositoryRoot)
    {
        SemanticLoadRequest loaded = _registry.GetLoadRequest(workspaceId);
        var loadedRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(loaded.RepositoryPath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!loadedRepository.Equals(repositoryRoot, comparison))
        {
            throw new InvalidOperationException(
                "Inventory repository does not match the authoritative loaded workspace.");
        }

        return loaded;
    }

    private static string NormalizeUnderRoot(string repositoryRoot, string candidate)
    {
        var normalized = Path.GetFullPath(candidate, repositoryRoot);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalized.Equals(repositoryRoot, comparison)
            && !normalized.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException("Inventory path escapes the repository.");
        }

        return normalized;
    }

    private static string ToRelative(string repositoryRoot, string path)
    {
        return Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
    }
}
