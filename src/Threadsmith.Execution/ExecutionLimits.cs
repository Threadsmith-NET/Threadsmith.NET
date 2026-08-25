namespace Threadsmith.Execution;

/// <summary>
/// Host-owned operational limits for the execution subsystem, sourced from layered
/// configuration (strategy §21.1). Bound once at the composition root and injected into
/// the execution consumers. Defaults define the current compiled-in values.
/// </summary>
public sealed record ExecutionLimits
{
    /// <summary>Default model continuation-round limit. Zero means disabled.</summary>
    public const int DefaultMaxModelRounds = 0;

    /// <summary>Default separate evidence-collection inspection-tool cutoff. Zero means disabled.</summary>
    public const int DefaultMaxPlanningToolRounds = 0;

    /// <summary>
    /// Optional maximum number of model continuation rounds within one plan-generation cycle.
    /// A value of zero or less disables the separate continuation-round cutoff so cancellation,
    /// tool policy, output accounting, and user-controlled budgets govern the run. Default: 0.
    /// </summary>
    public int MaxModelRounds { get; init; } = DefaultMaxModelRounds;

    /// <summary>
    /// Optional maximum initial evidence-collection rounds that advertise repository inspection tools.
    /// A value of zero or less disables the separate cutoff so read-only exploration can use the
    /// full continuation budget. Default: 0.
    /// </summary>
    public int MaxPlanningToolRounds { get; init; } = DefaultMaxPlanningToolRounds;

    /// <summary>
    /// Maximum active-turn corrective messages for recoverable malformed or invalid model requests.
    /// Default: 3.
    /// </summary>
    public int MaxCorrectiveTurns { get; init; } = 3;

    /// <summary>
    /// Legacy malformed <c>propose_plan</c> repair budget retained for compatibility until tests and
    /// checkpoint-facing contracts finish migrating to <see cref="MaxCorrectiveTurns"/>.
    /// </summary>
    public int MaxPlanProposalRepairAttempts { get; init; } = 3;

    /// <summary>
    /// Legacy plan-revision repair budget retained for compatibility until plan-sanity repair migrates.
    /// </summary>
    public int MaxPlanRevisionRepairAttempts { get; init; } = 3;

    /// <summary>
    /// Legacy mutation-proposal repair budget retained for compatibility until mutation correction migrates.
    /// </summary>
    public int MaxMutationProposalRepairAttempts { get; init; } = 3;

    /// <summary>
    /// Maximum characters of structured (JSON) mutation output the host accumulates from a
    /// streaming model before rejecting it as malformed. Historical default: 8 MiB.
    /// </summary>
    public int MaxStructuredOutputCharacters { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum characters of a tool result preview retained in the in-memory session
    /// projection. Historical default: 4096.
    /// </summary>
    public int MaxToolResultPreviewCharacters { get; init; } = 4096;

    /// <summary>The compiled-in defaults used when no configuration is supplied.</summary>
    public static ExecutionLimits Default { get; } = new();
}