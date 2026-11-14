namespace Threadsmith.Tools;

/// <summary>Contains explicitly supplied inputs for an isolated candidate check.</summary>
public sealed record CommandPolicyValidationRequest
{
    /// <summary>Gets the optional candidate policy subtree, not a complete configuration document.</summary>
    public string? PolicyJson { get; init; }

    /// <summary>Gets the intended candidate source: machine or user.</summary>
    public required string Layer { get; init; }

    /// <summary>Gets the actual host platform: windows, macos, or linux.</summary>
    public required string Platform { get; init; }

    /// <summary>Gets a supported shell basename or absolute shell path.</summary>
    public required string Shell { get; init; }

    /// <summary>Gets the explicit absolute working directory for every example.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the complete example set with stable unique IDs.</summary>
    public required IReadOnlyList<CommandPolicyValidationExample> Examples { get; init; }

    /// <summary>Gets an optional supplied list of outer-executable basenames.</summary>
    public IReadOnlyList<string>? AllowedExecutables { get; init; }
}

/// <summary>Identifies one complete command and its expected complete-command outcome.</summary>
public sealed record CommandPolicyValidationExample(string Id, string Command, string ExpectedOutcome);

/// <summary>Projects supplied-command facts and candidate results, never live policy authority.</summary>
public sealed record CommandPolicyValidationAction(
    string BindingId,
    string ExecutableName,
    string ResolvedExecutable,
    string CommandKind,
    IReadOnlyList<string> Arguments,
    string? ActualOutcome,
    string ReasonCode);

/// <summary>Reports preparation and isolated candidate evaluation of one complete command.</summary>
public sealed record CommandPolicyValidationExampleResult(
    string Id,
    string Command,
    string ExpectedOutcome,
    string PreparationStatus,
    string ReasonCode,
    string? ActualOutcome,
    bool Matched,
    string? OuterExecutable,
    string? OuterResolvedExecutable,
    string OuterEligibility,
    IReadOnlyList<CommandPolicyValidationAction> Actions);

/// <summary>
/// Identifies an isolated observation. CandidateDigest hashes exact candidate UTF-8 bytes.
/// ExampleSetId binds examples, evaluation context, supplied outer list, and observed bindings.
/// </summary>
public sealed record CommandPolicyValidationResult(
    string? CandidateDigest,
    string ExampleSetId,
    string Platform,
    string Shell,
    string WorkingDirectory,
    string ParseStatus,
    string CompileStatus,
    string CoverageStatus,
    string Status,
    bool IsValidated,
    int ExampleCount,
    IReadOnlyList<CommandPolicyValidationExampleResult> Results,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Diagnostics);
