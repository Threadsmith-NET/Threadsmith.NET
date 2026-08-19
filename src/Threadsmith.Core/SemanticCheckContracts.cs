namespace Threadsmith.Core;

/// <summary>Identifies the mutation-validation boundary that owns a semantic check.</summary>
public enum SemanticCheckPhase
{
    /// <summary>The phase is unknown, including restored legacy-adjacent data.</summary>
    Unknown,

    /// <summary>Read-only semantic screening before a mutation is staged or approved.</summary>
    PreMutation,

    /// <summary>Semantic diagnostic baseline capture before a committed mutation is applied.</summary>
    Baseline,

    /// <summary>Semantic diagnostics after a committed mutation is applied.</summary>
    PostMutation,
}

/// <summary>Identifies the normalized outcome of one semantic check activity.</summary>
public enum SemanticCheckOutcome
{
    /// <summary>No reliable outcome is available.</summary>
    Unknown,

    /// <summary>The check completed without blocking diagnostics or degraded evidence.</summary>
    Completed,

    /// <summary>The check ran and found blocking diagnostics.</summary>
    Failed,

    /// <summary>The check completed with explicit degraded or incomplete evidence.</summary>
    Degraded,

    /// <summary>The check was not applicable or was skipped by an earlier semantic boundary.</summary>
    Skipped,

    /// <summary>The caller cancelled the check.</summary>
    Cancelled,
}
