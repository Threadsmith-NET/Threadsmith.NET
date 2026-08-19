namespace Threadsmith.Execution;

/// <summary>
/// Host-owned operational limits for the execution subsystem, sourced from layered
/// configuration (strategy §21.1). Bound once at the composition root and injected into
/// the execution consumers. Defaults define the current compiled-in values.
/// </summary>
public sealed record ExecutionLimits
{
    /// <summary>
    /// Maximum number of model continuation rounds within one plan-generation cycle before
    /// the host stops the model (strategy §11). Historical default: 16.
    /// </summary>
    public int MaxModelRounds { get; init; } = 16;

    /// <summary>
    /// Maximum initial evidence-collection rounds that advertise repository inspection tools.
    /// Later rounds retain only <c>propose_plan</c> so planning converges instead of exhausting
    /// the complete continuation budget. Default: 4.
    /// </summary>
    public int MaxPlanningToolRounds { get; init; } = 4;

    /// <summary>
    /// Maximum malformed <c>propose_plan</c> argument repair attempts before the host fails closed.
    /// Default: 3.
    /// </summary>
    public int MaxPlanProposalRepairAttempts { get; init; } = 3;

    /// <summary>
    /// Maximum automatic plan-revision attempts after cheap plan sanity checks find repairable
    /// structured-scope issues before any approval prompt. Default: 3.
    /// </summary>
    public int MaxPlanRevisionRepairAttempts { get; init; } = 3;

    /// <summary>
    /// Maximum mutation-proposal attempts after host validation/staging rejects repairable
    /// model-authored text evidence. Default: 3.
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