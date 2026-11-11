namespace Threadsmith.Policies.Contracts;

/// <summary>Defines the configured authorization requirement for a matched action.</summary>
public enum PolicyOutcome
{
    /// <summary>Requires only the remaining host checks.</summary>
    Allow,

    /// <summary>Rejects execution without offering approval.</summary>
    AlwaysDeny,

    /// <summary>Requires a new approval for each invocation.</summary>
    ApprovalAlwaysRequired,

    /// <summary>Accepts an exact live-session grant or a new approval.</summary>
    ApprovalRequiredSession,

    /// <summary>Accepts an exact session or persistent grant or a new approval.</summary>
    ApprovalRequired,
}

/// <summary>Defines a host-permitted response to an exact approval request.</summary>
public enum PolicyApprovalChoice
{
    /// <summary>Rejects the request.</summary>
    Deny,

    /// <summary>Authorizes one final execution admission.</summary>
    Once,

    /// <summary>Remembers the exact context in the live session.</summary>
    Session,

    /// <summary>Persists the exact context in user configuration.</summary>
    Always,
}
