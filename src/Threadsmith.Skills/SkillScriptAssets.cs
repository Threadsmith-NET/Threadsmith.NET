namespace Threadsmith.Skills;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>One filename and its executable repository-relative source path.</summary>
internal sealed record SkillScriptPath(string Name, string ScriptPath);

/// <summary>Prepares inert native script assets without executing or granting tool authority.</summary>
internal static class SkillScriptAssets
{
    /// <summary>Bounds preparation across all scripts, not just each package asset.</summary>
    internal const int MaximumPreparationBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConditionalWeakTable<SkillInvocationRequest, WorkflowPreparation> Preparations = [];

    /// <summary>Limits native scripts to portable filenames in the flat scripts folder.</summary>
    internal static void ValidateMetadata(SkillAssetMetadata asset)
    {
        var path = asset.Path.Replace('\\', '/');
        var script = string.Equals(asset.Kind, "script", StringComparison.OrdinalIgnoreCase);
        var folder = path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase);
        if (!script && !folder && !Path.GetExtension(path).Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (!script || !folder || !asset.Path.Equals(path, StringComparison.Ordinal)
            || !path.Equals("scripts/" + name, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)
            || name.Length <= 4 || name[0] == '.'
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new InvalidDataException("Native scripts require kind 'script' and a flat scripts/<filename>.csx path with letters, digits, '.', '_' or '-'.");
        }
    }

    /// <summary>Reuses verified paths across steps of one workflow; no script bodies are cached.</summary>
    internal static async Task<IReadOnlyList<SkillScriptPath>> PrepareForWorkflowAsync(
        SkillInvocationPlan plan,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.ScriptAssets.Count == 0)
        {
            return [];
        }

        var preparation = Preparations.GetValue(plan.Request, _ => new WorkflowPreparation(plan, context));
        preparation.ValidateScope(plan, context);
        var paths = await preparation.GetPathsAsync(plan, context, cancellationToken).WaitAsync(cancellationToken);

        if (context.SecurityPolicy is { } policy)
        {
            context.PolicyBinding?.Validate(policy);
        }

        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path.ScriptPath, context.RepositoryPath);
            var roots = context.ApprovedRoots.Select(root => Path.GetFullPath(root, context.RepositoryPath));
            if (context.Scratchpad is { IsActive: true, RootPath: { } scratchpad }
                && RepositoryPathPolicy.ContainsPath(scratchpad, fullPath))
            {
                roots = roots.Append(scratchpad);
            }

            _ = ScopedFilePaths.ValidateRead(fullPath, context.RepositoryPath, context.RepositoryPath, roots, context.ProhibitedPaths);
        }

        return paths;
    }

    /// <summary>Binds package expectations to the ordinary immutable source-capture path.</summary>
    internal static IReadOnlyDictionary<string, ScriptSourceIntegrity> BindIntegrity(
        SkillInvocationPlan plan,
        IReadOnlyList<SkillScriptPath> paths,
        ToolInvocationContext context)
    {
        var integrity = new Dictionary<string, ScriptSourceIntegrity>(context.ScriptSourceIntegrity, RepositoryPathPolicy.GetPathComparer(context.RepositoryPath));
        for (var index = 0; index < paths.Count; index++)
        {
            var path = Path.GetFullPath(paths[index].ScriptPath, context.RepositoryPath);
            var asset = plan.ScriptAssets[index].Asset;
            var expected = new ScriptSourceIntegrity(asset.Bytes, asset.Sha256);
            if (integrity.TryGetValue(path, out var inherited) && inherited != expected)
            {
                throw new InvalidDataException("Conflicting inherited script integrity expectations.");
            }

            integrity[path] = expected;
        }

        return integrity.ToFrozenDictionary(integrity.Comparer);
    }

    /// <summary>Stages external scripts once per session/package and verifies copies before reuse.</summary>
    internal static async Task<IReadOnlyList<SkillScriptPath>> PrepareAsync(
        SkillInvocationPlan plan,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (plan.ScriptAssets.Count > 0 && context.SecurityPolicy is { } policy)
        {
            context.PolicyBinding?.Validate(policy);
        }

        if (plan.ScriptAssets.Sum(source => source.Asset.Bytes) > MaximumPreparationBytes)
        {
            throw new InvalidDataException("Packaged scripts exceed the aggregate preparation byte limit.");
        }

        var maximumCharacters = context.SecurityPolicy?.ResourceLimits.GetValueOrDefault("tools:config:csharp_script:max_code_characters", int.MaxValue) ?? int.MaxValue;
        var maximumWriteBytes = context.SecurityPolicy?.ResourceLimits.GetValueOrDefault("tools:writeFile:maxContentBytes", int.MaxValue) ?? int.MaxValue;
        foreach (var source in plan.ScriptAssets)
        {
            if (source.Asset.Bytes > Math.Min(maximumWriteBytes + 3L, (3L * maximumCharacters) + 3))
            {
                throw new InvalidDataException("Packaged script exceeds the effective C# source byte limit.");
            }
        }

        var paths = new List<SkillScriptPath>();
        foreach (var source in plan.ScriptAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMetadata(source.Asset);
            var original = SkillPathPolicy.ResolveConfined(source.PackageRoot, source.Asset.Path);
            var name = Path.GetFileName(original);
            string executable;
            if (RepositoryPathPolicy.TryNormalizeContainedPath(context.RepositoryPath, original, out var contained))
            {
                ValidateSourceBytes(await SkillAssetContent.ReadAsync(source.PackageRoot, source.Asset, cancellationToken), maximumCharacters, maximumWriteBytes);
                executable = ScopedFilePaths.ValidateRead(
                    contained,
                    context.RepositoryPath,
                    context.RepositoryPath,
                    context.ApprovedRoots.Select(root => Path.GetFullPath(root, context.RepositoryPath)),
                    context.ProhibitedPaths);
            }
            else
            {
                if (context.Scratchpad is not { IsActive: true, RootPath: { } scratchpad }
                    || !RepositoryPathPolicy.ContainsPath(context.RepositoryPath, scratchpad))
                {
                    throw new UnauthorizedAccessException("External skill scripts require an active session scratchpad inside the repository.");
                }

                var scope = new ScriptFileScope
                {
                    RepositoryPath = context.RepositoryPath,
                    ScratchpadPath = scratchpad,
                    ProhibitedPaths = context.ProhibitedPaths,
                    ControlPaths = context.SecurityPolicy?.ControlPaths ?? [],
                    ControlDirectories = context.SecurityPolicy?.ControlDirectories ?? [],
                };
                var folder = Path.Combine("skill-scripts", plan.Request.SessionId.Value.ToString("N"), plan.Package.Digest.Value);
                executable = ScopedFilePaths.ValidateWrite(Path.Combine(folder, name), scope);
                void ValidateDestination()
                {
                    _ = ScopedFilePaths.ValidateWrite(executable, scope);
                    _ = ScopedFilePaths.ValidateRead(executable, scratchpad, context.RepositoryPath, [scratchpad], context.ProhibitedPaths);
                }

                ValidateDestination();
                if (!File.Exists(executable))
                {
                    var bytes = await SkillAssetContent.ReadAsync(source.PackageRoot, source.Asset, cancellationToken);
                    ValidateSourceBytes(bytes, maximumCharacters, maximumWriteBytes);
                    try
                    {
                        await AtomicFileWriter.WriteAsync(executable, bytes, overwrite: false, ValidateDestination, cancellationToken);
                    }
                    catch (IOException) when (File.Exists(executable))
                    {
                        // A concurrent invocation may have published the same immutable asset.
                        // The shared integrity read below must still accept that exact copy.
                    }
                }

                ValidateDestination();
                var copy = source.Asset with { Path = name };
                var parent = Path.GetDirectoryName(executable)
                    ?? throw new InvalidDataException("Staged script has no parent directory.");
                ValidateSourceBytes(await SkillAssetContent.ReadAsync(parent, copy, cancellationToken), maximumCharacters, maximumWriteBytes);
            }

            paths.Add(new SkillScriptPath(name, Path.GetRelativePath(context.RepositoryPath, executable).Replace('\\', '/')));
        }

        return paths;
    }

    private static void ValidateSourceBytes(ReadOnlySpan<byte> bytes, int maximumCharacters, int maximumWriteBytes)
    {
        var offset = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        var source = bytes[offset..];
        if (source.Length > maximumWriteBytes || StrictUtf8.GetCharCount(source) > maximumCharacters)
        {
            throw new InvalidDataException("Packaged script exceeds the effective C# source limits.");
        }
    }

    // Weak request ownership releases the mapping when the workflow ends, including resume boundaries.
    // Task publication prevents concurrent steps from duplicating preparation. Only paths/metadata survive.
    private sealed class WorkflowPreparation
    {
        private readonly SkillPackageIdentity _package;
        private readonly SkillScriptSource[] _sources;
        private readonly string _repository;
        private readonly ScratchpadSessionCapability _scratchpad;
        private readonly EffectiveSessionPolicy? _policy;
        private readonly Lock _gate = new();
        private Task<IReadOnlyList<SkillScriptPath>>? _paths;

        public WorkflowPreparation(SkillInvocationPlan plan, ToolInvocationContext context)
        {
            _package = plan.Package;
            _sources = [.. plan.ScriptAssets];
            _repository = context.RepositoryPath;
            _scratchpad = context.Scratchpad;
            _policy = context.SecurityPolicy;
        }

        public Task<IReadOnlyList<SkillScriptPath>> GetPathsAsync(SkillInvocationPlan plan, ToolInvocationContext context, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_paths is { IsCanceled: true } or { IsFaulted: true })
                {
                    _paths = null;
                }

                return _paths ??= PrepareAsync(plan, context, cancellationToken);
            }
        }

        public void ValidateScope(SkillInvocationPlan plan, ToolInvocationContext context)
        {
            if (_package != plan.Package || !_sources.SequenceEqual(plan.ScriptAssets)
                || !string.Equals(_repository, context.RepositoryPath, StringComparison.Ordinal)
                || _scratchpad != context.Scratchpad || !ReferenceEquals(_policy, context.SecurityPolicy))
            {
                throw new InvalidOperationException("Skill script preparation scope changed; restart the workflow to revalidate its assets.");
            }
        }
    }
}
