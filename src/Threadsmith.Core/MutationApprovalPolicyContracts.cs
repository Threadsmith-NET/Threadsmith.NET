namespace Threadsmith.Core;

/// <summary>Controls whether staged mutation sets require an explicit user approval.</summary>
public enum MutationApprovalPolicy
{
    /// <summary>Requires approval for every mutation set.</summary>
    ReviewAll,

    /// <summary>Automatically approves ordinary edits and pauses for risk indicators.</summary>
    ReviewRisky,

    /// <summary>Automatically approves mutations contained by the accepted plan.</summary>
    TrustPlan,

    /// <summary>Automatically approves in-repository mutations for the current session.</summary>
    TrustSession,

    /// <summary>Persistently and automatically approves in-repository mutations for this repository.</summary>
    AlwaysTrustRepo,
}

/// <summary>Host-classified risk indicators for one exact staged mutation preview.</summary>
public sealed record MutationRiskAssessment
{
    /// <summary>Whether the set deletes at least one file.</summary>
    public bool HasDeletions { get; init; }

    /// <summary>Whether the set moves at least one file.</summary>
    public bool HasMoves { get; init; }

    /// <summary>Whether the set changes build, application, or package configuration.</summary>
    public bool HasConfigChanges { get; init; }

    /// <summary>Whether the set changes package dependency declarations.</summary>
    public bool HasDependencyChanges { get; init; }

    /// <summary>Whether the exact preview exceeds the configured changed-line threshold.</summary>
    public bool HasLargeDiff { get; init; }

    /// <summary>Whether any target cannot be confined to the repository root.</summary>
    public bool HasOutsideRepoChanges { get; init; }

    /// <summary>Number of distinct files targeted by the set.</summary>
    public int FileCount { get; init; }

    /// <summary>Total added and removed lines in the exact preview.</summary>
    public int TotalLinesChanged { get; init; }

    /// <summary>Whether any indicator requires review under <see cref="MutationApprovalPolicy.ReviewRisky"/>.</summary>
    public bool IsRisky => HasDeletions
        || HasMoves
        || HasConfigChanges
        || HasDependencyChanges
        || HasLargeDiff
        || HasOutsideRepoChanges;
}

/// <summary>Determines whether staged mutations require approval and enforces invariant guardrails.</summary>
public interface IMutationApprovalPolicy
{
    /// <summary>Gets the effective policy for the current process session.</summary>
    MutationApprovalPolicy CurrentPolicy { get; }

    /// <summary>Gets the changed-line threshold used to classify a large diff.</summary>
    int LargeDiffThreshold { get; }

    /// <summary>Rebinds repository-scoped policy state and persistence to the active repository.</summary>
    Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the session policy and persists or revokes repository-wide trust when required.</summary>
    Task SetPolicyAsync(
        MutationApprovalPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>Determines whether the classified mutation set requires explicit approval.</summary>
    bool RequiresApproval(MutationRiskAssessment risk, bool isWithinPlan);

    /// <summary>Rejects a mutation set that violates policy-invariant guardrails.</summary>
    void Validate(MutationSet mutations, string repositoryRoot);
}

/// <summary>Thrown when a mutation violates a policy-invariant hard guardrail.</summary>
public sealed class MutationPolicyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="MutationPolicyException"/> class.</summary>
    public MutationPolicyException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationPolicyException"/> class.</summary>
    /// <param name="message">Violation explanation.</param>
    public MutationPolicyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="MutationPolicyException"/> class.</summary>
    /// <param name="message">Violation explanation.</param>
    /// <param name="innerException">Exception that caused the policy violation.</param>
    public MutationPolicyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
