namespace Threadsmith.Execution;

using Threadsmith.Core;

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

    /// <summary>Maximum retained tool calls in one conversation loop; zero disables this limit.</summary>
    public int MaxRetainedToolCalls { get; init; } = 256;

    /// <summary>
    /// Maximum characters of structured (JSON) mutation output the host accumulates from a
    /// streaming model before rejecting it as malformed. Zero or less disables this configured cap.
    /// Historical default: 8 MiB.
    /// </summary>
    public int MaxStructuredOutputCharacters { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum characters of a tool result preview retained in the in-memory session
    /// projection. Historical default: 4096.
    /// </summary>
    public int MaxToolResultPreviewCharacters { get; init; } = 4096;

    /// <summary>Structured plan admission limits.</summary>
    public PlanResourceLimits Plan { get; init; } = new();

    /// <summary>Maximum source references retained for model context provenance.</summary>
    public int MaxSourceFrontierEntries { get; init; } = 256;

    /// <summary>Maximum retained structured plan sanity issues.</summary>
    public int MaxPlanSanityIssues { get; init; } = 32;

    /// <summary>Maximum characters accepted in a steering submission.</summary>
    public int MaxSteeringCharacters { get; init; } = 100000;

    /// <summary>Maximum pending child display fragments.</summary>
    public int MaxAgentDisplayFragments { get; init; } = 256;

    /// <summary>Maximum characters in a child display fragment.</summary>
    public int MaxAgentDisplayFragmentCharacters { get; init; } = 4096;

    /// <summary>Maximum child display line retained before complete-line sanitization.</summary>
    public int MaxAgentDisplayLineCharacters { get; init; } = 16384;

    /// <summary>Validates positive buffer limits and structured-plan limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSourceFrontierEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPlanSanityIssues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSteeringCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAgentDisplayFragments);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAgentDisplayFragmentCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAgentDisplayLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetainedToolCalls);
        Plan.Validate();
    }

    /// <summary>The compiled-in defaults used when no configuration is supplied.</summary>
    public static ExecutionLimits Default { get; } = new();
}
