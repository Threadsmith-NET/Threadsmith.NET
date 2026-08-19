namespace Threadsmith.Validation;

using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Discovers xUnit and Microsoft.Testing.Platform projects and test cases.</summary>
public sealed class TestDiscoverer
{
    private readonly IProcessManager _processManager;

    /// <summary>Initializes a new instance of the <see cref="TestDiscoverer"/> class.</summary>
    public TestDiscoverer(IProcessManager processManager)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        _processManager = processManager;
    }

    /// <summary>Discovers supported test projects from the semantic project inventory.</summary>
    /// <param name="repositoryPath">Repository root governing project confinement.</param>
    /// <param name="projects">Complete semantic project inventory.</param>
    /// <param name="prohibitedPaths">Repository-relative path patterns that discovery must reject.</param>
    /// <returns>Deterministically ordered supported test projects.</returns>
    public static IReadOnlyList<TestProject> DiscoverProjects(
        string repositoryPath,
        IReadOnlyList<SemanticProjectInfo> projects,
        IReadOnlyList<string>? prohibitedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(projects);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var discovered = new List<TestProject>();
        foreach (var project in projects)
        {
            var projectPath = Path.GetFullPath(project.FilePath);
            var relative = Path.GetRelativePath(root, projectPath);
            if (relative.Equals("..", comparison)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", comparison)
                || Path.IsPathRooted(relative)
                || !File.Exists(projectPath))
            {
                throw new InvalidOperationException(
                    $"Test discovery project '{project.FilePath}' must exist under the repository root.");
            }

            var normalizedRelative = relative.Replace('\\', '/');
            if (RepositoryPathPolicy.IsProhibited(normalizedRelative, prohibitedPaths ?? []))
            {
                throw new UnauthorizedAccessException(
                    $"Test discovery project '{normalizedRelative}' is prohibited by repository path policy.");
            }

            ValidationPathGuard.EnsureNoReparsePointTraversal(
                root,
                projectPath,
                normalizedRelative,
                "Test discovery project");

            using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            string[] packageReferences = [.. document
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value ?? string.Empty)
                .Concat(project.PackageReferences)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            var isXunit = packageReferences.Any(package =>
                package.Equals("xunit", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase));
            var usesTestingPlatform = packageReferences.Any(package =>
                    package.StartsWith("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
                || document.Descendants().Any(element =>
                    (element.Name.LocalName == "UseMicrosoftTestingPlatformRunner"
                        || element.Name.LocalName == "TestingPlatformDotnetTestSupport")
                    && bool.TryParse(element.Value, out var enabled)
                    && enabled);
            if (!isXunit && !usesTestingPlatform)
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectPath) ?? root;
            string[] references = [.. document
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath((value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar), projectDirectory))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .OrderBy(path => path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
            discovered.Add(new TestProject
            {
                Name = project.Name,
                FilePath = projectPath,
                TargetFrameworks = project.TargetFrameworks.ToArray(),
                Framework = usesTestingPlatform
                    ? TestFramework.MicrosoftTestingPlatform
                    : TestFramework.XUnit,
                ProjectReferences = references,
            });
        }

        return discovered
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Enumerates test cases in the already selected projects without building or restoring.</summary>
    /// <param name="runId">Owning validation run.</param>
    /// <param name="repositoryPath">Approved process working directory.</param>
    /// <param name="projects">Selected test projects.</param>
    /// <param name="timeout">Per-project discovery timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Framework-neutral discovered test cases.</returns>
    public Task<IReadOnlyList<TestCase>> DiscoverCasesAsync(
        RunId runId,
        string repositoryPath,
        IReadOnlyList<TestProject> projects,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return DiscoverCasesAsync(
            runId,
            repositoryPath,
            projects,
            filter: null,
            timeout,
            cancellationToken);
    }

    /// <summary>Enumerates test cases after applying one host-generated framework filter.</summary>
    public async Task<IReadOnlyList<TestCase>> DiscoverCasesAsync(
        RunId runId,
        string repositoryPath,
        IReadOnlyList<TestProject> projects,
        string? filter,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (runId == default)
        {
            throw new ArgumentException("Test discovery requires a non-default run id.", nameof(runId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var cases = new List<TestCase>();
        foreach (var project in projects)
        {
            var process = await _processManager.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = new ToolInvocationId(Guid.NewGuid()),
                    RunId = runId,
                    FileName = "dotnet",
                    Arguments = CreateDiscoveryArguments(project, filter),
                    WorkingDirectory = repositoryPath,
                    Timeout = timeout,
                    MaximumOutputCharacters = 1024 * 1024,
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
            if (process.TimedOut || process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Test discovery failed for '{project.Name}' with exit code {process.ExitCode?.ToString() ?? "none"}.");
            }

            var collecting = project.Framework == TestFramework.MicrosoftTestingPlatform;
            foreach (var configuredLine in string.Concat(
                    process.StandardOutput,
                    Environment.NewLine,
                    process.StandardError)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var line = configuredLine.Trim();
                if (line.StartsWith("The following Tests are available:", StringComparison.OrdinalIgnoreCase)
                    || (line.StartsWith("Discovered ", StringComparison.OrdinalIgnoreCase)
                        && line.Contains(" tests in assembly", StringComparison.OrdinalIgnoreCase)))
                {
                    collecting = true;
                    continue;
                }

                if (line.StartsWith("Test discovery summary:", StringComparison.OrdinalIgnoreCase))
                {
                    collecting = false;
                    continue;
                }

                var isTestingPlatformCase = project.Framework == TestFramework.MicrosoftTestingPlatform
                    && configuredLine.Length > 0
                    && char.IsWhiteSpace(configuredLine[0])
                    && line.Contains('.', StringComparison.Ordinal)
                    && !line.StartsWith("xUnit.net ", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("Running tests from ", StringComparison.OrdinalIgnoreCase)
                    && !line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                if (!collecting
                    || (project.Framework == TestFramework.MicrosoftTestingPlatform && !isTestingPlatformCase)
                    || line.StartsWith("Discovered ", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Test run", StringComparison.OrdinalIgnoreCase)
                    || line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var identitySource = $"{project.FilePath}|{line}";
                var id = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)))[..16];
                cases.Add(new TestCase
                {
                    Id = id,
                    FullyQualifiedName = line,
                    ProjectPath = project.FilePath,
                });
            }
        }

        return cases
            .DistinctBy(testCase => testCase.Id, StringComparer.Ordinal)
            .OrderBy(testCase => testCase.ProjectPath, StringComparer.Ordinal)
            .ThenBy(testCase => testCase.FullyQualifiedName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> CreateDiscoveryArguments(TestProject project, string? filter)
    {
        List<string> arguments = project.Framework == TestFramework.MicrosoftTestingPlatform
            ?
            [
                "run", "--project", project.FilePath, "--no-restore", "--no-build", "--", "--list-tests",
            ]
            :
            [
                "test", project.FilePath, "--no-restore", "--no-build", "--nologo", "--list-tests", "--verbosity:minimal",
            ];
        if (!string.IsNullOrWhiteSpace(filter))
        {
            arguments.Add(project.Framework == TestFramework.MicrosoftTestingPlatform
                ? "--filter-trait"
                : "--filter");
            arguments.Add(filter);
        }

        return arguments;
    }
}

/// <summary>Selects a conservative project-level test scope and explains every inclusion.</summary>
public sealed class TestSelector
{
    /// <summary>Selects directly affected and referencing test projects.</summary>
    /// <param name="testProjects">Supported discovered test projects.</param>
    /// <param name="affectedProjects">Directly changed projects and their dependents.</param>
    /// <param name="mutationSet">Mutation set whose effects require tests.</param>
    /// <returns>Selected project scope with deterministic rationale.</returns>
    public static TestSelection Select(
        IReadOnlyList<TestProject> testProjects,
        IReadOnlyList<AffectedProject> affectedProjects,
        MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(testProjects);
        ArgumentNullException.ThrowIfNull(affectedProjects);
        ArgumentNullException.ThrowIfNull(mutationSet);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var affectedPaths = affectedProjects
            .Select(project => Path.GetFullPath(project.FilePath))
            .ToHashSet(comparer);
        var affectedNames = affectedProjects
            .Select(project => project.Name)
            .ToHashSet(comparer);
        var selected = new List<TestProject>();
        var rationale = new List<string>();
        foreach (var testProject in testProjects)
        {
            var fullPath = Path.GetFullPath(testProject.FilePath);
            if (affectedPaths.Contains(fullPath))
            {
                selected.Add(testProject);
                rationale.Add($"Selected {testProject.Name} because it is in the affected project graph.");
                continue;
            }

            string[] referencedAffected = [.. testProject.ProjectReferences
                .Where(reference => affectedPaths.Contains(Path.GetFullPath(reference)))
                .Select(reference => Path.GetFileNameWithoutExtension(reference))
                .Where(affectedNames.Contains)
                .Distinct(comparer)
                .OrderBy(name => name, comparer)];
            if (referencedAffected.Length > 0)
            {
                selected.Add(testProject);
                rationale.Add(
                    $"Selected {testProject.Name} because it references affected project(s): "
                    + $"{string.Join(", ", referencedAffected)}.");
            }
        }

        string[] symbolIds = [.. mutationSet.Mutations
            .Select(mutation => mutation.RelatedSymbolId)
            .Where(symbolId => !string.IsNullOrWhiteSpace(symbolId))
            .Select(symbolId => symbolId ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbolId => symbolId, StringComparer.Ordinal)];
        if (selected.Count > 0 && symbolIds.Length > 0)
        {
            rationale.Add(
                "Project-level selection conservatively covers changed symbol(s): "
                + $"{string.Join(", ", symbolIds)}.");
        }

        if (selected.Count == 0)
        {
            rationale.Add("No supported test project was directly affected or referenced an affected project.");
        }

        return new TestSelection
        {
            Projects = selected
                .DistinctBy(project => Path.GetFullPath(project.FilePath), comparer)
                .OrderBy(project => project.Name, comparer)
                .ToArray(),
            Rationale = rationale,
            RelatedMutationIds = mutationSet.Mutations
                .Select(mutation => mutation.MutationId)
                .Distinct()
                .ToArray(),
        };
    }
}
