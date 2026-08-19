namespace Threadsmith.SampleExtensions.MinimalTool;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Extensions.Abstractions;

/// <summary>A sample extension contributing a callable echo tool and a model-preference hint.</summary>
public sealed class MinimalToolExtension : IThreadsmithExtension
{
    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.sample.minimal-tool",
        Name = "Minimal Tool Extension",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider", "model-preference"],
        Permissions = ["repository-read"],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Capabilities.RegisterTool(new EchoToolCapability(
            context.Configuration["toolId"] ?? "sample_echo",
            context.Configuration["capabilityId"] ?? "sample_echo"));
        context.Capabilities.RegisterModelPreferenceContributor(new SampleModelPreferenceContributor());
        context.Logger.Information("Minimal tool extension activated.");
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Logger.Information("Minimal tool extension deactivated.");
        return default;
    }
}

/// <summary>A read-only echo tool that returns its input message in a host-owned DTO.</summary>
public sealed class EchoToolCapability : IToolCapability
{
    private const string InputSchemaJson =
        """
        {"type":"object","properties":{"message":{"type":"string","description":"The message to echo."}},"required":["message"]}
        """;

    private const string OutputSchemaJson =
        """
        {"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}
        """;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Initializes a new instance of the <see cref="EchoToolCapability"/> class.</summary>
    /// <param name="toolId">The tool-definition id.</param>
    /// <param name="capabilityId">The capability descriptor id.</param>
    public EchoToolCapability(
        string toolId = "sample_echo",
        string capabilityId = "sample_echo")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        Descriptor = new CapabilityDescriptor
        {
            Id = capabilityId,
            Kind = CapabilityKind.Tool,
            DisplayName = "Sample Echo",
        };
        Definition = new ExtensionToolDefinition
        {
            Id = toolId,
            Version = "1.0",
            Description = "Echoes the provided message back as JSON. Sample extension tool.",
            Category = ExtensionToolCategory.RepositoryInspection,
            InputSchema = new ExtensionToolSchema("EchoInput", 1, InputSchemaJson),
            OutputSchema = new ExtensionToolSchema("EchoOutput", 1, OutputSchemaJson),
            RequiredTrust = ExtensionTrustRequirement.None,
            RequiredApproval = ExtensionApprovalRequirement.None,
            SideEffect = ExtensionToolSideEffect.ReadOnly,
            Idempotency = ExtensionToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(10),
            MaximumOutputBytes = 8 * 1024,
        };
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        EchoInput input;
        try
        {
            input = JsonSerializer.Deserialize<EchoInput>(argumentsJson, _jsonOptions)
                ?? throw new JsonException("Echo arguments were empty.");
        }
        catch (JsonException exception)
        {
            return Task.FromResult(new ExtensionToolResult
            {
                Succeeded = false,
                Error = $"Invalid echo arguments: {exception.Message}",
            });
        }

        var output = new EchoOutput { Message = input.Message };
        var resultJson = JsonSerializer.Serialize(output);
        return Task.FromResult(new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = resultJson,
            Sources = [new ExtensionProvenanceSource("extension", "sample_echo")],
        });
    }
}

/// <summary>An advisory model-preference contributor preferring the configured profile named "fast".</summary>
public sealed class SampleModelPreferenceContributor : IModelPreferenceContributor
{
    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "sample_model_preference",
        Kind = CapabilityKind.ModelPreference,
        DisplayName = "Sample Model Preference",
    };

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(
        string workloadName,
        CancellationToken cancellationToken = default)
    {
        var hint = new ExtensionModelPreferenceHint
        {
            WorkloadName = workloadName,
            PreferredProfileName = "fast",
            Priority = 10,
            Rationale = "Sample extension prefers the fast profile for general and planning work.",
        };
        IReadOnlyList<ExtensionModelPreferenceHint> hints = workloadName is "General" or "Planning"
            ? [hint]
            : [];

        return Task.FromResult(hints);
    }
}

/// <summary>Input for the echo tool.</summary>
internal sealed record EchoInput
{
    /// <summary>The message to echo.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Output from the echo tool.</summary>
internal sealed record EchoOutput
{
    /// <summary>The echoed message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
