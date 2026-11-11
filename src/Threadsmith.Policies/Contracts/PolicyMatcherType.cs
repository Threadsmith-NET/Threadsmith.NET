namespace Threadsmith.Policies.Contracts;

/// <summary>Identifies the closed set of host-owned matcher implementations.</summary>
public enum PolicyMatcherType
{
    /// <summary>Matches one scalar value.</summary>
    Exact,

    /// <summary>Matches a finite set of scalar values.</summary>
    ValueSet,

    /// <summary>Matches an executable identity or literal shell command name.</summary>
    Executable,

    /// <summary>Matches a normalized path within configured roots.</summary>
    Path,

    /// <summary>Matches a full scalar value using a glob.</summary>
    Glob,

    /// <summary>Matches a full scalar value using a bounded regular expression.</summary>
    Regex,

    /// <summary>Requires every child matcher.</summary>
    AllOf,

    /// <summary>Requires one complete child branch.</summary>
    AnyOf,
}
