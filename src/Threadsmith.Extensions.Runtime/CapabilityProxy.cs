namespace Threadsmith.Extensions.Runtime;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Host-owned <see cref="ITool"/> proxy that adapts an extension <see cref="IToolCapability"/> (strategy §17.14, §7.1).
/// The proxy guarantees no extension implementation type crosses the boundary: it holds the capability, acquires an
/// invocation lease, runs the extension's JSON-in/JSON-out contract, and normalizes the result to host-owned DTOs.</summary>
public sealed class CapabilityProxy : ITool
{
    private readonly IToolCapability _capability;
    private readonly CapabilityId _capabilityId;
    private readonly ExtensionGeneration _generation;
    private readonly InvocationLeaseAuthority _leaseAuthority;
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="CapabilityProxy"/> class.</summary>
    /// <param name="capability">The extension tool capability.</param>
    /// <param name="capabilityId">The host-assigned capability id.</param>
    /// <param name="generation">The owning generation.</param>
    /// <param name="leaseAuthority">The lease authority.</param>
    /// <param name="logger">The logger.</param>
    public CapabilityProxy(
        IToolCapability capability,
        CapabilityId capabilityId,
        ExtensionGeneration generation,
        InvocationLeaseAuthority leaseAuthority,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(leaseAuthority);
        ArgumentNullException.ThrowIfNull(logger);
        _capability = capability;
        _capabilityId = capabilityId;
        _generation = generation;
        _leaseAuthority = leaseAuthority;
        _logger = logger;
        Definition = MapDefinition(capability.Definition, generation.Descriptor.Name);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <summary>The immutable extension generation that owns this proxy.</summary>
    public ExtensionGenerationId GenerationId => _generation.GenerationId;

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);

        // The extension deserializes and validates its own arguments because the host cannot resolve
        // extension-owned input types from the default load context (plan-14). Keep the raw JSON as the
        // opaque input the pipeline carries through policy and execution.
        return new ExtensionToolArguments(argumentsJson);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        return _capability.GetResourcePaths(((ExtensionToolArguments)input).Json, MapContext(context));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _capability.GetSecretReferences(((ExtensionToolArguments)input).Json);
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _capability.GetExecutable(((ExtensionToolArguments)input).Json);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return _capability.GetNetworkHosts(((ExtensionToolArguments)input).Json);
    }

    /// <inheritdoc />
    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var argumentsJson = ((ExtensionToolArguments)input).Json;

        // Acquire a lease so plan-17 draining cannot unload the ALC while the extension is executing.
        var lease = _leaseAuthority.Acquire(
            _generation.GenerationId,
            _generation.Budget ?? new ExtensionInvocationBudget(_generation.GenerationId),
            GetLeaseTimeout());
        try
        {
            var result = await _capability.ExecuteAsync(
                argumentsJson,
                MapContext(context.Invocation),
                cancellationToken);
            object value = result.ResultJson is null
                ? new { succeeded = result.Succeeded }
                : JsonDocument.Parse(result.ResultJson).RootElement.Clone();
            var sources = result.Sources
                .Select(s => new ToolProvenanceSource(s.Kind, s.Identifier, s.Range))
                .ToArray();
            return new ToolExecutionEnvelope(value, sources, result.IsTruncated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "Extension tool {ToolId} for generation {GenerationId} failed: {Error}",
                _capability.Definition.Id,
                _generation.GenerationId,
                exception.Message);
            throw;
        }
        finally
        {
            lease.Dispose();
        }
    }

    private TimeSpan GetLeaseTimeout()
    {
        return _capability.Definition.Timeout > TimeSpan.Zero
                ? _capability.Definition.Timeout
                : InvocationLeaseAuthority.DefaultLeaseTimeout;
    }

    private static ExtensionToolInvocationContext MapContext(ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ExtensionToolInvocationContext
        {
            RepositoryPath = context.RepositoryPath,
            ApprovedRoots = context.ApprovedRoots,
            ProhibitedPaths = context.ProhibitedPaths,
            AllowedExecutables = context.AllowedExecutables,
            AllowedNetworkHosts = context.AllowedNetworkHosts,
            AllowedSecretReferences = context.AllowedSecretReferences,
            RequestedBy = context.RequestedBy,
        };
    }

    private static ToolDefinition MapDefinition(ExtensionToolDefinition extension, string extensionDisplayName)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionDisplayName);
        return new ToolDefinition
        {
            Id = extension.Id,
            DisplayName = extension.Id.Replace('_', ' '),
            Source = extensionDisplayName,
            Essential = false,
            Version = extension.Version,
            Description = extension.Description,
            Category = MapCategory(extension.Category),
            InputSchema = new ToolSchema(extension.InputSchema.TypeName, extension.InputSchema.SchemaVersion, extension.InputSchema.JsonSchema),
            OutputSchema = new ToolSchema(extension.OutputSchema.TypeName, extension.OutputSchema.SchemaVersion, extension.OutputSchema.JsonSchema),
            RequiredTrust = MapTrust(extension.RequiredTrust),
            RequiredApproval = MapApproval(extension.RequiredApproval),
            SideEffect = MapSideEffect(extension.SideEffect),
            Idempotency = MapIdempotency(extension.Idempotency),
            SupportsCancellation = extension.SupportsCancellation,
            Timeout = extension.Timeout,
            MaximumOutputBytes = extension.MaximumOutputBytes,
        };
    }

    private static ToolCategory MapCategory(ExtensionToolCategory category)
    {
        return category switch
        {
            ExtensionToolCategory.RepositoryInspection => ToolCategory.RepositoryInspection,
            ExtensionToolCategory.FileRead => ToolCategory.FileRead,
            ExtensionToolCategory.FileSearch => ToolCategory.FileSearch,
            ExtensionToolCategory.SemanticSearch => ToolCategory.SemanticSearch,
            ExtensionToolCategory.GitInspection => ToolCategory.GitInspection,
            ExtensionToolCategory.ProcessExecution => ToolCategory.ProcessExecution,
            _ => ToolCategory.RepositoryInspection,
        };
    }

    private static RepositoryTrustLevel MapTrust(ExtensionTrustRequirement trust)
    {
        return trust switch
        {
            ExtensionTrustRequirement.None => RepositoryTrustLevel.UntrustedInspection,
            ExtensionTrustRequirement.RepositoryRead => RepositoryTrustLevel.TrustedRead,
            ExtensionTrustRequirement.RepositoryBuild => RepositoryTrustLevel.TrustedBuild,
            ExtensionTrustRequirement.RepositoryMutation => RepositoryTrustLevel.TrustedMutation,
            _ => RepositoryTrustLevel.UntrustedInspection,
        };
    }

    private static ApprovalLevel MapApproval(ExtensionApprovalRequirement approval)
    {
        return approval switch
        {
            ExtensionApprovalRequirement.None => ApprovalLevel.None,
            ExtensionApprovalRequirement.HostPolicy => ApprovalLevel.HostPolicy,
            ExtensionApprovalRequirement.User => ApprovalLevel.User,
            _ => throw new ArgumentOutOfRangeException(nameof(approval), approval, "Unknown extension approval requirement."),
        };
    }

    private static ToolSideEffect MapSideEffect(ExtensionToolSideEffect sideEffect)
    {
        return sideEffect switch
        {
            ExtensionToolSideEffect.ReadOnly => ToolSideEffect.ReadOnly,
            ExtensionToolSideEffect.ExecutesCode => ToolSideEffect.ExecutesCode,
            _ => ToolSideEffect.ReadOnly,
        };
    }

    private static ToolIdempotency MapIdempotency(ExtensionToolIdempotency idempotency)
    {
        return idempotency switch
        {
            ExtensionToolIdempotency.Idempotent => ToolIdempotency.Idempotent,
            ExtensionToolIdempotency.NonIdempotent => ToolIdempotency.NonIdempotent,
            _ => ToolIdempotency.Idempotent,
        };
    }

    /// <summary>Opaque raw-JSON input carried through the host pipeline.</summary>
    private sealed record ExtensionToolArguments(string Json);
}
