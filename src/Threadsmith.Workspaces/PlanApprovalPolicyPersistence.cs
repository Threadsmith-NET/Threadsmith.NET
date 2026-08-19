namespace Threadsmith.Workspaces;

using Threadsmith.Core;

/// <summary>Coordinates repository plan-policy markers with user-owned persistent trust grants.</summary>
internal sealed class PlanApprovalPolicyPersistence
{
    private readonly IRepositoryPlanApprovalPolicyStore _repositoryStore;
    private readonly IUserPlanTrustGrantStore _trustGrantStore;

    /// <summary>Initializes a new instance of the <see cref="PlanApprovalPolicyPersistence"/> class.</summary>
    /// <param name="repositoryStore">Repository policy marker store.</param>
    /// <param name="trustGrantStore">User-owned exact repository grant store.</param>
    public PlanApprovalPolicyPersistence(
        IRepositoryPlanApprovalPolicyStore repositoryStore,
        IUserPlanTrustGrantStore trustGrantStore)
    {
        ArgumentNullException.ThrowIfNull(repositoryStore);
        ArgumentNullException.ThrowIfNull(trustGrantStore);
        _repositoryStore = repositoryStore;
        _trustGrantStore = trustGrantStore;
    }

    /// <summary>Persists one policy change using the fail-closed cross-store ordering.</summary>
    /// <param name="binding">Immutable repository binding.</param>
    /// <param name="policy">Policy to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PersistAsync(
        PlanApprovalRepositoryBinding binding,
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!Enum.IsDefined(policy) || policy == PlanApprovalPolicy.TrustSession)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy == PlanApprovalPolicy.AlwaysTrustRepo)
        {
            await PersistAlwaysTrustRepoAsync(binding, cancellationToken);
            return;
        }

        await PersistRepositoryDefaultAsync(binding, policy, cancellationToken);
    }

    private async Task PersistAlwaysTrustRepoAsync(
        PlanApprovalRepositoryBinding binding,
        CancellationToken cancellationToken)
    {
        await _trustGrantStore.GrantAsync(binding.RepositoryIdentity, cancellationToken);
        try
        {
            await _repositoryStore.WritePolicyAsync(
                binding,
                PlanApprovalPolicy.AlwaysTrustRepo,
                cancellationToken);
        }
        catch (Exception exception)
        {
            await RevokeAfterFailedRepositoryWriteAsync(binding.RepositoryIdentity, exception);
            throw;
        }
    }

    private async Task PersistRepositoryDefaultAsync(
        PlanApprovalRepositoryBinding binding,
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken)
    {
        await _trustGrantStore.RevokeAsync(binding.RepositoryIdentity, CancellationToken.None);
        await _repositoryStore.WritePolicyAsync(binding, policy, cancellationToken);
    }

    private async Task RevokeAfterFailedRepositoryWriteAsync(
        string repositoryIdentity,
        Exception repositoryException)
    {
        try
        {
            await _trustGrantStore.RevokeAsync(repositoryIdentity, CancellationToken.None);
        }
        catch (Exception compensationException)
        {
            throw new AggregateException(
                "Persisting persistent plan approval trust failed, and compensating trust-grant revocation also failed.",
                repositoryException,
                compensationException);
        }
    }
}
