namespace Threadsmith.Policies.Contracts;

/// <summary>Defines stable decision identifiers for host-owned diagnostics.</summary>
public static class PolicyReasons
{
    /// <summary>The facts and selected profile disagree.</summary>
    public const string ProfileMismatch = "PolicyProfileMismatch";

    /// <summary>No ordinary rule grants authority.</summary>
    public const string Unmatched = "PolicyUnmatched";

    /// <summary>Remaining ordinary rules require different outcomes.</summary>
    public const string Conflict = "PolicyConflict";

    /// <summary>No machine constraint admits an action in a closed set.</summary>
    public const string ClosedSet = "PolicyClosedSet";

    /// <summary>A rule explicitly denies the action.</summary>
    public const string ExplicitDenial = "PolicyExplicitDenial";

    /// <summary>The action has a resolved ordinary outcome.</summary>
    public const string Matched = "PolicyMatched";

    /// <summary>Authorization could not be safely evaluated.</summary>
    public const string AnalysisError = "PolicyAnalysisError";

    /// <summary>The serialized matcher kind is not in the closed catalog.</summary>
    public const string UnknownMatcher = "PolicyUnknownMatcher";
}
