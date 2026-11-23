namespace Threadsmith.Extensions.Runtime;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Host-owned <see cref="ITool"/> proxy that adapts an extension <see cref="IToolCapability"/> (strategy §17.14, §7.1).
/// The proxy guarantees no extension implementation type crosses the boundary: it holds the capability, acquires an
/// invocation lease, runs the extension's JSON-in/JSON-out contract, and normalizes the result to host-owned DTOs.</summary>
public sealed class CapabilityProxy : ITool, IToolStatementReview
{
    private readonly ExtensionStatementReview? _review;
    private readonly IToolCapability _capability;
    private readonly CapabilityId _capabilityId;
    private readonly ExtensionGeneration _generation;
    private readonly Func<ExtensionInvocationBudget?, TimeSpan, IInvocationLease> _acquireLease;
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
        _acquireLease = leaseAuthority.CaptureAdmission(generation.GenerationId);
        _logger = logger;
        var metadata = capability.Definition;
        _review = metadata.StatementReview;
        var definition = MapDefinition(metadata, generation.Descriptor.Name);
        Definition = definition.RequiredTrust < generation.MinimumTrust ? definition with { RequiredTrust = generation.MinimumTrust } : definition;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    public string? CreateReview(object input, IOutputSanitizer sanitizer)
    {
        return _review is null ? null : ExtensionStatementPreview.Create(_review, _generation.Descriptor.Id, Definition.Id, GenerationId, ((ExtensionToolArguments)input).Json, sanitizer, _generation.SecretProtection);
    }

    /// <summary>The immutable extension generation that owns this proxy.</summary>
    public ExtensionGenerationId GenerationId => _generation.GenerationId;

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);

        // The extension deserializes and validates its own arguments because the host cannot resolve
        // extension-owned input types from the default load context (plan-14). Keep the cleaned JSON as
        // opaque input the pipeline carries through policy and execution.
        return new ExtensionToolArguments(ModelJsonCleanup.Clean(argumentsJson));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Enabling the package grants its disk access. These host path-policy claims apply to host tools.
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Copy the extension-owned collection inside the boundary; enumeration can also run extension code.
        return ReadAdmissionClaim(
            () => _capability.GetSecretReferences(((ExtensionToolArguments)input).Json).ToArray(),
            references => references.Any(_generation.SecretProtection.Contains),
            unavailable: []);
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReadAdmissionClaim(
            () => _capability.GetExecutable(((ExtensionToolArguments)input).Json),
            _generation.SecretProtection.Contains,
            unavailable: null);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The enabled extension owns its outbound connections, including redirects and discovered peers.
        return [];
    }

    /// <inheritdoc />
    public Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return ExecuteCoreAsync(input, context, markDispatched: null, cancellationToken);
    }

    Task<ToolExecutionEnvelope> ITool.ExecuteExternalWriteAsync(
        object input,
        ToolExecutionContext context,
        Action markDispatched,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markDispatched);
        return ExecuteCoreAsync(input, context, markDispatched, cancellationToken);
    }

    /// <summary>Maps the extension trust requirement to the host repository level.</summary>
    internal static RepositoryTrustLevel MapTrust(ExtensionTrustRequirement trust)
    {
        return trust switch
        {
            ExtensionTrustRequirement.None => RepositoryTrustLevel.UntrustedInspection,
            ExtensionTrustRequirement.RepositoryRead => RepositoryTrustLevel.TrustedRead,
            ExtensionTrustRequirement.RepositoryBuild => RepositoryTrustLevel.TrustedBuild,
            ExtensionTrustRequirement.RepositoryMutation => RepositoryTrustLevel.TrustedMutation,
            ExtensionTrustRequirement.FullyTrustedAutomation => RepositoryTrustLevel.FullyTrustedAutomation,
            _ => throw new ExtensionIncompatibleException("Unsupported extension trust requirement."),
        };
    }

    private async Task<ToolExecutionEnvelope> ExecuteCoreAsync(
        object input,
        ToolExecutionContext context,
        Action? markDispatched,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (_generation.RepositoryPath is { } repositoryPath
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Invocation.RepositoryPath)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("ExtensionRepositoryStale: this extension generation belongs to another repository.");
        }

        var argumentsJson = ((ExtensionToolArguments)input).Json;

        // Acquire a lease so plan-17 draining cannot unload the ALC while the extension is executing.
        using var lease = _acquireLease(
            _generation.Budget ?? new ExtensionInvocationBudget(_generation.GenerationId), GetLeaseTimeout());
        ExtensionSecretAccess? secrets = null;
        using var boundary = _generation.SecretProtection.CreateScope(pinActiveValues: true);
        try
        {
            var references = context.TakeSecretReferences(this, input);
            ExtensionToolResult result;
            if (references is not null && _generation.SecretResolver is { } resolver && context.Invocation.TrustLevel >= RepositoryTrustLevel.FullyTrustedAutomation)
            {
                secrets = new ExtensionSecretAccess(resolver, references, context, _generation.SecretProtection, _generation.Descriptor.Id, _generation.SecretRetirement.Token, cancellationToken);
            }

            try
            {
                var extensionContext = MapContext(context.Invocation) with { Secrets = secrets };
                markDispatched?.Invoke();
                result = await _capability.ExecuteAsync(argumentsJson, extensionContext, cancellationToken);
            }
            finally
            {
                secrets?.Revoke();
            }

            object value = result.ResultJson is null
                ? new { succeeded = result.Succeeded }
                : JsonSerializer.Deserialize<JsonElement>(result.ResultJson);
            var sources = result.Sources
                .Select(s => new ToolProvenanceSource(s.Kind, s.Identifier, s.Range))
                .ToArray();
            var protection = _generation.SecretProtection;
            if (protection.HasValues && ((value is JsonElement json && protection.Contains(json))
                || sources.Any(source => protection.ContainsDiagnostic(source.Kind) || protection.ContainsDiagnostic(source.Identifier) || protection.ContainsDiagnostic(source.Range))))
            {
                throw new InvalidOperationException("ExtensionSecretInOutput");
            }

            var failure = result.Succeeded
                ? null
                : new ToolExecutionFailure(ToolErrorClassification.ExecutionFailure, protection.SafeLogMessage(result.Error ?? "ExtensionToolExecutionFailed"));
            return new ToolExecutionEnvelope(value, sources, result.IsTruncated, Failure: failure);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_generation.SecretProtection.HasValues)
            {
                throw new InvalidOperationException("ExtensionToolExecutionFailed");
            }

            _logger.LogError(
                "Extension tool {ToolId} for generation {GenerationId} failed: {Error}",
                Definition.Id,
                _generation.GenerationId,
                exception.Message);
            throw;
        }
        finally
        {
            secrets?.Dispose();
        }
    }

    private T ReadAdmissionClaim<T>(Func<T> read, Func<T, bool> containsSecret, T unavailable)
    {
        IInvocationLease lease;
        try
        {
            lease = _acquireLease(null, GetLeaseTimeout());
        }
        catch (ExtensionDrainingException)
        {
            // Do not call a retiring extension for preflight data. The same permanently closed
            // book refuses execution before dispatch, where the pipeline reports not-started.
            return unavailable;
        }

        using var claimLease = lease;
        using var boundary = _generation.SecretProtection.CreateScope(pinActiveValues: true);
        T claim;
        try
        {
            claim = read();
        }
        catch (Exception) when (_generation.SecretProtection.HasValues)
        {
            throw new UnauthorizedAccessException("An extension admission callback failed.");
        }

        if (containsSecret(claim))
        {
            throw new UnauthorizedAccessException("An extension admission claim contained a protected value.");
        }

        return claim;
    }

    private TimeSpan GetLeaseTimeout()
    {
        return Definition.Timeout > TimeSpan.Zero ? Definition.Timeout : InvocationLeaseAuthority.DefaultLeaseTimeout;
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
            ExtensionToolCategory.ExternalService => ToolCategory.ExternalService,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown extension tool category."),
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
            ExtensionToolSideEffect.WritesExternalData => ToolSideEffect.WritesExternalData,
            _ => throw new ArgumentOutOfRangeException(nameof(sideEffect)),
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
