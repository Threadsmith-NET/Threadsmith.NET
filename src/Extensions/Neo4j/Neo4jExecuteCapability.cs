namespace Threadsmith.Extensions.Neo4j;

using System.Globalization;
using Threadsmith.Extensions.Abstractions;

/// <summary>Runs one Cypher statement in one explicit transaction at the configured destination.</summary>
/// <remarks>
/// Static metadata is conservative: unless server-enforced read-only access is confirmed, every invocation is
/// classified as a non-idempotent external write that requires operator approval, because statement
/// text cannot prove that a query is read-only.
/// </remarks>
internal sealed class Neo4jExecuteCapability : IToolCapability
{
    private const string _inputSchemaJson =
        """
        {"type":"object","additionalProperties":false,"properties":{"cypher":{"type":"string","maxLength":4096,"description":"One Cypher statement. Prefer parameters over literals; the whole statement is shown to the operator."},"parameters":{"type":"object","description":"Optional parameter map of JSON scalars, arrays, or objects."}},"required":["cypher"]}
        """;

    private const string _outputSchemaJson =
        """
        {"type":"object","properties":{"schemaVersion":{"type":"integer"},"outcome":{"enum":["committed","rolledBack","notStarted","unknown"]},"phase":{"enum":["admission","connection","execution","consumption","commit","completed"]},"readOnly":{"type":"boolean"},"destination":{"type":"string"},"database":{"type":"string"},"columns":{"type":"array","items":{"type":"string"}},"records":{"type":"array"},"returnedRows":{"type":"integer"},"truncated":{"type":"boolean"},"limitsReached":{"type":"array","items":{"type":"string"}},"counters":{"type":["object","null"]},"statuses":{"type":"array"},"error":{"type":["object","null"]}},"required":["schemaVersion","outcome","phase","records","truncated"]}
        """;

    private readonly Neo4jSettings _settings;
    private readonly IGraphSessionFactory _sessions;

    /// <summary>Initializes a new instance of the <see cref="Neo4jExecuteCapability"/> class.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="sessions">The invocation-scoped session factory.</param>
    internal Neo4jExecuteCapability(Neo4jSettings settings, IGraphSessionFactory sessions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sessions);
        _settings = settings;
        _sessions = sessions;
        Descriptor = new CapabilityDescriptor
        {
            Id = "neo4j_execute",
            Kind = CapabilityKind.Tool,
            DisplayName = "Neo4j Execute",
            RequiredPermissions = ["network-access", "secret-access"],
        };
        var confirmedReadOnly = settings.ForceReadOnly && settings.ReadOnlyEnforcedByServerAccount;
        Definition = new ExtensionToolDefinition
        {
            Id = "neo4j_execute",
            Version = "1.0",
            Description = Describe(settings),
            Category = ExtensionToolCategory.ExternalService,
            InputSchema = new("Neo4jExecuteInput", 1, _inputSchemaJson),
            OutputSchema = new("Neo4jExecuteOutput", 1, _outputSchemaJson),
            RequiredTrust = ExtensionTrustRequirement.FullyTrustedAutomation,
            RequiredApproval = confirmedReadOnly
                ? ExtensionApprovalRequirement.None
                : ExtensionApprovalRequirement.User,
            StatementReview = new(settings.Destination, settings.Database, "cypher", "parameters"),
            SideEffect = confirmedReadOnly
                ? ExtensionToolSideEffect.ReadOnly
                : ExtensionToolSideEffect.WritesExternalData,
            Idempotency = confirmedReadOnly
                ? ExtensionToolIdempotency.Idempotent
                : ExtensionToolIdempotency.NonIdempotent,
            SupportsCancellation = true,
            Timeout = settings.QueryTimeout + TimeSpan.FromSeconds(15),
            MaximumOutputBytes = Neo4jResults.MaximumOutputBytes(settings),
        };
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        // The reference comes from the captured generation settings, never from model arguments.
        return [_settings.PasswordReference];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        GraphStatement statement;
        try
        {
            statement = Neo4jArguments.ReadStatement(argumentsJson, _settings);
        }
        catch (Neo4jArgumentException exception)
        {
            return Task.FromResult(
                Neo4jResults.NotStarted(_settings, GraphFailureKind.InvalidArguments, exception.Message));
        }

        return Neo4jExecution.RunAsync(
            _settings,
            _sessions,
            context,
            async session => Neo4jResults.FromExecution(
                _settings,
                await session.RunAsync(statement, _settings.ForceReadOnly, cancellationToken)),
            (kind, reason) => Neo4jResults.NotStarted(_settings, kind, reason),
            cancellationToken);
    }

    private static string Describe(Neo4jSettings settings)
    {
        string mode;
        if (!settings.ForceReadOnly)
        {
            mode = "This deployment permits write requests. Each call needs operator approval and the database account's own privileges. An unknown outcome is never retried automatically.";
        }
        else if (settings.ReadOnlyEnforcedByServerAccount)
        {
            mode = "This deployment is configured read-only, requests the server read access mode, and the operator confirmed the database account denies writes.";
        }
        else
        {
            mode = "This deployment requests the server read access mode, but server-enforced read-only access is not confirmed, so each call still needs operator approval.";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Runs one Cypher statement in one explicit transaction against {settings.Destination} database '{settings.Database}' and returns bounded, tagged records with an explicit transaction outcome. {mode}");
    }
}
