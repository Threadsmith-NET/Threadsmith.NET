namespace Threadsmith.Tests.Fixtures.InvalidSecondTool;

using Threadsmith.Extensions.Abstractions;

/// <summary>Registers a valid tool followed by one whose output bound is invalid.</summary>
public sealed class InvalidSecondToolExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.invalid-second-tool",
        Name = "Invalid Second Tool Fixture",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider"],
        Permissions = [],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Capabilities.RegisterTool(new FixtureTool("valid_capability", "valid_before_failure", 1024));
        context.Capabilities.RegisterTool(new FixtureTool("invalid_capability", "invalid_after_valid", 0));
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}

/// <summary>Minimal metadata-only tool used to exercise generation publication.</summary>
public sealed class FixtureTool : IToolCapability
{
    /// <summary>Initializes a new instance of the <see cref="FixtureTool"/> class.</summary>
    public FixtureTool(string descriptorId, string definitionId, int maximumOutputBytes)
    {
        Descriptor = new CapabilityDescriptor
        {
            Id = descriptorId,
            Kind = CapabilityKind.Tool,
            DisplayName = descriptorId,
        };
        Definition = new ExtensionToolDefinition
        {
            Id = definitionId,
            Version = "1.0",
            Description = "Generation publication fixture tool.",
            Category = ExtensionToolCategory.RepositoryInspection,
            InputSchema = new ExtensionToolSchema("Input", 1, "{}"),
            OutputSchema = new ExtensionToolSchema("Output", 1, "{}"),
            SupportsCancellation = true,
            MaximumOutputBytes = maximumOutputBytes,
        };
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context) => [];

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
        => Task.FromResult(new ExtensionToolResult { Succeeded = true });
}
