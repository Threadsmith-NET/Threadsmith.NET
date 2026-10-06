namespace Threadsmith.Core;

/// <summary>Controls whether structured implementation plans require explicit user approval.</summary>
public enum PlanApprovalPolicy
{
    /// <summary>Requires approval for every valid implementation plan.</summary>
    ReviewAll,

    /// <summary>Automatically approves low-risk valid plans and prompts for riskier plans.</summary>
    ReviewRisky,

    /// <summary>Automatically approves low- and moderate-risk valid plans for the current session.</summary>
    TrustSession,

    /// <summary>Persistently approves low- and moderate-risk valid plans for this repository.</summary>
    AlwaysTrustRepo,

    /// <summary>Automatically approves every valid non-blocked plan after explicit trusted selection.</summary>
    AutoApproveAllValid,
}

/// <summary>Host-owned risk classification assigned after plan sanity checks.</summary>
public enum PlanRiskClassification
{
    /// <summary>Small, exact, and ordinary source, test, or documentation scope.</summary>
    Low,

    /// <summary>Broader or partially lifecycle/configuration-related scope that remains mechanically checkable.</summary>
    Moderate,

    /// <summary>Broad, destructive, dependency, generated, binary, secret-adjacent, or policy-sensitive scope.</summary>
    High,

    /// <summary>A hard guardrail prevents the plan from being approved or shown for ordinary review.</summary>
    Blocked,
}
