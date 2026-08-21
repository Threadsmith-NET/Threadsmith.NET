namespace Threadsmith.Workspaces;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Classifies mutation previews for policy-aware approval.</summary>
public static class MutationRiskCalculator
{
    /// <summary>Calculates host-owned risk indicators from a mutation set and its exact preview.</summary>
    /// <param name="mutationSet">Proposed typed mutations.</param>
    /// <param name="preview">Exact staged diff.</param>
    /// <param name="repositoryRoot">Authorized repository root.</param>
    /// <param name="largeDiffThreshold">Changed-line threshold.</param>
    /// <returns>Immutable risk assessment.</returns>
    public static MutationRiskAssessment Calculate(
        MutationSet mutationSet,
        MutationPreview preview,
        string repositoryRoot,
        int largeDiffThreshold = 500)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(largeDiffThreshold);

        var fullRoot = Path.GetFullPath(repositoryRoot);
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        var outsideRepository = false;
        var configChanges = false;
        var dependencyChanges = false;
        foreach ((var mutation, var relativePath) in mutationSet.Mutations.SelectMany(mutation =>
            (mutation.DestinationRelativePath is null
                ? [mutation.RelativePath]
                : new[] { mutation.RelativePath, mutation.DestinationRelativePath })
            .Select(path => (mutation, path.Replace('\\', '/')))))
        {
            var fileName = Path.GetFileName(relativePath);
            var extension = Path.GetExtension(relativePath);
            var isConfig = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
                || (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                    && extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase);
            configChanges |= isConfig;
            dependencyChanges |= fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("packages.config", StringComparison.OrdinalIgnoreCase)
                || (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    && ((mutation.ExpectedText?.Contains("PackageReference", StringComparison.OrdinalIgnoreCase) ?? false)
                        || mutation.ReplacementText.Contains("PackageReference", StringComparison.OrdinalIgnoreCase)));

            try
            {
                var fullPath = Path.GetFullPath(relativePath, fullRoot);
                outsideRepository |= Path.IsPathRooted(relativePath)
                    || (!fullPath.StartsWith(rootPrefix, PathComparison)
                        && !string.Equals(fullPath, fullRoot, PathComparison));
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                outsideRepository = true;
            }
        }

        var totalLines = checked(preview.AddedLines + preview.RemovedLines);
        return new MutationRiskAssessment
        {
            HasDeletions = mutationSet.Mutations.Any(mutation => mutation.Type == MutationType.DeleteFile),
            HasMoves = mutationSet.Mutations.Any(mutation => mutation.Type == MutationType.MoveFile),
            HasConfigChanges = configChanges,
            HasDependencyChanges = dependencyChanges,
            HasLargeDiff = totalLines > largeDiffThreshold,
            HasOutsideRepoChanges = outsideRepository,
            FileCount = mutationSet.Mutations
                .SelectMany(mutation => mutation.DestinationRelativePath is null
                    ? [mutation.RelativePath.Replace('\\', '/')]
                    : new[]
                    {
                        mutation.RelativePath.Replace('\\', '/'),
                        mutation.DestinationRelativePath.Replace('\\', '/'),
                    })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            TotalLinesChanged = totalLines,
        };
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

/// <summary>Repository-backed session mutation policy with invariant path and secret guardrails.</summary>
public sealed class MutationApprovalPolicyService : IMutationApprovalPolicy
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _repositoryConfigurationPath;
    private MutationApprovalPolicy _currentPolicy;
    private int _largeDiffThreshold;

    /// <summary>Initializes a new instance of the <see cref="MutationApprovalPolicyService"/> class.</summary>
    /// <param name="configuration">Effective layered configuration.</param>
    /// <param name="repositoryConfigurationPath">Repository config path used for persistent trust.</param>
    public MutationApprovalPolicyService(
        IConfiguration? configuration = null,
        string? repositoryConfigurationPath = null)
    {
        if (repositoryConfigurationPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfigurationPath);
            _repositoryConfigurationPath = Path.GetFullPath(repositoryConfigurationPath);
        }

        ApplyConfiguration(configuration);
    }

    /// <inheritdoc />
    public MutationApprovalPolicy CurrentPolicy => _currentPolicy;

    /// <inheritdoc />
    public int LargeDiffThreshold => _largeDiffThreshold;

    /// <inheritdoc />
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Repository root '{normalizedRoot}' does not exist.");
        }

        var configurationPath = Path.Combine(normalizedRoot, ".threadsmith", "config.json");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(configurationPath, _repositoryConfigurationPath, PathComparison))
            {
                return;
            }

            EnsureNoReparsePoints(normalizedRoot, configurationPath);
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(configurationPath, optional: true)
                .Build();
            ApplyConfiguration(configuration);
            _repositoryConfigurationPath = configurationPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetPolicyAsync(
        MutationApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryConfigurationPath is not null)
            {
                await PersistRepositoryTrustAsync(policy, cancellationToken);
            }

            _currentPolicy = policy;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool RequiresApproval(MutationRiskAssessment risk, bool isWithinPlan)
    {
        ArgumentNullException.ThrowIfNull(risk);
        return _currentPolicy switch
        {
            MutationApprovalPolicy.ReviewAll => true,
            MutationApprovalPolicy.ReviewRisky => risk.IsRisky,
            MutationApprovalPolicy.TrustPlan => !isWithinPlan,
            MutationApprovalPolicy.TrustSession or MutationApprovalPolicy.AlwaysTrustRepo => false,
            _ => throw new InvalidOperationException($"Unsupported mutation approval policy '{_currentPolicy}'."),
        };
    }

    /// <inheritdoc />
    public void Validate(MutationSet mutations, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var fullRoot = Path.GetFullPath(repositoryRoot);
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        foreach (var relativePath in mutations.Mutations
            .SelectMany(mutation => mutation.DestinationRelativePath is null
                ? [mutation.RelativePath]
                : new[] { mutation.RelativePath, mutation.DestinationRelativePath })
            .Select(path => path.Replace('\\', '/')))
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new MutationPolicyException("Mutation targets must be repository-relative.");
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(relativePath, fullRoot);
            }
            catch (Exception exception) when (exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                throw new MutationPolicyException("A mutation target contains an invalid path.");
            }

            if (!fullPath.StartsWith(rootPrefix, PathComparison)
                || relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)))
            {
                throw new MutationPolicyException(
                    "Mutations must stay inside the authorized repository and may not modify Git metadata.");
            }

            var fileName = Path.GetFileName(relativePath);
            var isSecretPath = relativePath.StartsWith(".threadsmith/secrets/", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".snk", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase);
            if (isSecretPath)
            {
                throw new MutationPolicyException("Mutation policy prohibits changes to secret-bearing paths.");
            }
        }
    }

    private void ApplyConfiguration(IConfiguration? configuration)
    {
        var configured = configuration?["mutation:approvalPolicy"] ?? "reviewAll";
        if (!Enum.TryParse(configured, ignoreCase: true, out _currentPolicy)
            || !Enum.IsDefined(_currentPolicy))
        {
            throw new InvalidOperationException(
                $"Unknown mutation approval policy '{configured}'.");
        }

        var thresholdText = configuration?["mutation:largeDiffThreshold"];
        if (thresholdText is not null
            && (!int.TryParse(thresholdText, out var configuredThreshold)
                || configuredThreshold <= 0))
        {
            throw new InvalidOperationException(
                $"Mutation large-diff threshold '{thresholdText}' must be a positive integer.");
        }

        _largeDiffThreshold = thresholdText is null
            ? 500
            : int.Parse(thresholdText, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task PersistRepositoryTrustAsync(
        MutationApprovalPolicy policy,
        CancellationToken cancellationToken)
    {
        var configurationPath = _repositoryConfigurationPath
            ?? throw new InvalidOperationException("A repository configuration path is required.");
        var directory = Path.GetDirectoryName(configurationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The repository configuration path has no parent directory.");
        }

        var repositoryRoot = Directory.GetParent(directory)?.FullName
            ?? throw new InvalidOperationException("The repository configuration path has no repository root.");
        EnsureNoReparsePoints(repositoryRoot, configurationPath);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(repositoryRoot, configurationPath);
        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            configurationPath,
            async token =>
            {
                var root = File.Exists(configurationPath)
                    ? JsonNode.Parse(
                        await File.ReadAllTextAsync(configurationPath, token),
                        new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                        documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                        ?? throw new InvalidOperationException(
                            "Repository configuration must contain a JSON object.")
                    : [];
                var mutation = root["mutation"] as JsonObject ?? [];
                root["mutation"] = mutation;
                if (policy == MutationApprovalPolicy.AlwaysTrustRepo)
                {
                    mutation["approvalPolicy"] = "alwaysTrustRepo";
                }
                else
                {
                    mutation.Remove("approvalPolicy");
                    if (mutation.Count == 0)
                    {
                        root.Remove("mutation");
                    }
                }

                var temporaryPath = configurationPath + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    EnsureNoReparsePoints(repositoryRoot, configurationPath);
                    await File.WriteAllTextAsync(
                        temporaryPath,
                        root.ToJsonString(_jsonOptions) + Environment.NewLine,
                        token);
                    EnsureNoReparsePoints(repositoryRoot, configurationPath);
                    File.Move(temporaryPath, configurationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        EnsureNoReparsePoints(repositoryRoot, temporaryPath);
                        File.Delete(temporaryPath);
                    }
                }
            },
            cancellationToken);
    }

    private static void EnsureNoReparsePoints(string repositoryRoot, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var fullPath = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException("Repository policy persistence must stay inside the repository.");
        }

        var currentPath = normalizedRoot;
        if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Repository policy persistence cannot traverse a symbolic link or junction.");
        }

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                break;
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Repository policy persistence cannot traverse a symbolic link or junction.");
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
