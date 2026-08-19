namespace Threadsmith.Workspaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Threadsmith.Core;

/// <summary>Repository-bound session plan policy with exact repository-identity fencing for persisted trust.</summary>
public sealed class PlanApprovalPolicyService :
    IPlanApprovalPolicy,
    ICommandHandler<GetPlanApprovalPolicyCommand, PlanApprovalPolicy>,
    ICommandHandler<SetPlanApprovalPolicyCommand, PlanApprovalPolicy>
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IUserPlanTrustGrantStore _trustGrantStore;
    private readonly PlanApprovalPolicyPersistence _persistence;
    private readonly IDomainEventStream? _events;
    private readonly IReadOnlyDictionary<string, string?> _configurationBeforeRepository;
    private readonly IReadOnlyDictionary<string, string?> _configurationAfterRepository;
    private PlanApprovalRepositoryBinding? _repositoryBinding;
    private PlanApprovalPolicy _currentPolicy;

    /// <summary>Initializes a new instance of the <see cref="PlanApprovalPolicyService"/> class.</summary>
    /// <param name="configuration">Effective layered configuration.</param>
    /// <param name="repositoryConfigurationPath">Repository configuration path used for persistent trust markers.</param>
    /// <param name="userPlanTrustPath">User-owned trust store required before persistent plan trust is honored.</param>
    /// <param name="events">Optional durable event stream used by command-boundary policy changes.</param>
    public PlanApprovalPolicyService(
        IConfiguration? configuration = null,
        string? repositoryConfigurationPath = null,
        string? userPlanTrustPath = null,
        IDomainEventStream? events = null)
        : this(configuration, CreateDefaultDependencies(repositoryConfigurationPath, userPlanTrustPath), events)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PlanApprovalPolicyService"/> class with explicit storage collaborators.</summary>
    /// <param name="configuration">Effective layered configuration.</param>
    /// <param name="repositoryBinding">Immutable repository binding, or <see langword="null" /> when no repository is active.</param>
    /// <param name="trustGrantStore">User-owned trust-grant store.</param>
    /// <param name="persistence">Cross-store persistence protocol.</param>
    /// <param name="events">Optional durable event stream used by command-boundary policy changes.</param>
    internal PlanApprovalPolicyService(
        IConfiguration? configuration,
        PlanApprovalRepositoryBinding? repositoryBinding,
        IUserPlanTrustGrantStore trustGrantStore,
        PlanApprovalPolicyPersistence persistence,
        IDomainEventStream? events = null)
    {
        ArgumentNullException.ThrowIfNull(trustGrantStore);
        ArgumentNullException.ThrowIfNull(persistence);

        _repositoryBinding = repositoryBinding;
        _trustGrantStore = trustGrantStore;
        _persistence = persistence;
        _events = events;
        var layers = CaptureRebindableConfiguration(
            configuration,
            _repositoryBinding?.ConfigurationPath);
        _configurationBeforeRepository = layers.BeforeRepository;
        _configurationAfterRepository = layers.AfterRepository;
        ApplyConfiguration(configuration, _repositoryBinding, layers.Repository);
    }

    private PlanApprovalPolicyService(
        IConfiguration? configuration,
        PlanApprovalPolicyDependencies dependencies,
        IDomainEventStream? events)
        : this(
            configuration,
            dependencies.RepositoryBinding,
            dependencies.TrustGrantStore,
            dependencies.Persistence,
            events)
    {
    }

    /// <inheritdoc />
    public PlanApprovalPolicy CurrentPolicy => _currentPolicy;

    /// <inheritdoc />
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        PlanApprovalRepositoryBinding binding = PlanApprovalRepositoryBinding.CreateFromRepositoryRoot(repositoryRoot);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryBinding is not null
                && string.Equals(
                    binding.ConfigurationPath,
                    _repositoryBinding.ConfigurationPath,
                    PathComparison))
            {
                return;
            }

            var configuration = BuildConfigurationForRepository(binding.ConfigurationPath);
            var layers = CaptureRebindableConfiguration(configuration, binding.ConfigurationPath);
            ApplyConfiguration(configuration, binding, layers.Repository);
            _repositoryBinding = binding;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetPolicyAsync(
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryBinding is not null && policy != PlanApprovalPolicy.TrustSession)
            {
                await _persistence.PersistAsync(_repositoryBinding, policy, cancellationToken);
            }

            _currentPolicy = policy;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public PlanApprovalDecision Decide(PlanSanityCheckResult result, RepositoryTrustLevel trustLevel)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Risk == PlanRiskClassification.Blocked || result.HasNonRepairableBlockingIssues)
        {
            return new PlanApprovalDecision
            {
                Kind = PlanApprovalDecisionKind.Blocked,
                Policy = _currentPolicy,
                Risk = result.Risk,
                Reason = "Plan sanity checks found a hard guardrail violation.",
            };
        }

        if (result.HasRepairableBlockingIssues)
        {
            return new PlanApprovalDecision
            {
                Kind = PlanApprovalDecisionKind.RequiresReview,
                Policy = _currentPolicy,
                Risk = result.Risk,
                Reason = "Plan requires revision before approval.",
            };
        }

        bool trustedForStrongModes = trustLevel >= RepositoryTrustLevel.TrustedMutation;
        bool autoApprove = _currentPolicy switch
        {
            PlanApprovalPolicy.ReviewAll => false,
            PlanApprovalPolicy.ReviewRisky => result.Risk == PlanRiskClassification.Low,
            PlanApprovalPolicy.TrustSession => result.Risk is PlanRiskClassification.Low or PlanRiskClassification.Moderate,
            PlanApprovalPolicy.AlwaysTrustRepo => trustedForStrongModes
                && result.Risk is (PlanRiskClassification.Low or PlanRiskClassification.Moderate),
            PlanApprovalPolicy.AutoApproveAllValid => trustedForStrongModes,
            _ => throw new InvalidOperationException($"Unsupported plan approval policy '{_currentPolicy}'."),
        };
        return new PlanApprovalDecision
        {
            Kind = autoApprove ? PlanApprovalDecisionKind.AutoApproved : PlanApprovalDecisionKind.RequiresReview,
            Policy = _currentPolicy,
            Risk = result.Risk,
            Reason = autoApprove
                ? $"Policy {_currentPolicy} approved a {result.Risk} risk plan after sanity checks."
                : $"Policy {_currentPolicy} requires manual review for a {result.Risk} risk plan.",
        };
    }

    /// <inheritdoc />
    public Task<PlanApprovalPolicy> HandleAsync(
        GetPlanApprovalPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_currentPolicy);
    }

    /// <inheritdoc />
    public async Task<PlanApprovalPolicy> HandleAsync(
        SetPlanApprovalPolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(command.Policy))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        await SetPolicyAsync(command.Policy, cancellationToken);
        if (_events is not null && command.SessionId is { } sessionId)
        {
            await _events.PublishAsync(
                new PlanApprovalPolicyChanged(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    command.Policy,
                    NormalizeScope(command.Scope)),
                cancellationToken);
        }

        return _currentPolicy;
    }

    private static PlanApprovalPolicyDependencies CreateDefaultDependencies(
        string? repositoryConfigurationPath,
        string? userPlanTrustPath)
    {
        var trustGrantStore = new UserPlanTrustGrantStore(userPlanTrustPath);
        var repositoryStore = new RepositoryPlanApprovalPolicyStore();
        return new PlanApprovalPolicyDependencies(
            PlanApprovalRepositoryBinding.CreateFromConfigurationPath(repositoryConfigurationPath),
            trustGrantStore,
            new PlanApprovalPolicyPersistence(repositoryStore, trustGrantStore));
    }

    private IConfigurationRoot BuildConfigurationForRepository(string configurationPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(_configurationBeforeRepository)
            .AddJsonFile(configurationPath, optional: true)
            .AddInMemoryCollection(_configurationAfterRepository)
            .Build();
    }

    private void ApplyConfiguration(
        IConfiguration? configuration,
        PlanApprovalRepositoryBinding? binding,
        IReadOnlyDictionary<string, string?> repositoryConfiguration)
    {
        const string policyKey = "planning:approvalPolicy";
        string configured = configuration?[policyKey] ?? "reviewAll";
        var parsed = ParsePolicy(configured);
        bool overriddenAfterRepository = _configurationAfterRepository.ContainsKey(policyKey);
        bool repositorySourced = !overriddenAfterRepository
            && repositoryConfiguration.TryGetValue(policyKey, out string? repositoryValue)
            && string.Equals(configured, repositoryValue, StringComparison.OrdinalIgnoreCase);
        if (repositorySourced && parsed == PlanApprovalPolicy.TrustSession)
        {
            configured = _configurationBeforeRepository.TryGetValue(policyKey, out string? trustedValue)
                ? trustedValue ?? "reviewAll"
                : "reviewAll";
            parsed = ParsePolicy(configured);
        }

        if (parsed is PlanApprovalPolicy.AlwaysTrustRepo)
        {
            string? configuredIdentity = configuration?["planning:approvalRepositoryIdentity"];
            if (binding is null
                || !string.Equals(configuredIdentity, binding.RepositoryIdentity, StringComparison.Ordinal)
                || !_trustGrantStore.IsGranted(binding.RepositoryIdentity))
            {
                _currentPolicy = PlanApprovalPolicy.ReviewAll;
                return;
            }
        }

        _currentPolicy = parsed;
    }

    private static PlanApprovalPolicy ParsePolicy(string configured)
    {
        if (!Enum.TryParse(configured, ignoreCase: true, out PlanApprovalPolicy parsed)
            || !Enum.IsDefined(parsed))
        {
            throw new InvalidOperationException($"Unknown plan approval policy '{configured}'.");
        }

        return parsed;
    }

    private static ConfigurationLayers CaptureRebindableConfiguration(
        IConfiguration? configuration,
        string? repositoryConfigurationPath)
    {
        if (configuration is not IConfigurationRoot root)
        {
            var repository = SnapshotRepositoryConfiguration(
                repositoryConfigurationPath);
            return new ConfigurationLayers(
                ExcludeMatchingRepositoryValues(SnapshotEffectiveValues(configuration), repository),
                repository,
                new Dictionary<string, string?>());
        }

        IReadOnlyList<IConfigurationProvider> providers = [.. root.Providers];
        int repositoryProviderIndex = FindRepositoryProviderIndex(providers, repositoryConfigurationPath);
        if (repositoryProviderIndex < 0)
        {
            var repository = SnapshotRepositoryConfiguration(
                repositoryConfigurationPath);
            return new ConfigurationLayers(
                ExcludeMatchingRepositoryValues(SnapshotEffectiveValues(configuration), repository),
                repository,
                new Dictionary<string, string?>());
        }

        return new ConfigurationLayers(
            SnapshotEffectiveValues(providers.Take(repositoryProviderIndex)),
            SnapshotEffectiveValues([providers[repositoryProviderIndex]]),
            SnapshotEffectiveValues(providers.Skip(repositoryProviderIndex + 1)));
    }

    private static IReadOnlyDictionary<string, string?> SnapshotRepositoryConfiguration(
        string? repositoryConfigurationPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryConfigurationPath)
            || !File.Exists(repositoryConfigurationPath))
        {
            return new Dictionary<string, string?>();
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(repositoryConfigurationPath, optional: false)
            .Build();
        return SnapshotEffectiveValues(configuration);
    }

    private static IReadOnlyDictionary<string, string?> ExcludeMatchingRepositoryValues(
        IReadOnlyDictionary<string, string?> effective,
        IReadOnlyDictionary<string, string?> repository)
    {
        var trusted = new Dictionary<string, string?>(effective, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? repositoryValue) in repository)
        {
            if (trusted.TryGetValue(key, out string? effectiveValue)
                && string.Equals(effectiveValue, repositoryValue, StringComparison.Ordinal))
            {
                trusted.Remove(key);
            }
        }

        return trusted;
    }

    private static IReadOnlyDictionary<string, string?> SnapshotEffectiveValues(IConfiguration? configuration)
    {
        return configuration is null
            ? []
            : configuration.AsEnumerable()
                .Where(pair => pair.Value is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> SnapshotEffectiveValues(
        IEnumerable<IConfigurationProvider> providers)
    {
        IConfigurationProvider[] providerList = [.. providers];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providerList)
        {
            AddProviderKeys(provider, parentPath: null, keys);
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            for (int index = providerList.Length - 1; index >= 0; index--)
            {
                if (providerList[index].TryGet(key, out string? value) && value is not null)
                {
                    values[key] = value;
                    break;
                }
            }
        }

        return values;
    }

    private static void AddProviderKeys(
        IConfigurationProvider provider,
        string? parentPath,
        HashSet<string> keys)
    {
        foreach (string child in provider.GetChildKeys([], parentPath))
        {
            string key = parentPath is null
                ? child
                : ConfigurationPath.Combine(parentPath, child);
            if (keys.Add(key))
            {
                AddProviderKeys(provider, key, keys);
            }
        }
    }

    private static int FindRepositoryProviderIndex(
        IReadOnlyList<IConfigurationProvider> providers,
        string? repositoryConfigurationPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryConfigurationPath))
        {
            return -1;
        }

        string normalizedRepositoryConfigurationPath = Path.GetFullPath(repositoryConfigurationPath);
        for (int index = 0; index < providers.Count; index++)
        {
            if (providers[index] is FileConfigurationProvider fileProvider
                && IsSamePath(fileProvider, normalizedRepositoryConfigurationPath))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSamePath(FileConfigurationProvider fileProvider, string normalizedPath)
    {
        string? providerPath = fileProvider.Source.Path;
        if (string.IsNullOrWhiteSpace(providerPath))
        {
            return false;
        }

        if (string.Equals(Path.GetFullPath(providerPath), normalizedPath, PathComparison))
        {
            return true;
        }

        if (fileProvider.Source.FileProvider is not PhysicalFileProvider physicalFileProvider)
        {
            return false;
        }

        string rootedCandidate = Path.GetFullPath(Path.Combine(physicalFileProvider.Root, providerPath));
        return string.Equals(rootedCandidate, normalizedPath, PathComparison);
    }

    private sealed record ConfigurationLayers(
        IReadOnlyDictionary<string, string?> BeforeRepository,
        IReadOnlyDictionary<string, string?> Repository,
        IReadOnlyDictionary<string, string?> AfterRepository);

    private sealed record PlanApprovalPolicyDependencies(
        PlanApprovalRepositoryBinding? RepositoryBinding,
        IUserPlanTrustGrantStore TrustGrantStore,
        PlanApprovalPolicyPersistence Persistence);

    private static string NormalizeScope(string scope)
    {
        return string.IsNullOrWhiteSpace(scope) ? "session" : scope.Trim();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
