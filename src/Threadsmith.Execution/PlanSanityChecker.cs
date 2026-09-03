namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Runs cheap bounded sanity checks over structured implementation plans.</summary>
public sealed class PlanSanityChecker : IPlanSanityChecker
{
    private const int MaximumIssues = 32;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="PlanSanityChecker"/> class.</summary>
    public PlanSanityChecker(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
    }

    /// <inheritdoc />
    public Task<PlanSanityCheckResult> CheckAsync(
        PlanSanityCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumAffectedPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumPathBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryRoot));
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var baselineFiles = new HashSet<string>(
            request.Baseline?.Files.Select(file => file.RelativePath.Replace('\\', '/')) ?? [],
            pathComparer);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<PlanSanityIssue>();
        var declaredCount = 0;
        var pathCharacters = 0;
        var risk = PlanRiskClassification.Low;

        foreach (var step in request.Plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step.FileIntents.Count == 0)
            {
                AddIssue(issues, new PlanSanityIssue
                {
                    Kind = PlanSanityIssueKind.EmptyFileIntents,
                    IsRepairable = true,
                    IsBlocking = true,
                    Message = RenderPromptValue(
                        PromptFileNames.CorrectionPlanSanityIssueEmptyFileIntents,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["StepTitle"] = step.Title,
                        }),
                });
                risk = Max(risk, PlanRiskClassification.High);
                continue;
            }

            foreach (var intent in step.FileIntents)
            {
                var normalizedIntent = NormalizeIntent(
                    intent,
                    repositoryRoot,
                    pathComparison,
                    pathComparer,
                    baselineFiles,
                    request.ProhibitedPaths,
                    normalized,
                    issues,
                    ref declaredCount,
                    ref pathCharacters,
                    ref risk);
                if (normalizedIntent is null)
                {
                    continue;
                }

                ValidateIntentExistence(
                    normalizedIntent,
                    repositoryRoot,
                    baselineFiles,
                    issues,
                    ref risk);
                foreach (var relativePath in normalizedIntent.AffectedPaths)
                {
                    risk = Max(risk, ClassifyPathRisk(relativePath, normalizedIntent.Kind, issues));
                }
            }
        }

        if (normalized.Count > 1)
        {
            risk = Max(risk, PlanRiskClassification.Moderate);
        }

        if (request.Plan.Risks.Count > 0)
        {
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (declaredCount > request.MaximumAffectedPaths || pathCharacters > request.MaximumPathBytes)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.ScopeLimitExceeded,
                IsRepairable = false,
                IsBlocking = true,
                Message = "Plan affected-path scope exceeds host sanity-check bounds.",
            });
            risk = PlanRiskClassification.Blocked;
        }

        if (issues.Any(issue => issue.IsBlocking && !issue.IsRepairable))
        {
            risk = PlanRiskClassification.Blocked;
        }

        return Task.FromResult(new PlanSanityCheckResult
        {
            Risk = risk,
            Issues = issues.ToArray(),
            NormalizedAffectedPaths = [.. normalized.Order(pathComparer)],
            DeclaredAffectedPathCount = declaredCount,
        });
    }

    private string GetPromptValue(string promptFileName)
    {
        return _prompts.Get(promptFileName).TrimEnd('\r', '\n');
    }

    private string RenderPromptValue(
        string promptFileName,
        IReadOnlyDictionary<string, string> tokens)
    {
        return _prompts.Render(promptFileName, tokens).TrimEnd('\r', '\n');
    }

    private NormalizedPlanFileIntent? NormalizeIntent(
        PlanFileIntent intent,
        string repositoryRoot,
        StringComparison pathComparison,
        StringComparer pathComparer,
        HashSet<string> baselineFiles,
        IReadOnlyList<string> prohibitedPaths,
        HashSet<string> normalized,
        List<PlanSanityIssue> issues,
        ref int declaredCount,
        ref int pathCharacters,
        ref PlanRiskClassification risk)
    {
        if (!Enum.IsDefined(intent.Kind))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.InvalidPath,
                IsRepairable = true,
                IsBlocking = true,
                Message = "File-intent kind is not supported.",
            });
            risk = Max(risk, PlanRiskClassification.High);
            return null;
        }

        var requiresDestination = intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename;
        var hasDestination = !string.IsNullOrWhiteSpace(intent.DestinationPath);
        if (requiresDestination != hasDestination)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.InvalidPath,
                RelativePath = intent.Path,
                IsRepairable = true,
                IsBlocking = true,
                Message = requiresDestination
                    ? RenderPromptValue(
                        PromptFileNames.CorrectionPlanSanityIssueRequiresDestination,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["IntentKind"] = intent.Kind.ToString(),
                        })
                    : RenderPromptValue(
                        PromptFileNames.CorrectionPlanSanityIssueForbidsDestination,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["IntentKind"] = intent.Kind.ToString(),
                        }),
            });
            risk = Max(risk, PlanRiskClassification.High);
            return null;
        }

        var source = NormalizeDeclaredPath(
            intent.Path,
            repositoryRoot,
            pathComparison,
            pathComparer,
            baselineFiles,
            prohibitedPaths,
            issues,
            ref declaredCount,
            ref pathCharacters,
            ref risk);
        if (source is null)
        {
            return null;
        }

        var destination = (NormalizedPath?)null;
        if (intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename)
        {
            destination = NormalizeDeclaredPath(
                intent.DestinationPath ?? string.Empty,
                repositoryRoot,
                pathComparison,
                pathComparer,
                baselineFiles,
                prohibitedPaths,
                issues,
                ref declaredCount,
                ref pathCharacters,
                ref risk);
            if (destination is null)
            {
                return null;
            }
        }

        normalized.Add(source.Path);
        if (destination is not null)
        {
            normalized.Add(destination.Path);
        }

        return new NormalizedPlanFileIntent(intent.Kind, source.Path, destination?.Path)
        {
            PathExistence = source.Existence,
            DestinationPathExistence = destination?.Existence,
        };
    }

    private NormalizedPath? NormalizeDeclaredPath(
        string rawPath,
        string repositoryRoot,
        StringComparison pathComparison,
        StringComparer pathComparer,
        HashSet<string> baselineFiles,
        IReadOnlyList<string> prohibitedPaths,
        List<PlanSanityIssue> issues,
        ref int declaredCount,
        ref int pathCharacters,
        ref PlanRiskClassification risk)
    {
        declaredCount++;
        var declared = rawPath.Replace('\\', '/');
        var trimmed = declared.Trim();
        pathCharacters += declared.Length;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.InvalidPath,
                IsRepairable = true,
                IsBlocking = true,
                Message = GetPromptValue(PromptFileNames.CorrectionPlanSanityIssueEmptyPath),
            });
            return null;
        }

        if (ContainsGlob(trimmed))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.AmbiguousPath,
                RelativePath = trimmed,
                IsRepairable = true,
                IsBlocking = true,
                Message = RenderPromptValue(
                    PromptFileNames.CorrectionPlanSanityIssueGlobLikeExactFiles,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Path"] = trimmed,
                    }),
            });
            risk = Max(risk, PlanRiskClassification.High);
            return null;
        }

        var relativePath = NormalizePath(trimmed, repositoryRoot, pathComparison);
        if (relativePath is null)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.InvalidPath,
                RelativePath = trimmed,
                IsRepairable = false,
                IsBlocking = true,
                Message = $"File-intent path '{trimmed}' is not confined to the repository.",
            });
            risk = PlanRiskClassification.Blocked;
            return null;
        }

        if (relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.GitMetadataPath,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = true,
                Message = "Plans may not target Git metadata.",
            });
            risk = PlanRiskClassification.Blocked;
            return null;
        }

        if (IsProtected(relativePath, prohibitedPaths))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.ProtectedPath,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = true,
                Message = $"File-intent path '{relativePath}' is protected by repository policy.",
            });
            risk = PlanRiskClassification.Blocked;
            return null;
        }

        if (!string.Equals(declared, relativePath, StringComparison.Ordinal))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.AmbiguousPath,
                RelativePath = trimmed,
                IsRepairable = true,
                IsBlocking = true,
                Message = RenderPromptValue(
                    PromptFileNames.CorrectionPlanSanityIssueNormalizedPath,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["DeclaredPath"] = trimmed,
                        ["NormalizedPath"] = relativePath,
                    }),
            });
            risk = Max(risk, PlanRiskClassification.High);
            return null;
        }

        var pathExistence = InspectPath(repositoryRoot, relativePath);
        if (pathExistence == PathExistence.Unsafe)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.InvalidPath,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = true,
                Message = $"File-intent path '{relativePath}' traverses a link or cannot be safely inspected.",
            });
            risk = PlanRiskClassification.Blocked;
            return null;
        }

        if (pathExistence == PathExistence.Directory)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.AmbiguousPath,
                RelativePath = relativePath,
                IsRepairable = true,
                IsBlocking = true,
                Message = RenderPromptValue(
                    PromptFileNames.CorrectionPlanSanityIssueDirectoryExactFiles,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Path"] = relativePath,
                    }),
            });
            risk = Max(risk, PlanRiskClassification.High);
            return null;
        }

        if (!relativePath.Contains('/', StringComparison.Ordinal)
            && pathExistence == PathExistence.Absent
            && baselineFiles.Count > 0)
        {
            var matches = baselineFiles
                .Where(file => pathComparer.Equals(Path.GetFileName(file), relativePath))
                .Take(3)
                .ToArray();
            if (matches.Length == 1 && !pathComparer.Equals(matches[0], relativePath))
            {
                AddIssue(issues, new PlanSanityIssue
                {
                    Kind = PlanSanityIssueKind.AmbiguousPath,
                    RelativePath = relativePath,
                    IsRepairable = true,
                    IsBlocking = true,
                    Message = RenderPromptValue(
                        PromptFileNames.CorrectionPlanSanityIssueBarePathResolved,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["DeclaredPath"] = relativePath,
                            ["ResolvedPath"] = matches[0],
                        }),
                });
                risk = Max(risk, PlanRiskClassification.High);
                return null;
            }

            if (matches.Length > 1)
            {
                AddIssue(issues, new PlanSanityIssue
                {
                    Kind = PlanSanityIssueKind.AmbiguousPath,
                    RelativePath = relativePath,
                    IsRepairable = true,
                    IsBlocking = true,
                    Message = RenderPromptValue(
                        PromptFileNames.CorrectionPlanSanityIssueBarePathAmbiguous,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Path"] = relativePath,
                        }),
                });
                risk = Max(risk, PlanRiskClassification.High);
                return null;
            }
        }

        return new NormalizedPath(relativePath, pathExistence);
    }

    private static void ValidateIntentExistence(
        NormalizedPlanFileIntent intent,
        string repositoryRoot,
        HashSet<string> baselineFiles,
        List<PlanSanityIssue> issues,
        ref PlanRiskClassification risk)
    {
        var sourceExists = baselineFiles.Contains(intent.Path) || intent.PathExistence == PathExistence.Exists;
        if (intent.Kind == PlanFileChangeKind.Create)
        {
            if (sourceExists)
            {
                AddIssue(issues, new PlanSanityIssue
                {
                    Kind = PlanSanityIssueKind.CreateTargetExists,
                    RelativePath = intent.Path,
                    IsRepairable = true,
                    IsBlocking = true,
                    Message = $"Step declares creating '{intent.Path}', but it already exists in the repository.",
                });
                risk = Max(risk, PlanRiskClassification.High);
            }

            return;
        }

        if (!sourceExists)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.MissingExistingFile,
                RelativePath = intent.Path,
                IsRepairable = true,
                IsBlocking = true,
                Message = $"Step declares {FormatKind(intent.Kind)} '{intent.Path}', but it is absent from the repository.",
            });
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (intent is { DestinationPath: { } destinationPath, DestinationPathExistence: { } destinationExistence })
        {
            var destinationExists = baselineFiles.Contains(destinationPath)
                || destinationExistence == PathExistence.Exists;
            if (destinationExists && !IsCaseOnlyRelocationDestination(intent, repositoryRoot, baselineFiles))
            {
                AddIssue(issues, new PlanSanityIssue
                {
                    Kind = PlanSanityIssueKind.CreateTargetExists,
                    RelativePath = destinationPath,
                    IsRepairable = true,
                    IsBlocking = true,
                    Message = $"Step declares {FormatKind(intent.Kind)} to '{destinationPath}', but the destination already exists in the repository.",
                });
                risk = Max(risk, PlanRiskClassification.High);
            }
        }
    }

    private static bool IsCaseOnlyRelocationDestination(
        NormalizedPlanFileIntent intent,
        string repositoryRoot,
        HashSet<string> baselineFiles)
    {
        if (intent.Kind is not (PlanFileChangeKind.Move or PlanFileChangeKind.Rename)
            || intent.DestinationPath is null
            || StringComparer.Ordinal.Equals(intent.Path, intent.DestinationPath)
            || !StringComparer.OrdinalIgnoreCase.Equals(intent.Path, intent.DestinationPath))
        {
            return false;
        }

        return !baselineFiles.Any(file => StringComparer.Ordinal.Equals(file, intent.DestinationPath))
            && !PathExistsWithExactCasing(repositoryRoot, intent.DestinationPath);
    }

    private static bool PathExistsWithExactCasing(string repositoryRoot, string relativePath)
    {
        var current = repositoryRoot;
        try
        {
            foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Directory.Exists(current))
                {
                    return false;
                }

                if (!Directory.EnumerateFileSystemEntries(current)
                    .Any(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal)))
                {
                    return false;
                }

                current = Path.Combine(current, segment);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static PlanRiskClassification ClassifyPathRisk(
        string relativePath,
        PlanFileChangeKind changeKind,
        List<PlanSanityIssue> issues)
    {
        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        var risk = PlanRiskClassification.Low;
        if (changeKind is not PlanFileChangeKind.Modify)
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.LifecycleChange,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = false,
                Message = $"Affected path '{relativePath}' has {FormatKind(changeKind)} lifecycle risk.",
            });
            var lifecycleRisk = changeKind == PlanFileChangeKind.Create
                ? PlanRiskClassification.Moderate
                : PlanRiskClassification.High;
            risk = Max(risk, lifecycleRisk);
        }

        if (IsConfigurationOrDependency(relativePath, fileName, extension))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.ConfigurationOrDependencyChange,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = false,
                Message = $"Affected path '{relativePath}' changes configuration, project, or dependency state.",
            });
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (IsGenerated(relativePath, fileName))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.GeneratedPath,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = false,
                Message = $"Affected path '{relativePath}' appears generated.",
            });
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (IsBinary(extension))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.BinaryPath,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = false,
                Message = $"Affected path '{relativePath}' appears to be binary or non-text.",
            });
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (changeKind == PlanFileChangeKind.Delete
            && relativePath.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            AddIssue(issues, new PlanSanityIssue
            {
                Kind = PlanSanityIssueKind.TestDeletion,
                RelativePath = relativePath,
                IsRepairable = false,
                IsBlocking = false,
                Message = $"Affected path '{relativePath}' may remove test coverage.",
            });
            risk = Max(risk, PlanRiskClassification.High);
        }

        if (relativePath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            risk = Max(risk, PlanRiskClassification.Moderate);
        }

        return risk;
    }

    private static string? NormalizePath(string path, string repositoryRoot, StringComparison comparison)
    {
        if (Path.IsPathRooted(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }

        if (!IsWithin(fullPath, repositoryRoot, comparison))
        {
            return null;
        }

        var relative = Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
        return relative is "." or "" ? null : relative;
    }

    private static bool IsWithin(string candidate, string root, StringComparison comparison)
    {
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static PathExistence InspectPath(string repositoryRoot, string relativePath)
    {
        var current = repositoryRoot;
        try
        {
            var finalAttributes = File.GetAttributes(current);
            if ((finalAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return PathExistence.Unsafe;
            }

            foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                finalAttributes = File.GetAttributes(current);
                if ((finalAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    return PathExistence.Unsafe;
                }
            }

            return (finalAttributes & FileAttributes.Directory) != 0
                ? PathExistence.Directory
                : PathExistence.Exists;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathExistence.Absent;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PathExistence.Unsafe;
        }
    }

    private static bool ContainsGlob(string path)
    {
        return path.Contains("*", StringComparison.Ordinal)
            || path.Contains("?", StringComparison.Ordinal)
            || path.EndsWith("/", StringComparison.Ordinal);
    }

    private static bool IsProtected(string relativePath, IReadOnlyList<string> prohibitedPaths)
    {
        var fileName = Path.GetFileName(relativePath);
        if (relativePath.StartsWith(".threadsmith/secrets/", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".snk", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RepositoryPathPolicy.IsProhibited(relativePath, prohibitedPaths);
    }

    private static bool IsConfigurationOrDependency(string relativePath, string fileName, string extension)
    {
        return relativePath.StartsWith(".threadsmith/", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenerated(string relativePath, string fileName)
    {
        return fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("generated/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/generated/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinary(string extension)
    {
        var normalized = extension.ToLowerInvariant();
        return normalized is ".dll" or ".exe" or ".pdb" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".zip" or ".nupkg";
    }

    private static string FormatKind(PlanFileChangeKind kind)
    {
        return kind.ToString().ToLowerInvariant();
    }

    private enum PathExistence
    {
        Absent,
        Directory,
        Exists,
        Unsafe,
    }

    private sealed record NormalizedPath(string Path, PathExistence Existence);

    private sealed record NormalizedPlanFileIntent(
        PlanFileChangeKind Kind,
        string Path,
        string? DestinationPath)
    {
        public PathExistence PathExistence { get; init; }

        public PathExistence? DestinationPathExistence { get; init; }

        public IReadOnlyList<string> AffectedPaths => DestinationPath is null
            ? [Path]
            : [Path, DestinationPath];
    }

    private static PlanRiskClassification Max(PlanRiskClassification left, PlanRiskClassification right)
    {
        return (PlanRiskClassification)Math.Max((int)left, (int)right);
    }

    private static void AddIssue(List<PlanSanityIssue> issues, PlanSanityIssue issue)
    {
        if (issues.Count < MaximumIssues)
        {
            issues.Add(issue);
            return;
        }

        if (!issue.IsBlocking)
        {
            return;
        }

        var replaceIndex = issues.FindIndex(static retained => !retained.IsBlocking);
        if (replaceIndex >= 0)
        {
            issues[replaceIndex] = issue;
        }
    }
}
