namespace Threadsmith.Tools;

/// <summary>Trusted resource limits for static secret resolution.</summary>
public sealed record SecretResourceLimits
{
    /// <summary>Maximum bytes in a JSON secret store.</summary>
    public int MaximumStoreBytes { get; init; } = 65536;

    /// <summary>Maximum total JSON secret-store properties.</summary>
    public int MaximumProperties { get; init; } = 512;

    /// <summary>Maximum JSON secret-store nesting depth.</summary>
    public int MaximumJsonDepth { get; init; } = 16;

    /// <summary>Maximum resolved secret-value characters, for every provider.</summary>
    public int MaximumValueCharacters { get; init; } = 65536;

    /// <summary>Default deadline for each secret-provider attempt.</summary>
    public int ProviderTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Rejects nonpositive limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumStoreBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProperties);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumJsonDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumValueCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProviderTimeoutMilliseconds);
    }
}
