namespace Threadsmith.Workspaces;

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

/// <summary>Locally approved repository mutation policy with invariant path and secret guardrails.</summary>
public sealed class MutationApprovalPolicyService : IMutationApprovalPolicy
{
    private readonly SessionPolicyBinding? _policyBinding;
    private readonly IRepositoryFactsStore? _facts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _repositoryConfigurationPath;
    private MutationApprovalPolicy _currentPolicy;
    private ConsentState _consent = new(null, 0);
    private int _largeDiffThreshold;

    /// <summary>Initializes a new instance of the <see cref="MutationApprovalPolicyService"/> class.</summary>
    /// <param name="configuration">Effective layered configuration.</param>
    /// <param name="repositoryConfigurationPath">Repository identity hint; permission choices are never written here.</param>
    /// <param name="policyBinding">Optional host security ceiling and generation owner.</param>
    /// <param name="facts">Host-owned repository trust persistence; repository configuration never supplies consent.</param>
    public MutationApprovalPolicyService(
        IConfiguration? configuration = null,
        string? repositoryConfigurationPath = null,
        SessionPolicyBinding? policyBinding = null,
        IRepositoryFactsStore? facts = null)
    {
        _policyBinding = policyBinding;
        _facts = facts;
        if (repositoryConfigurationPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfigurationPath);
            _repositoryConfigurationPath = Path.GetFullPath(repositoryConfigurationPath);
        }

        ApplyConfiguration(configuration);
    }

    /// <inheritdoc />
    public MutationApprovalPolicy CurrentPolicy
    {
        get
        {
            var authority = _policyBinding?.Current;
            var selected = Volatile.Read(ref _consent).Choice ?? authority?.MutationDefault ?? MutationApprovalPolicy.ReviewAll;
            var ceiling = authority?.HasExplicitMutationCeiling == true
                ? authority.MutationCeiling
                : MutationApprovalPolicy.AlwaysTrustRepo;
            return Restrict(selected, ceiling);
        }
    }

    /// <inheritdoc />
    public MutationApprovalPolicy RequestedPolicy => Volatile.Read(ref _consent).Choice
        ?? _policyBinding?.Current.MutationDefault ?? _currentPolicy;

    /// <inheritdoc />
    public EffectiveSessionPolicy? SecurityPolicy => _policyBinding is null ? null
        : _policyBinding.Current with { MutationConsentRevision = Volatile.Read(ref _consent).Revision };

    /// <inheritdoc />
    public void ValidatePolicyGeneration(EffectiveSessionPolicy? capturedPolicy)
    {
        _policyBinding?.Validate(capturedPolicy ?? throw new MutationPolicyException("PolicyGenerationStale: the mutation has no captured policy."));
        if (capturedPolicy is not null && capturedPolicy.MutationConsentRevision != Volatile.Read(ref _consent).Revision)
        {
            throw new MutationPolicyException("PolicyGenerationStale: local mutation consent changed; stage a new diff.");
        }
    }

    /// <inheritdoc />
    public int LargeDiffThreshold => Math.Min(_largeDiffThreshold, _policyBinding?.Current.LargeDiffThreshold ?? 500);

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
            EnsureNoReparsePoints(normalizedRoot, configurationPath);

            var keepSessionChoice = string.Equals(configurationPath, _repositoryConfigurationPath, PathComparison)
                && Volatile.Read(ref _consent).Choice == MutationApprovalPolicy.TrustSession;

            // Fail closed before reading another repository or an unavailable/corrupt store.
            SetLocalChoice(MutationApprovalPolicy.ReviewAll);
            _currentPolicy = MutationApprovalPolicy.ReviewAll;

            // Repository binding keeps authority invalid until every scoped service is ready.
            // CurrentPolicy applies the published authority after that transaction completes.
            var choice = _facts is null ? null : await _facts.GetMutationPolicyAsync(normalizedRoot, cancellationToken);
            if (choice is not (null or MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
            {
                throw new InvalidDataException("Stored mutation consent is invalid.");
            }

            SetLocalChoice(keepSessionChoice ? MutationApprovalPolicy.TrustSession : choice);
            _repositoryConfigurationPath = configurationPath;
        }
        catch
        {
            SetLocalChoice(MutationApprovalPolicy.ReviewAll);
            throw;
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
        if (!Enum.IsDefined(policy) || policy == MutationApprovalPolicy.TrustPlan)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryConfigurationPath is not null && policy != MutationApprovalPolicy.TrustSession)
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(_repositoryConfigurationPath))
                    ?? throw new InvalidOperationException("The repository policy path has no root.");
                EnsureNoReparsePoints(root, _repositoryConfigurationPath);
                if (_facts is null)
                {
                    throw new InvalidOperationException("Remembered mutation consent is unavailable in this host. Use session-only trust or configure a trust store.");
                }

                await _facts.SetMutationPolicyAsync(root, policy, cancellationToken);
            }

            SetLocalChoice(policy);
        }
        catch
        {
            // A failed revocation must not leave automatic effects enabled in this process.
            SetLocalChoice(MutationApprovalPolicy.ReviewAll);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool RequiresApproval(MutationRiskAssessment risk)
    {
        ArgumentNullException.ThrowIfNull(risk);
        return CurrentPolicy switch
        {
            MutationApprovalPolicy.ReviewAll => true,
            MutationApprovalPolicy.ReviewRisky => risk.IsRisky,
            MutationApprovalPolicy.TrustSession or MutationApprovalPolicy.AlwaysTrustRepo => false,
            _ => throw new InvalidOperationException($"Unsupported mutation approval policy '{CurrentPolicy}'."),
        };
    }

    /// <inheritdoc />
    public void Validate(MutationSet mutations, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var paths = mutations.Mutations.SelectMany(mutation => mutation.DestinationRelativePath is null
            ? [mutation.RelativePath] : new[] { mutation.RelativePath, mutation.DestinationRelativePath });
        ValidatePaths(paths, repositoryRoot);
    }

    /// <inheritdoc />
    public void ValidatePaths(IEnumerable<string> relativePaths, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var fullRoot = Path.GetFullPath(repositoryRoot);
        var rootPrefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        foreach (var relativePath in relativePaths.Select(path => path.Replace('\\', '/')))
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

            var normalizedPath = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
            var policy = _policyBinding?.Current;
            try
            {
                AuthorityControlPathGuard.Validate(fullPath, fullRoot, policy?.ControlPaths, policy?.ControlDirectories);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new MutationPolicyException(exception.Message);
            }

            if (policy is not null && (!policy.MutationRoots.Any(root =>
                {
                    var relativeRoot = Path.GetRelativePath(policy.RepositoryPath, root);
                    var workspaceRoot = relativeRoot == ".." || relativeRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativeRoot)
                        ? root : Path.GetFullPath(relativeRoot, fullRoot);
                    return fullPath.Equals(workspaceRoot, PathComparison) || fullPath.StartsWith(Path.TrimEndingDirectorySeparator(workspaceRoot) + Path.DirectorySeparatorChar, PathComparison);
                })
                || RepositoryPathPolicy.IsProhibited(normalizedPath, policy.ProhibitedPaths, fullRoot)))
            {
                throw new MutationPolicyException("The effective security policy prohibits this mutation path.");
            }

            var fileName = Path.GetFileName(normalizedPath);
            var isSecretPath = normalizedPath.StartsWith(".threadsmith/secrets/", StringComparison.OrdinalIgnoreCase)
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

        if (_currentPolicy is MutationApprovalPolicy.TrustPlan or MutationApprovalPolicy.TrustSession)
        {
            _currentPolicy = MutationApprovalPolicy.ReviewAll;
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

    private void SetLocalChoice(MutationApprovalPolicy? choice)
    {
        var previous = Volatile.Read(ref _consent);
        Volatile.Write(ref _consent, new ConsentState(choice, checked(previous.Revision + 1)));
    }

    private sealed record ConsentState(MutationApprovalPolicy? Choice, long Revision);

    private static MutationApprovalPolicy Restrict(MutationApprovalPolicy selected, MutationApprovalPolicy ceiling)
    {
        if (selected == MutationApprovalPolicy.ReviewAll || ceiling == MutationApprovalPolicy.ReviewAll)
        {
            return MutationApprovalPolicy.ReviewAll;
        }

        return selected == MutationApprovalPolicy.ReviewRisky || ceiling == MutationApprovalPolicy.ReviewRisky
            ? MutationApprovalPolicy.ReviewRisky : selected;
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
