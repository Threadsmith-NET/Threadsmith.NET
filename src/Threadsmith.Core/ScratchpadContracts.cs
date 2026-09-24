namespace Threadsmith.Core;

/// <summary>Why the optional session scratchpad is unavailable.</summary>
public enum ScratchpadDisabledReason
{
    /// <summary>The capability is active.</summary>
    None,

    /// <summary>No path was configured, or a higher layer explicitly disabled it.</summary>
    NotConfigured,

    /// <summary>The configured value or target was unsafe or invalid.</summary>
    InvalidConfiguration,

    /// <summary>An external configured directory did not already exist.</summary>
    ExternalDirectoryMissing,

    /// <summary>The required built-in writer was unavailable.</summary>
    WriteFileUnavailable,

    /// <summary>The directory could not be cleared safely.</summary>
    CleanupFailed,
}

/// <summary>Immutable scratchpad authority captured for an active session.</summary>
public sealed record ScratchpadSessionCapability
{
    /// <summary>Disabled capability used when no scratchpad is available.</summary>
    public static ScratchpadSessionCapability Disabled(ScratchpadDisabledReason reason) => new()
    {
        DisabledReason = reason,
    };

    /// <summary>Whether eligible built-in tools may access the root.</summary>
    public bool IsActive { get; init; }

    /// <summary>Closed reason when inactive.</summary>
    public ScratchpadDisabledReason DisabledReason { get; init; } = ScratchpadDisabledReason.NotConfigured;

    /// <summary>Normalized absolute enforcement root.</summary>
    public string? RootPath { get; init; }

    /// <summary>Path rendered to the model.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Whether the root is strictly beneath the repository root.</summary>
    public bool IsInsideRepository { get; init; }

    /// <summary>Repository identity for which this authority was created.</summary>
    public string? RepositoryIdentity { get; init; }

    /// <summary>Monotonic activation generation.</summary>
    public long Generation { get; init; }
}

/// <summary>Supplies the current host-owned scratchpad capability.</summary>
public interface IScratchpadSessionCapabilityProvider
{
    /// <summary>Returns one immutable current snapshot.</summary>
    ScratchpadSessionCapability Current { get; }
}
