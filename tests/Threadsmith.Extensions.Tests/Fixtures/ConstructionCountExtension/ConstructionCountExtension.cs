namespace Threadsmith.Tests.Fixtures.ConstructionCount;

using System.Text.Json;
using Threadsmith.Extensions.Abstractions;

/// <summary>
/// A minimal extension that increments a static counter each time its constructor runs and exposes
/// the count through a tool. Used to prove the host constructs an extension exactly once during load
/// (F2: a throwaway descriptor-reading prototype must not run constructor side effects a second time).
/// </summary>
public sealed class ConstructionCountExtension : IThreadsmithExtension
{
    /// <summary>Number of times any instance of this extension has been constructed in this process.</summary>
    private static int _constructionCount;

    /// <summary>Gets the number of times any instance of this extension has been constructed in this process.</summary>
    public static int ConstructionCount => _constructionCount;

    /// <summary>Initializes a new instance of the <see cref="ConstructionCountExtension"/> class.</summary>
    public ConstructionCountExtension()
    {
        Interlocked.Increment(ref _constructionCount);
    }

    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.construction-count",
        Name = "Construction Count Fixture",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider"],
        Permissions = [],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Capabilities.RegisterTool(new ConstructionCountTool());
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
        => default;
}

/// <summary>A tool that returns the number of times the extension was constructed.</summary>
public sealed class ConstructionCountTool : IToolCapability
{
    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "construction_count",
        Kind = CapabilityKind.Tool,
        DisplayName = "Construction Count",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; } = new()
    {
        Id = "construction_count",
        Version = "1.0",
        Description = "Returns the number of times the extension constructor ran. Fixture for the F2 test.",
        Category = ExtensionToolCategory.RepositoryInspection,
        InputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
        OutputSchema = new ExtensionToolSchema("Count", 1, "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\"}}}"),
        RequiredTrust = ExtensionTrustRequirement.None,
        RequiredApproval = ExtensionApprovalRequirement.None,
        SideEffect = ExtensionToolSideEffect.ReadOnly,
        Idempotency = ExtensionToolIdempotency.Idempotent,
        SupportsCancellation = true,
    };

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
    {
        string resultJson = JsonSerializer.Serialize(new { count = ConstructionCountExtension.ConstructionCount });
        return Task.FromResult(new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = resultJson,
            Sources = [new ExtensionProvenanceSource("extension", "construction_count")],
        });
    }
}