namespace Threadsmith.Extensions.Neo4j;

using Threadsmith.Extensions.Abstractions;

/// <summary>Contributes bounded Cypher execution and schema discovery for one configured database.</summary>
/// <remarks>
/// Activation validates the repository-owned settings section without opening a connection,
/// resolving a credential, or starting background work. The configured destination, access mode, and
/// limits are captured once per generation.
/// </remarks>
public sealed class Neo4jExtension : IThreadsmithExtension
{
    private readonly IGraphSessionFactory _sessions;

    /// <summary>Initializes a new instance of the <see cref="Neo4jExtension"/> class.</summary>
    public Neo4jExtension()
        : this(new Neo4jDriverSessionFactory())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Neo4jExtension"/> class with a supplied gateway.</summary>
    /// <param name="sessions">The invocation-scoped session factory.</param>
    internal Neo4jExtension(IGraphSessionFactory sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.neo4j",
        Name = "Neo4j",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        RequiredTrust = ExtensionTrustRequirement.FullyTrustedAutomation,
        Capabilities = ["tool-provider"],
        Permissions = ["network-access", "secret-access"],
        AccessTypes = ["network-access", "secret-access", "database-read", "database-write"],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        Neo4jSettings settings;
        try
        {
            settings = Neo4jSettings.Create(context.Configuration);
        }
        catch (InvalidOperationException exception)
        {
            // Settings diagnostics name the key and rule, never the configured value.
            throw new ExtensionValidationException(exception.Message);
        }

        context.Capabilities.RegisterTool(new Neo4jExecuteCapability(settings, _sessions));
        context.Capabilities.RegisterTool(new Neo4jSchemaCapability(settings, _sessions));

        // The log states the configured mode only. It never contains the endpoint, account, or reference.
        context.Logger.Information(settings.ReadOnly
            ? "Neo4j extension activated in read-only mode."
            : "Neo4j extension activated with write-capable execution. Writes still require operator approval and database privileges.");
        return default;
    }

    /// <inheritdoc />
    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Drivers are invocation-scoped, so deactivation releases nothing that could pin the context.
        context.Logger.Information("Neo4j extension deactivated.");
        return default;
    }
}
