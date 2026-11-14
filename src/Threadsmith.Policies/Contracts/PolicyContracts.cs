namespace Threadsmith.Policies.Contracts;

using System.Collections.Immutable;

/// <summary>Identifies a typed, host-provided operation domain.</summary>
public abstract record ActionFacts(string Domain);

/// <summary>Contains source command facts established by a shell adapter; variable arguments retain submitted syntax and shell commands have an empty resolved executable.</summary>
public sealed record CommandFacts(
    string Platform,
    string Shell,
    string ExecutableName,
    string ResolvedExecutable,
    string CommandKind,
    ImmutableArray<string> Arguments,
    string WorkingDirectory) : ActionFacts(CommandPolicyNames.Domain);

/// <summary>Selects a typed scalar command fact.</summary>
public enum PolicyFact
{
    /// <summary>The actual host platform.</summary>
    Platform,

    /// <summary>The actual launch dialect.</summary>
    Shell,

    /// <summary>The executable basename or literal shell command name.</summary>
    ExecutableName,

    /// <summary>The absolute external target, or empty for a shell command.</summary>
    ResolvedExecutable,

    /// <summary>The bound command kind.</summary>
    CommandKind,

    /// <summary>The normalized execution directory.</summary>
    WorkingDirectory,
}

/// <summary>Describes one closed-catalog matcher. Validation rejects incompatible fields.</summary>
public sealed record PolicyMatcher
{
    /// <summary>Gets the stable catalog discriminator.</summary>
    public required PolicyMatcherType Type { get; init; }

    /// <summary>Gets the scalar fact selected by a text matcher.</summary>
    public PolicyFact? Fact { get; init; }

    /// <summary>Gets the exact text, executable identity, or bounded pattern.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the finite alternatives or allowed path roots.</summary>
    public ImmutableArray<string> Values { get; init; } = [];

    /// <summary>Gets explicit case-insensitive text matching where the fact permits it.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Gets the nonempty composition branches.</summary>
    public ImmutableArray<PolicyMatcher> Matchers { get; init; } = [];
}

/// <summary>Defines a stable rule; overrides apply only to matching ordinary rules.</summary>
public sealed record PolicyRule
{
    /// <summary>Gets the stable source-layer identity.</summary>
    public required string Id { get; init; }

    /// <summary>Gets AND-composed top-level matchers.</summary>
    public required ImmutableArray<PolicyMatcher> Matchers { get; init; }

    /// <summary>Gets the configured outcome.</summary>
    public required PolicyOutcome Policy { get; init; }

    /// <summary>Gets explicitly replaced matching rule identities.</summary>
    public ImmutableArray<string> Overrides { get; init; } = [];

    /// <summary>Gets regex searches that deny when any matches literal arguments joined with single spaces, excluding the command name.</summary>
    public ImmutableArray<string> DenyArgumentsMatching { get; init; } = [];
}

/// <summary>Records an exact persistent approval without sensitive command or environment values.</summary>
public sealed record PolicyGrant(string Id, string ContextFingerprint, string RuleFingerprint);

/// <summary>Contains one platform-common or shell-specific source layer.</summary>
public sealed record PolicyProfile
{
    /// <summary>Gets added or completely replaced ordinary rules.</summary>
    public ImmutableArray<PolicyRule> Rules { get; init; } = [];

    /// <summary>Gets inherited ordinary rules explicitly removed by this layer.</summary>
    public ImmutableArray<string> DisabledRuleIds { get; init; } = [];

    /// <summary>Gets exact user-owned remembered approvals.</summary>
    public ImmutableArray<PolicyGrant> AlwaysAllow { get; init; } = [];

    /// <summary>Gets machine-only constraints which cannot provide ordinary permission.</summary>
    public ImmutableArray<PolicyRule> Enforced { get; init; } = [];

    /// <summary>Gets whether membership in the machine constraint set is mandatory.</summary>
    public bool RequireEnforcedMatch { get; init; }

    /// <summary>Gets distinct shell profiles; no dialect is inferred from another profile.</summary>
    public ImmutableDictionary<string, PolicyProfile> Shells { get; init; } = [];
}

/// <summary>Defines a versioned machine or user policy document.</summary>
public sealed record PolicyDocument
{
    /// <summary>Gets the supported schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets source-wide rules applied before platform and shell profiles.</summary>
    public PolicyProfile Common { get; init; } = new();

    /// <summary>Gets platform profiles keyed by windows, macos, or linux.</summary>
    public ImmutableDictionary<string, PolicyProfile> Platforms { get; init; } = [];
}

/// <summary>Contains validated effective authority for one actual platform and shell.</summary>
public sealed record PolicySnapshot(
    string Platform,
    string Shell,
    string Fingerprint,
    ImmutableArray<PolicyRule> Rules,
    ImmutableArray<PolicyRule> Enforced,
    bool RequireEnforcedMatch,
    ImmutableArray<PolicyGrant> AlwaysAllow,
    ImmutableDictionary<string, string> Sources)
{
    /// <summary>Gets the expected source revision for coordinated configuration transformations.</summary>
    public string ConfigurationRevision { get; init; } = string.Empty;
}

/// <summary>Contains a host-only decision; never serialize it as a model tool result.</summary>
public sealed record PolicyDecision(
    bool IsAllowed,
    bool RequiresApproval,
    string ReasonCode,
    PolicyOutcome? Outcome,
    ImmutableArray<string> RuleIds,
    string RuleFingerprint,
    ImmutableArray<PolicyApprovalChoice> Choices);

/// <summary>Contains the exact context and choices displayed only to the approving user.</summary>
public sealed record PolicyApprovalRequest(
    Guid RequestId,
    string Requester,
    string Summary,
    string ContextFingerprint,
    string AuthorityFingerprint,
    long SessionEpoch,
    ImmutableArray<string> RuleIds,
    ImmutableArray<PolicyApprovalChoice> Choices);

/// <summary>Returns a selection, not an authorization token.</summary>
public sealed record PolicyApprovalResponse(Guid RequestId, PolicyApprovalChoice Choice);
