namespace Threadsmith.Mcp;

/// <summary>Resource limits shared by MCP discovery, mapping, management, and wire readers.</summary>
public sealed record McpResourceLimits
{
    /// <summary>Maximum resource, prompt, or tool content blocks retained.</summary>
    public int MaximumContentItems { get; init; } = 64;

    /// <summary>Maximum OAuth identity metadata JSON depth.</summary>
    public int MaximumIdentityJsonDepth { get; init; } = 16;

    /// <summary>Maximum resource label characters.</summary>
    public int MaximumResourceLabelCharacters { get; init; } = 1024;

    /// <summary>Maximum configured HTTP header value characters.</summary>
    public int MaximumHeaderValueCharacters { get; init; } = 8192;

    /// <summary>Maximum Arguments.</summary>
    public int MaximumArguments { get; init; } = 32;

    /// <summary>Maximum Argument Characters.</summary>
    public int MaximumArgumentCharacters { get; init; } = 16 * 1024;

    /// <summary>Maximum Capabilities.</summary>
    public int MaximumCapabilities { get; init; } = 256;

    /// <summary>Maximum Failure Characters.</summary>
    public int MaximumFailureCharacters { get; init; } = 1024;

    /// <summary>Maximum Profiles.</summary>
    public int MaximumProfiles { get; init; } = 64;

    /// <summary>Maximum Recent Latency Samples.</summary>
    public int MaximumRecentLatencySamples { get; init; } = 32;

    /// <summary>Maximum Capabilities Per Kind.</summary>
    public int MaximumCapabilitiesPerKind { get; init; } = 256;

    /// <summary>Maximum Content Characters.</summary>
    public int MaximumContentCharacters { get; init; } = 256 * 1024;

    /// <summary>Maximum Description Characters.</summary>
    public int MaximumDescriptionCharacters { get; init; } = 2048;

    /// <summary>Maximum Identity Characters.</summary>
    public int MaximumIdentityCharacters { get; init; } = 4096;

    /// <summary>Maximum Prompt Arguments.</summary>
    public int MaximumPromptArguments { get; init; } = 32;

    /// <summary>Maximum Schema Characters.</summary>
    public int MaximumSchemaCharacters { get; init; } = 64 * 1024;

    /// <summary>Maximum Response Bytes.</summary>
    public int MaximumResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum Line Bytes.</summary>
    public int MaximumLineBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum Name Characters.</summary>
    public int MaximumNameCharacters { get; init; } = 256;

    /// <summary>Maximum Argument Name Characters.</summary>
    public int MaximumArgumentNameCharacters { get; init; } = 128;

    /// <summary>Maximum Profile Id Characters.</summary>
    public int MaximumProfileIdCharacters { get; init; } = 128;

    /// <summary>Maximum Command Characters.</summary>
    public int MaximumCommandCharacters { get; init; } = 4096;

    /// <summary>Maximum Profile Arguments.</summary>
    public int MaximumProfileArguments { get; init; } = 64;

    /// <summary>Maximum Profile Argument Characters.</summary>
    public int MaximumProfileArgumentCharacters { get; init; } = 8192;

    /// <summary>Maximum Environment Variables.</summary>
    public int MaximumEnvironmentVariables { get; init; } = 64;

    /// <summary>Maximum Environment Value Characters.</summary>
    public int MaximumEnvironmentValueCharacters { get; init; } = 32 * 1024;

    /// <summary>Maximum Headers.</summary>
    public int MaximumHeaders { get; init; } = 64;

    /// <summary>Maximum Secret References.</summary>
    public int MaximumSecretReferences { get; init; } = 64;

    /// <summary>Maximum Secret Reference Characters.</summary>
    public int MaximumSecretReferenceCharacters { get; init; } = 512;

    /// <summary>Maximum Scopes.</summary>
    public int MaximumScopes { get; init; } = 64;

    /// <summary>Maximum Scope Characters.</summary>
    public int MaximumScopeCharacters { get; init; } = 256;

    /// <summary>Maximum Client Id Characters.</summary>
    public int MaximumClientIdCharacters { get; init; } = 1024;

    /// <summary>Maximum Standard Error Line Characters.</summary>
    public int MaximumStandardErrorLineCharacters { get; init; } = 8192;

    /// <summary>Maximum advertised OAuth authorization servers.</summary>
    public int MaximumOAuthAuthorizationServers { get; init; } = 4;

    /// <summary>Maximum OAuth metadata or identity-response bytes.</summary>
    public int MaximumOAuthMetadataBytes { get; init; } = 65536;

    /// <summary>Maximum loopback OAuth request-header bytes.</summary>
    public int MaximumCallbackHeaderBytes { get; init; } = 32768;

    /// <summary>Maximum loopback OAuth request headers.</summary>
    public int MaximumCallbackHeaders { get; init; } = 64;

    /// <summary>Maximum loopback OAuth request-line bytes.</summary>
    public int MaximumCallbackLineBytes { get; init; } = 8192;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumContentItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIdentityJsonDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResourceLabelCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHeaderValueCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOAuthAuthorizationServers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOAuthMetadataBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumOAuthMetadataBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCallbackHeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCallbackHeaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCallbackLineBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumCallbackLineBytes, Array.MaxLength);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArgumentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCapabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFailureCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProfiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecentLatencySamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCapabilitiesPerKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumContentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDescriptionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIdentityCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPromptArguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSchemaCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArgumentNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProfileIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCommandCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProfileArguments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProfileArgumentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEnvironmentVariables);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEnvironmentValueCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHeaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSecretReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSecretReferenceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumScopes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumScopeCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumClientIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumStandardErrorLineCharacters);
    }
}
