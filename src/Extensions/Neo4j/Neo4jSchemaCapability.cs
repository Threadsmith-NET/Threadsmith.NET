namespace Threadsmith.Extensions.Neo4j;

using System.Globalization;
using Threadsmith.Extensions.Abstractions;

/// <summary>Returns bounded, structured schema information with explicit completeness limits.</summary>
/// <remarks>
/// Declared sections come from built-in server metadata commands. Observed sections come from
/// built-in sampling procedures and are never presented as database rules. No plugin is required and
/// a denied metadata command never falls back to an unrestricted graph scan.
/// </remarks>
internal sealed class Neo4jSchemaCapability : IToolCapability
{
    private const string _inputSchemaJson =
        """
        {"type":"object","additionalProperties":false,"properties":{"sections":{"type":"array","items":{"enum":["constraints","indexes","nodes","relationships","relationshipEndpoints"]}},"sampleLimit":{"type":"integer","minimum":1,"description":"Lowers the configured upper bound. It cannot raise it."}}}
        """;

    private const string _outputSchemaJson =
        """
        {"type":"object","properties":{"schemaVersion":{"type":"integer"},"destination":{"type":"string"},"database":{"type":"string"},"sampleLimit":{"type":"integer"},"note":{"type":"string"},"sections":{"type":"object","additionalProperties":{"type":"object","properties":{"kind":{"enum":["declared","observed"]},"status":{"enum":["complete","truncated","permissionDenied","unsupported","unavailable"]},"sourceMethod":{"type":"string"},"limit":{"type":"integer"},"returned":{"type":"integer"},"entries":{"type":"array"},"detail":{"type":["string","null"]}}}}},"required":["schemaVersion","sections"]}
        """;

    private readonly Neo4jSettings _settings;
    private readonly IGraphSessionFactory _sessions;

    /// <summary>Initializes a new instance of the <see cref="Neo4jSchemaCapability"/> class.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="sessions">The invocation-scoped session factory.</param>
    internal Neo4jSchemaCapability(Neo4jSettings settings, IGraphSessionFactory sessions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sessions);
        _settings = settings;
        _sessions = sessions;
        Descriptor = new CapabilityDescriptor
        {
            Id = "neo4j_schema",
            Kind = CapabilityKind.Tool,
            DisplayName = "Neo4j Schema",
            RequiredPermissions = ["network-access", "secret-access"],
        };
        Definition = new ExtensionToolDefinition
        {
            Id = "neo4j_schema",
            Version = "1.0",
            Description = string.Create(
                CultureInfo.InvariantCulture,
                $"Returns declared constraints and indexes plus observed labels, relationship types, and property types for {settings.Destination} database '{settings.Database}'. Each section reports its source method, status, and limits. Observed sections are samples, not database rules."),
            Category = ExtensionToolCategory.ExternalService,
            InputSchema = new("Neo4jSchemaInput", 1, _inputSchemaJson),
            OutputSchema = new("Neo4jSchemaOutput", 1, _outputSchemaJson),
            RequiredTrust = ExtensionTrustRequirement.FullyTrustedAutomation,
            RequiredApproval = ExtensionApprovalRequirement.None,
            SideEffect = ExtensionToolSideEffect.ReadOnly,
            Idempotency = ExtensionToolIdempotency.Idempotent,
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
        IReadOnlyList<string> requested;
        int sampleLimit;
        try
        {
            (requested, sampleLimit) = Neo4jArguments.ReadSchemaRequest(argumentsJson, _settings);
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
            session => DiscoverAsync(session, requested, sampleLimit, cancellationToken),
            (kind, reason) => Neo4jResults.NotStarted(_settings, kind, reason),
            cancellationToken);
    }

    private static (GraphSchemaKind Kind, string Query, string SourceMethod)? Describe(string section)
    {
        return section switch
        {
            "constraints" => (
                GraphSchemaKind.Declared,
                "SHOW CONSTRAINTS YIELD name, type, entityType, labelsOrTypes, properties "
                    + "RETURN name, type, entityType, labelsOrTypes, properties LIMIT $limit",
                "SHOW CONSTRAINTS"),
            "indexes" => (
                GraphSchemaKind.Declared,
                "SHOW INDEXES YIELD name, type, entityType, labelsOrTypes, properties, state "
                    + "RETURN name, type, entityType, labelsOrTypes, properties, state LIMIT $limit",
                "SHOW INDEXES"),
            "nodes" => (
                GraphSchemaKind.Observed,
                "CALL db.schema.nodeTypeProperties() YIELD nodeLabels, propertyName, propertyTypes "
                    + "RETURN nodeLabels, propertyName, propertyTypes LIMIT $limit",
                "db.schema.nodeTypeProperties"),
            "relationships" => (
                GraphSchemaKind.Observed,
                "CALL db.schema.relTypeProperties() YIELD relType, propertyName, propertyTypes "
                    + "RETURN relType, propertyName, propertyTypes LIMIT $limit",
                "db.schema.relTypeProperties"),
            _ => null,
        };
    }

    private static GraphSchemaSection Unsupported(string section, int sampleLimit)
    {
        return new GraphSchemaSection
        {
            Name = section,
            Kind = GraphSchemaKind.Observed,
            Status = GraphSchemaStatus.Unsupported,
            SourceMethod = "none",
            Limit = sampleLimit,
            Detail = "Observed source-label, relationship-type, and target-label combinations require a graph scan. "
                + "This version does not perform one, so the absence of this section says nothing about the graph.",
        };
    }

    private static GraphSchemaSection Build(
        string section,
        GraphSchemaKind kind,
        string sourceMethod,
        int sampleLimit,
        GraphExecutionResult execution)
    {
        if (execution.Failure is { } failure)
        {
            return new GraphSchemaSection
            {
                Name = section,
                Kind = kind,
                Status = failure.Kind is GraphFailureKind.Authorization or GraphFailureKind.Authentication
                    ? GraphSchemaStatus.PermissionDenied
                    : GraphSchemaStatus.Unavailable,
                SourceMethod = sourceMethod,
                Limit = sampleLimit,
                Detail = failure.Code is null
                    ? $"The section failed with a {failure.Kind} error."
                    : $"The section failed with a {failure.Kind} error (code={failure.Code}).",
            };
        }

        var truncated = execution.Truncated || execution.Records.Count > sampleLimit;
        var entries = execution.Records;
        if (entries.Count > sampleLimit)
        {
            entries = [.. entries.Take(sampleLimit)];
        }

        var detail = truncated
            ? "The section reached its limit. Entries are missing, so this is not the complete set."
            : null;
        if (kind == GraphSchemaKind.Observed)
        {
            const string note = "Observed entries come from server metadata procedures that read stored "
                + "entities. The limit bounds returned rows, not the server work used to produce them.";
            detail = detail is null ? note : detail + " " + note;
        }

        return new GraphSchemaSection
        {
            Name = section,
            Kind = kind,
            Status = truncated ? GraphSchemaStatus.Truncated : GraphSchemaStatus.Complete,
            SourceMethod = sourceMethod,
            Limit = sampleLimit,
            Entries = entries,
            Detail = detail,
        };
    }

    private async Task<ExtensionToolResult> DiscoverAsync(
        IGraphSession session,
        IReadOnlyList<string> requested,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        List<GraphSchemaSection> sections = [];
        foreach (var section in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Describe(section) is not { } descriptor)
            {
                sections.Add(Unsupported(section, sampleLimit));
                continue;
            }

            var statement = new GraphStatement
            {
                Cypher = descriptor.Query,

                // One extra entry separates an exact-size section from a genuinely truncated one.
                Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["limit"] = (long)sampleLimit + 1,
                },
                MaximumRows = sampleLimit + 1,
                MaximumRecordBytes = _settings.MaximumResultBytes / Math.Max(requested.Count, 1),
            };

            // Schema discovery always uses read access mode, even when writes are permitted.
            var execution = await session.RunAsync(statement, readOnly: true, cancellationToken);
            sections.Add(Build(section, descriptor.Kind, descriptor.SourceMethod, sampleLimit, execution));
        }

        return Neo4jResults.FromSchema(_settings, sampleLimit, sections);
    }
}
