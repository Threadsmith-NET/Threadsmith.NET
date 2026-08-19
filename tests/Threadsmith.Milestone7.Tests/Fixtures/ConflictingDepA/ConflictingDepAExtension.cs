namespace Threadsmith.Tests.Fixtures.ConflictingDepA;

using Threadsmith.Extensions.Abstractions;
using Threadsmith.PrivateLib;

/// <summary>Fixture bundling PrivateLib v1.0; surfaces the resolved private version in its tool description.</summary>
public sealed class ConflictingDepAExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.conflicting-dep-a",
        Name = "ConflictingDepA Fixture",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider"],
        Permissions = ["repository-read"],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string version = VersionSource.GetVersion();
        context.Capabilities.RegisterTool(new ProbeToolCapability(version));
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}

/// <summary>A tool whose description carries the resolved private-dependency version.</summary>
internal sealed class ProbeToolCapability : IToolCapability
{
    /// <summary>Initializes a new instance of the <see cref="ProbeToolCapability"/> class.</summary>
    /// <param name="version">The resolved private dependency version.</param>
    public ProbeToolCapability(string version)
    {
        Version = version;
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "conflicting_dep_a_probe",
        Kind = CapabilityKind.Tool,
        DisplayName = "ConflictingDepA Probe",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; } = new()
    {
        Id = "conflicting_dep_a_probe",
        Version = "1.0",
        Description = "private-version:unset",
        Category = ExtensionToolCategory.RepositoryInspection,
        InputSchema = new ExtensionToolSchema("ProbeInput", 1, "{}"),
        OutputSchema = new ExtensionToolSchema("ProbeOutput", 1, "{}"),
        RequiredTrust = ExtensionTrustRequirement.None,
        RequiredApproval = ExtensionApprovalRequirement.None,
        SideEffect = ExtensionToolSideEffect.ReadOnly,
        Idempotency = ExtensionToolIdempotency.Idempotent,
        SupportsCancellation = true,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
        => [];

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => [];

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson) => null;

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson) => [];

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        return Task.FromResult(new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = $"{{\"version\":\"{Version}\"}}",
            Sources = [new ExtensionProvenanceSource("extension", "conflicting_dep_a_probe")],
        });
    }

    private string Version { get; }
}