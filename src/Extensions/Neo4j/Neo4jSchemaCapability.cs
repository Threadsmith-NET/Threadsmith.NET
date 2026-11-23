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
        {"type":"object","additionalProperties":false,"properties":{"sections":{"type":"array","items":{"enum":["constraints","indexes","nodes","relationships","relationshipEndpoints","nodeSamples"]},"description":"Defaults to nodeSamples, relationshipEndpoints and indexes. Request nodes or relationships explicitly for detailed property discovery."},"forceRefresh":{"type":"boolean","default":false,"description":"Refresh cached schema information — by user request only."},"sampleLimit":{"type":"integer","minimum":1,"description":"Lowers the configured upper bound. It cannot raise it."}}}
        """;

    private const string _outputSchemaJson =
        """
        {"type":"object","properties":{"schemaVersion":{"type":"integer"},"destination":{"type":"string"},"database":{"type":"string"},"sampleLimit":{"type":"integer"},"note":{"type":"string"},"sections":{"type":"object","additionalProperties":{"type":"object","properties":{"kind":{"enum":["declared","observed"]},"status":{"enum":["complete","truncated","permissionDenied","unsupported","unavailable"]},"sourceMethod":{"type":"string"},"limit":{"type":"integer"},"returned":{"type":"integer"},"entries":{"type":"array"},"detail":{"type":["string","null"]}}}}},"required":["schemaVersion","sections"]}
        """;

    // Limit both the work per token and the total number of entities inspected per sample section.
    private const int _perName = 8;
    private const int _entityBudget = 256;

    private static readonly string _nodeSamples = SampleQuery(relationships: false);
    private static readonly string _relationshipSamples = SampleQuery(relationships: true);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly Dictionary<string, GraphSchemaSection> _cache = new(StringComparer.Ordinal);
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
                $"Returns cached labels, directed relationship endpoints, sampled property names and types, and indexes for {settings.Destination} database '{settings.Database}'. Observations are not rules. Detailed sections are optional. Set forceRefresh by user request only."),
            Category = ExtensionToolCategory.ExternalService,
            InputSchema = new("Neo4jSchemaInput", 1, _inputSchemaJson),
            OutputSchema = new("Neo4jSchemaOutput", 1, _outputSchemaJson),
            RequiredTrust = ExtensionTrustRequirement.FullyTrustedAutomation,
            RequiredApproval = ExtensionApprovalRequirement.None,
            SideEffect = ExtensionToolSideEffect.ReadOnly,
            Idempotency = ExtensionToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromMinutes(5),
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
        bool forceRefresh;
        try
        {
            (requested, sampleLimit, forceRefresh) = Neo4jArguments.ReadSchemaRequest(argumentsJson, _settings);
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
            session => DiscoverAsync(session, requested, sampleLimit, forceRefresh, cancellationToken),
            (kind, reason) => Neo4jResults.NotStarted(_settings, kind, reason),
            cancellationToken);
    }

    // Sample once, before expanding the catalog. A rare token must not trigger a graph search.
    private static string SampleQuery(bool relationships)
    {
        var catalog = relationships ? "db.relationshipTypes() YIELD relationshipType AS name" : "db.labels() YIELD label AS name";
        var pattern = relationships ? "()-[entity]->()" : "(entity)";
        var tokens = relationships ? "[type(entity)]" : "labels(entity)";
        var key = relationships ? "relationshipType" : "label";
        var endpoints = relationships
            ? ", COLLECT { UNWIND entities AS r RETURN DISTINCT {sourceLabels: labels(startNode(r)), targetLabels: labels(endNode(r))} AS pair } AS endpoints"
            : string.Empty;
        return $$"""
            CALL {{catalog}}
            WITH name LIMIT $limit
            WITH collect(name) AS names
            WITH names, COLLECT {
                MATCH {{pattern}} WITH entity LIMIT $entityBudget
                RETURN {entity: entity, tokens: {{tokens}}} AS entry
            } AS sample
            UNWIND names AS name
            WITH name, [s IN sample WHERE name IN s.tokens | s.entity][..$perName] AS entities
            RETURN name AS {{key}}, size(entities) > 0 AS inspected, size(entities) AS sampled,
                COLLECT {
                    UNWIND entities AS e UNWIND keys(e) AS k
                    WITH k, collect(DISTINCT valueType(e[k])) AS types
                    RETURN {name: k, types: types} AS property
                } AS properties{{endpoints}}
            """;
    }

    private static (GraphSchemaKind Kind, string Query, string SourceMethod) Describe(string section)
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
            "nodeSamples" => (GraphSchemaKind.Observed, _nodeSamples, "db.labels + node sample"),
            "relationshipEndpoints" => (GraphSchemaKind.Observed, _relationshipSamples, "db.relationshipTypes + directed sample"),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
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
            var note = section is "nodeSamples" or "relationshipEndpoints"
                ? "Properties and directed endpoints are observations only. inspected=false means this name was listed but not sampled."
                : "The limit bounds property rows returned by the metadata procedure, not the server work to produce them.";
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
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        // Serialize misses so two first-time callers do not both run discovery. Hits still go
        // through the normal credential admission path, but execute no database statements.
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            List<GraphSchemaSection> sections = [];
            foreach (var name in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!forceRefresh && _cache.TryGetValue(name, out var cached) && cached.Limit == sampleLimit)
                {
                    sections.Add(cached);
                    continue;
                }

                var descriptor = Describe(name);
                var perName = Math.Min(_perName, sampleLimit);
                var statement = new GraphStatement
                {
                    Cypher = descriptor.Query,
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["limit"] = (long)sampleLimit + 1,
                        ["perName"] = perName,
                        ["entityBudget"] = Math.Min(_entityBudget, sampleLimit),
                    },
                    MaximumRows = sampleLimit + 1,
                    MaximumRecordBytes = _settings.MaximumResultBytes,
                };
                var execution = await session.RunAsync(statement, readOnly: true, cancellationToken);
                sections.Add(Build(name, descriptor.Kind, descriptor.SourceMethod, sampleLimit, execution));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = Neo4jResults.FromSchema(_settings, sampleLimit, sections);
            foreach (var section in sections)
            {
                if (section.Status is GraphSchemaStatus.Complete or GraphSchemaStatus.Truncated)
                {
                    _cache[section.Name] = section;
                }
            }

            return result;
        }
        finally
        {
            _cacheGate.Release();
        }
    }
}
