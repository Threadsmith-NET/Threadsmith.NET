namespace Threadsmith.DotNet;

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using BuildProject = Microsoft.Build.Evaluation.Project;

/// <content>Verifies resolved MSBuild imports before retaining published project configuration.</content>
public sealed partial class SemanticEngine
{
    private readonly ConditionalWeakTable<MSBuildWorkspace, ResolvedImportSnapshot> _resolvedImportBaselines = [];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ResolvedImportSnapshot? TryCaptureResolvedImports(
        IEnumerable<string> projectPaths,
        ImmutableDictionary<string, string> workspaceProperties,
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        using var capture = new GraphInputCapture(this, workspaceProperties, request, progress: null, cancellationToken);
        foreach (var path in projectPaths)
        {
            if (!capture.Capture(path))
            {
                return null;
            }
        }

        return capture.Snapshot();
    }

    private ResolvedImportSnapshot? VerifyGraphImportBaseline(
        ResolvedImportSnapshot baseline,
        IReadOnlySet<string> opened,
        ImmutableDictionary<string, string> properties,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (opened.Any(path => !baseline.Projects.ContainsKey(path)))
        {
            return null;
        }

        var identities = new Dictionary<string, string?>(StringComparerForCurrentPlatform());
        if (!RestoreInputsMatch(baseline, identities, request, cancellationToken))
        {
            return null;
        }

        foreach (var (path, conventional) in baseline.Conventional)
        {
            if (!ConventionalInputsMatch(path, conventional, identities, request, cancellationToken))
            {
                return null;
            }
        }

        var dynamicPaths = opened.Where(path => !baseline.Conventional.ContainsKey(path));
        return TryCaptureResolvedImports(dynamicPaths, properties, request, cancellationToken) is { } after && baseline.Covers(after)
            ? baseline : null;
    }

    private bool RestoreInputsMatch(
        ResolvedImportSnapshot baseline,
        Dictionary<string, string?> identities,
        SemanticLoadRequest request,
        CancellationToken cancellationToken) => baseline.RestoreInputs.Values.All(inputs => ScopeInputsMatch(inputs, request, cancellationToken, identities, restoreAssets: true));

    private bool ConventionalInputsMatch(
        string path,
        ConventionalProjectInputs inputs,
        Dictionary<string, string?> identities,
        SemanticLoadRequest request,
        CancellationToken cancellationToken) =>
        UserExtensionsEmpty(inputs.UserExtensions, request)
        && ScopeInputsMatch(inputs.Local, request, cancellationToken, identities)
        && Path.GetDirectoryName(path) is { } directory && !HasUnindexedLocalImports(path, directory, request);

    private static bool UserExtensionsEmpty(string path, SemanticLoadRequest request) =>
        !PathExists(path) || (IsSemanticInputPathAllowed(path, request) && !Directory.EnumerateFileSystemEntries(path).Any());

    private static FrozenDictionary<string, bool> CaptureExistsStates(
        IEnumerable<string> paths, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, bool>(StringComparerForCurrentPlatform());
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSemanticInputPathAllowed(path, request))
            {
                throw new InvalidOperationException("A literal MSBuild Exists input could not be verified under repository path policy.");
            }

            states.Add(path, PathExists(path));
        }

        return states.ToFrozenDictionary(states.Comparer);
    }

    private static bool PathExists(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !Path.EndsInDirectorySeparator(path) || (attributes & FileAttributes.Directory) != 0;
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private sealed class GraphInputCapture : IProgress<ProjectLoadProgress>, IDisposable
    {
        private readonly SemanticEngine _engine;
        private readonly SourceRefreshEvaluation _evaluation;
        private readonly ProjectLoadProgressCollector? _progress;
        private readonly CancellationToken _cancellationToken;
        private readonly HashSet<string> _attempted = new(StringComparerForCurrentPlatform());
        private readonly Dictionary<string, Dictionary<string, ImmutableArray<ResolvedDeclaration>>> _projects = new(StringComparerForCurrentPlatform());
        private readonly Dictionary<string, FrozenDictionary<string, bool>> _existsByProject = new(StringComparerForCurrentPlatform());
        private readonly Dictionary<string, ConventionalProjectInputs> _conventional = new(StringComparerForCurrentPlatform());
        private readonly Dictionary<string, FrozenDictionary<string, string?>> _restoreInputs = new(StringComparerForCurrentPlatform());
        private readonly Dictionary<string, string?> _restoreIdentities = new(StringComparerForCurrentPlatform());

        public GraphInputCapture(
            SemanticEngine engine,
            ImmutableDictionary<string, string> properties,
            SemanticLoadRequest request,
            ProjectLoadProgressCollector? progress,
            CancellationToken cancellationToken)
        {
            _engine = engine;
            _evaluation = new(properties, request, engine._resourceLimits.MaximumInventoryXmlBytes);
            _progress = progress;
            _cancellationToken = cancellationToken;
        }

        public bool Capture(string path)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_attempted.Add(path))
            {
                return _projects.ContainsKey(path);
            }

            if (!Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || !IsSemanticInputPathAllowed(path, _evaluation.Request))
            {
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The MSBuild project directory is unknown.");
                var local = TryCaptureLocalInputs(path, directory);
                var project = _evaluation.OpenProject(path);
                var frameworks = new Dictionary<string, ImmutableArray<ResolvedDeclaration>>(StringComparer.OrdinalIgnoreCase);
                var existsPaths = new HashSet<string>(StringComparerForCurrentPlatform());
                var restoreInputs = new Dictionary<string, string?>(StringComparerForCurrentPlatform());
                var conventional = string.Equals(project.Xml.Sdk, "Microsoft.NET.Sdk", StringComparison.Ordinal);
                var ordinaryDeclarations = new HashSet<string>(StringComparerForCurrentPlatform());
                var userExtensions = project.GetPropertyValue("MSBuildUserExtensionsPath");
                string? nonConventionalDeclaration = null;
                var innerFrameworks = GetInnerTargetFrameworks(project);
                CaptureFramework();
                foreach (var framework in innerFrameworks)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    _evaluation.SelectFramework(project, framework);
                    CaptureFramework();
                }

                var exists = CaptureExistsStates(existsPaths, _evaluation.Request, _cancellationToken);
                if (conventional)
                {
                    if (!Path.IsPathFullyQualified(userExtensions) || !UserExtensionsEmpty(userExtensions, _evaluation.Request))
                    {
                        nonConventionalDeclaration = "unverified SDK user extensions";
                    }
                    else if (HasUnindexedLocalImports(path, directory, _evaluation.Request))
                    {
                        nonConventionalDeclaration = "unindexed local imports";
                    }
                    else if (local is not null && ordinaryDeclarations.All(declaration => local.TryGetValue(declaration, out var identity) && identity is not null))
                    {
                        _conventional.Add(path, new(local, userExtensions));
                    }
                }

                if (!_conventional.ContainsKey(path) && _engine._logger.IsEnabled(LogLevel.Debug))
                {
                    var reason = nonConventionalDeclaration ?? (conventional ? "unverified local inputs or extension directory" : "nonstandard SDK");
                    _engine._logger.LogDebug("Project import reuse unavailable for {ImportReuseProject}: {ImportReuseReason}", path, reason);
                }

                _projects.Add(path, frameworks);
                _existsByProject.Add(path, exists);
                _restoreInputs.Add(path, restoreInputs.ToFrozenDictionary(restoreInputs.Comparer));
                return true;

                void CaptureFramework()
                {
                    var imports = _evaluation.ReadImports(project, _cancellationToken);
                    frameworks.Add(imports.Framework, imports.Declarations);
                    var assets = project.GetPropertyValue("ProjectAssetsFile");
                    if (assets.Length != 0)
                    {
                        var assetsPath = Path.GetFullPath(assets.Replace('\\', Path.DirectorySeparatorChar), directory);
                        if (!_restoreIdentities.TryGetValue(assetsPath, out var identity))
                        {
                            identity = ReadRestoreIdentity(assetsPath, _evaluation.Request, _cancellationToken);
                            _restoreIdentities.Add(assetsPath, identity);
                        }

                        restoreInputs.TryAdd(assetsPath, identity);
                    }

                    var toolsPath = project.GetPropertyValue("MSBuildToolsPath");
                    foreach (var declaration in project.Imports.Select(import => import.ImportedProject).Prepend(project.Xml))
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        if (!IsStockBuildDeclaration(declaration, toolsPath))
                        {
                            var dependencies = _evaluation.GetSourceDependencies(declaration, directory);
                            existsPaths.UnionWith(dependencies.ExistsPaths);
                            var simple = !dependencies.Opaque && dependencies.ExistsPaths.IsEmpty
                                && declaration.Imports.Count == 0
                                && declaration.Items.All(item => item.ItemType is "ProjectReference" or "PackageReference" or "PackageVersion"
                                    && !item.Include.AsSpan().ContainsAny(_unresolvedImportCharacters)
                                    && item.Metadata.All(metadata => !_sourceInputItems.Contains(metadata.Value)))
                                && declaration.Properties.All(property => IsConventionalProperty(property.Name));
                            if (!simple)
                            {
                                nonConventionalDeclaration ??= declaration.FullPath;
                                conventional = false;
                            }

                            ordinaryDeclarations.Add(declaration.FullPath);
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidProjectFileException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or XmlException)
            {
                _engine._logger.LogDebug(exception, "Resolved imports could not be verified for scoped semantic refresh.");
                return false;
            }
        }

        public void Report(ProjectLoadProgress value)
        {
            _progress?.Report(value);
            if (value.Operation == ProjectLoadOperation.Evaluate)
            {
                // Roslyn reports project evaluation before its design-time build. It can also
                // probe a metadata reference. Only retain snapshots of projects Roslyn opens.
                Capture(value.FilePath);
            }
        }

        public ResolvedImportSnapshot Snapshot(IReadOnlySet<string>? opened = null) => new(
            _projects.Where(pair => opened?.Contains(pair.Key) != false).ToFrozenDictionary(_projects.Comparer),
            _existsByProject.Where(pair => opened?.Contains(pair.Key) != false).ToFrozenDictionary(_existsByProject.Comparer),
            _conventional.Where(pair => opened?.Contains(pair.Key) != false).ToFrozenDictionary(_conventional.Comparer),
            _restoreInputs.Where(pair => opened?.Contains(pair.Key) != false).ToFrozenDictionary(_restoreInputs.Comparer),
            _engine._resourceLimits.MaximumInventoryXmlBytes);

        public void Dispose() => _evaluation.Dispose();

        private FrozenDictionary<string, string?>? TryCaptureLocalInputs(string path, string directory)
        {
            try
            {
                return _engine.CaptureProjectInputs(path, directory, _evaluation.Request, _cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or XmlException)
            {
                _engine._logger.LogDebug(exception, "Local inputs could not be captured for verifying {ImportReuseProject}", path);
                return null;
            }
        }

        private static bool IsConventionalProperty(string name) => name is
            "TargetFramework" or "TargetFrameworks" or "OutputType" or "ImplicitUsings" or "Nullable" or "LangVersion"
            or "AssemblyName" or "RootNamespace" or "DefineConstants" or "Optimize" or "IsPackable" or "TreatWarningsAsErrors"
            or "UseSharedCompilation" or "MSBuildUserExtensionsPath";
    }

    private static string[] GetInnerTargetFrameworks(BuildProject project) => project.GetPropertyValue("TargetFramework").Length != 0
        ? []
        : project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record ResolvedImportSnapshot(
        FrozenDictionary<string, Dictionary<string, ImmutableArray<ResolvedDeclaration>>> Projects,
        FrozenDictionary<string, FrozenDictionary<string, bool>> ExistsByProject,
        FrozenDictionary<string, ConventionalProjectInputs> Conventional,
        FrozenDictionary<string, FrozenDictionary<string, string?>> RestoreInputs,
        long MaximumXmlBytes)
    {
        public bool MatchesProject(string path, (string Framework, ImmutableArray<ResolvedDeclaration> Declarations) current) =>
            Projects.TryGetValue(path, out var frameworks) && frameworks.TryGetValue(current.Framework, out var baseline)
            && baseline.AsSpan().SequenceEqual(current.Declarations.AsSpan());

        public bool CoversImports(ResolvedImportSnapshot other) => other.Projects.All(pair =>
            Projects.TryGetValue(pair.Key, out var frameworks) && frameworks.Count == pair.Value.Count
            && pair.Value.All(framework => MatchesProject(pair.Key, (framework.Key, framework.Value))));

        public bool CoversRestoreInputs(ResolvedImportSnapshot other) => other.RestoreInputs.All(project =>
            RestoreInputs.TryGetValue(project.Key, out var expected) && expected.Count == project.Value.Count
            && project.Value.All(input => expected.TryGetValue(input.Key, out var identity) && identity == input.Value));

        public bool Covers(ResolvedImportSnapshot other) => CoversImports(other) && CoversRestoreInputs(other) && other.ExistsByProject.All(project =>
            ExistsByProject.TryGetValue(project.Key, out var exists) && ExistsStatesMatch(exists, project.Value));

        public static bool ExistsStatesMatch(FrozenDictionary<string, bool> expected, FrozenDictionary<string, bool> actual) =>
            expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private sealed record ConventionalProjectInputs(FrozenDictionary<string, string?> Local, string UserExtensions);

    private readonly record struct ResolvedDeclaration(string Path, string Identity);

    private sealed class SourceRefreshEvaluation : IDisposable
    {
        private readonly ProjectCollection _collection;
        private readonly ProjectOptions _options;
        private readonly long _maximumXmlBytes;
        private readonly Dictionary<ProjectRootElement, string> _identities = [];
        private readonly Dictionary<(ProjectRootElement Declaration, string Directory), SourceDependencies> _dependencies = [];
        private readonly Dictionary<string, string> _toolchainContexts = new(StringComparerForCurrentPlatform());
        private readonly HashSet<string> _verifiedToolchainContexts = new(StringComparerForCurrentPlatform());

        public SourceRefreshEvaluation(ImmutableDictionary<string, string> properties, SemanticLoadRequest request, long maximumXmlBytes)
        {
            // Match the globals of the Roslyn .NET build host. A fresh batch rediscovers wildcard matches.
            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DesignTimeBuild"] = bool.TrueString,
                ["NonExistentFile"] = @"__NonExistentSubDir__\__NonExistentFile__",
                ["BuildProjectReferences"] = bool.FalseString,
                ["BuildingProject"] = bool.FalseString,
                ["ProvideCommandLineArgs"] = bool.TrueString,
                ["SkipCompilerExecution"] = bool.TrueString,
                ["ContinueOnError"] = "ErrorAndContinue",
                ["ShouldUnsetParentConfigurationAndPlatform"] = bool.FalseString,
            };
            foreach (var pair in properties)
            {
                globals[pair.Key] = pair.Value;
            }

            Request = request;
            _maximumXmlBytes = maximumXmlBytes;
            _collection = new ProjectCollection(globals);
            _options = new ProjectOptions { ProjectCollection = _collection, EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared) };
        }

        public SemanticLoadRequest Request { get; }

        public BuildProject OpenProject(string path)
        {
            VerifyToolchain(Request.SolutionPath);
            VerifyToolchain(path);
            var project = BuildProject.FromFile(path, _options);
            if (string.IsNullOrEmpty(project.Xml.Sdk) && project.Xml.Imports.All(import => string.IsNullOrEmpty(import.Sdk))
                && project.Xml.Children.All(child => child is not ProjectSdkElement))
            {
                throw new InvalidOperationException($"The workspace may use a non-SDK MSBuild toolchain for '{path}'.");
            }

            return project;
        }

        public BuildProject GetLoadedProject(string path) => _collection.GetLoadedProjects(path).Single();

        public SourceDependencies GetSourceDependencies(ProjectRootElement declaration, string directory)
        {
            var key = (declaration, directory);
            if (!_dependencies.TryGetValue(key, out var dependencies))
            {
                dependencies = ReadSourceDependencies(declaration, directory);
                _dependencies.Add(key, dependencies);
            }

            return dependencies;
        }

        public void SelectFramework(BuildProject project, string framework)
        {
            project.SetGlobalProperty("TargetFramework", framework);
            project.ReevaluateIfNecessary(_options.EvaluationContext);
        }

        public (string Framework, ImmutableArray<ResolvedDeclaration> Declarations) ReadImports(BuildProject project, CancellationToken cancellationToken)
        {
            var toolsPath = project.GetPropertyValue("MSBuildToolsPath");
            var imports = ImmutableArray.CreateBuilder<ResolvedDeclaration>();
            foreach (var declaration in project.Imports.Select(import => import.ImportedProject).Prepend(project.Xml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_identities.TryGetValue(declaration, out var identity))
                {
                    if (IsRepositoryLocalSemanticInputRejected(declaration.FullPath, Request))
                    {
                        throw new InvalidOperationException("A resolved MSBuild declaration is prohibited by repository path policy.");
                    }

                    // MSBuild already loaded this XML for a trusted evaluation. Verify it without
                    // reopening external package or SDK files or admitting them as repository inputs.
                    var xml = declaration.OuterElement.AsSpan();
                    if (!IsStockBuildDeclaration(declaration, toolsPath) && Encoding.UTF8.GetByteCount(xml) > _maximumXmlBytes)
                    {
                        throw new InvalidOperationException("A resolved MSBuild declaration exceeds the scope XML limit.");
                    }

                    identity = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(xml)));
                    _identities.Add(declaration, identity);
                }

                imports.Add(new(declaration.FullPath, identity));
            }

            return (project.GetPropertyValue("TargetFramework"), imports.ToImmutable());
        }

        public void Dispose() => _collection.Dispose();

        private void VerifyToolchain(string entryPath)
        {
            var directory = Path.GetDirectoryName(entryPath) ?? throw new InvalidOperationException("The MSBuild entry directory is unknown.");
            var context = GetToolchainContext(directory);
            if (_verifiedToolchainContexts.Contains(context))
            {
                return;
            }

            // The graph build host can be anchored at the solution; a scoped host starts at the
            // project. Both must agree with in-process evaluation. Compare paths, not just versions.
            lock (_msBuildGate)
            {
                if (_registeredMsBuildInstance is { DiscoveryType: DiscoveryType.DotNetSdk } registered
                    && MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
                    {
                        DiscoveryTypes = DiscoveryType.DotNetSdk,
                        WorkingDirectory = directory,
                        AllowAllDotnetLocations = true,
                        AllowAllRuntimeVersions = true,
                    }).FirstOrDefault() is { } selected
                    && string.Equals(registered.MSBuildPath, selected.MSBuildPath, PathComparison))
                {
                    _verifiedToolchainContexts.Add(context);
                    return;
                }
            }

            throw new InvalidOperationException($"The workspace MSBuild toolchain could not be matched for '{directory}'.");
        }

        private string GetToolchainContext(string directory)
        {
            if (!_toolchainContexts.TryGetValue(directory, out var context))
            {
                // SDK selection searches upwards for global.json. Discover once per distinct
                // search context in this evaluation, not once per project directory.
                context = PathExists(Path.Combine(directory, "global.json")) || Path.GetDirectoryName(directory) is not { } parent
                    ? directory
                    : GetToolchainContext(parent);
                _toolchainContexts.Add(directory, context);
            }

            return context;
        }
    }
}
