namespace Threadsmith.Extensions.Neo4j;

using System.Text.Json.Nodes;

/// <summary>Transaction outcome names shared with the host external-write receipt vocabulary.</summary>
internal static class GraphOutcome
{
    /// <summary>The server confirmed the commit.</summary>
    internal const string Committed = "committed";

    /// <summary>No database operation began.</summary>
    internal const string NotStarted = "notStarted";

    /// <summary>The database confirmed the rollback.</summary>
    internal const string RolledBack = "rolledBack";

    /// <summary>Execution began and the final outcome cannot be proved.</summary>
    internal const string Unknown = "unknown";
}

/// <summary>Execution phase names reported with every outcome.</summary>
internal static class GraphPhase
{
    /// <summary>Argument or credential admission before any connection.</summary>
    internal const string Admission = "admission";

    /// <summary>Session creation and transaction start.</summary>
    internal const string Connection = "connection";

    /// <summary>Statement dispatch.</summary>
    internal const string Execution = "execution";

    /// <summary>Record streaming and summary retrieval.</summary>
    internal const string Consumption = "consumption";

    /// <summary>Commit acknowledgement.</summary>
    internal const string Commit = "commit";

    /// <summary>The operation finished.</summary>
    internal const string Completed = "completed";
}

/// <summary>Safe failure classifications returned to the model.</summary>
internal static class GraphFailureKind
{
    /// <summary>The credential was rejected.</summary>
    internal const string Authentication = "authentication";

    /// <summary>The account lacks the required database privilege.</summary>
    internal const string Authorization = "authorization";

    /// <summary>The server or a routing member could not be reached.</summary>
    internal const string Unavailable = "unavailable";

    /// <summary>A client or server deadline expired.</summary>
    internal const string Timeout = "timeout";

    /// <summary>The statement itself was rejected.</summary>
    internal const string InvalidStatement = "invalidStatement";

    /// <summary>A retryable server condition occurred. The extension never replays automatically.</summary>
    internal const string Transient = "transient";

    /// <summary>Another database failure occurred.</summary>
    internal const string Database = "database";

    /// <summary>The model arguments were rejected before any database work.</summary>
    internal const string InvalidArguments = "invalidArguments";

    /// <summary>The host did not admit the configured credential reference.</summary>
    internal const string CredentialUnavailable = "credentialUnavailable";
}

/// <summary>Reasons a bounded result stopped retaining data.</summary>
internal static class GraphLimit
{
    /// <summary>The configured retained-row limit was reached.</summary>
    internal const string Rows = "maximumRows";

    /// <summary>The configured retained-byte limit was reached.</summary>
    internal const string ResultBytes = "maximumResultBytes";

    /// <summary>At least one individual value was bounded during conversion.</summary>
    internal const string ValueSize = "maximumValueSize";
}

/// <summary>One statement, its validated parameters, and the limits applied while reading its result.</summary>
internal sealed record GraphStatement
{
    /// <summary>The Cypher text executed at the configured destination.</summary>
    public required string Cypher { get; init; }

    /// <summary>The validated parameter map. Values are JSON scalars, lists, or maps.</summary>
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }

    /// <summary>Maximum number of records retained.</summary>
    public required int MaximumRows { get; init; }

    /// <summary>Maximum serialized UTF-8 bytes of the retained records array, including punctuation.</summary>
    public required int MaximumRecordBytes { get; init; }
}

/// <summary>A classified database failure with no statement text or credential material.</summary>
/// <param name="Kind">The classification from <see cref="GraphFailureKind"/>.</param>
/// <param name="Code">The sanitized server error code when one is available.</param>
/// <param name="Detail">A fixed, safe explanation when no server code applies.</param>
internal sealed record GraphFailure(string Kind, string? Code, string? Detail = null);

/// <summary>Update counters reported by the server.</summary>
/// <param name="ContainsUpdates">Whether any counter is greater than zero.</param>
/// <param name="NodesCreated">Nodes created.</param>
/// <param name="NodesDeleted">Nodes deleted.</param>
/// <param name="RelationshipsCreated">Relationships created.</param>
/// <param name="RelationshipsDeleted">Relationships deleted.</param>
/// <param name="PropertiesSet">Properties set.</param>
/// <param name="LabelsAdded">Labels added.</param>
/// <param name="LabelsRemoved">Labels removed.</param>
/// <param name="IndexesAdded">Indexes added.</param>
/// <param name="IndexesRemoved">Indexes removed.</param>
/// <param name="ConstraintsAdded">Constraints added.</param>
/// <param name="ConstraintsRemoved">Constraints removed.</param>
internal sealed record GraphCounters(
    bool ContainsUpdates,
    int NodesCreated,
    int NodesDeleted,
    int RelationshipsCreated,
    int RelationshipsDeleted,
    int PropertiesSet,
    int LabelsAdded,
    int LabelsRemoved,
    int IndexesAdded,
    int IndexesRemoved,
    int ConstraintsAdded,
    int ConstraintsRemoved);

/// <summary>A bounded, safe server status entry.</summary>
/// <param name="Status">The GQLSTATUS value.</param>
/// <param name="Description">The bounded status description.</param>
/// <param name="Severity">The raw severity reported by the server.</param>
internal sealed record GraphStatus(string Status, string Description, string Severity);

/// <summary>The bounded outcome of one statement, including its explicit transaction state.</summary>
internal sealed record GraphExecutionResult
{
    /// <summary>The transaction outcome from <see cref="GraphOutcome"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>The phase the operation reached, from <see cref="GraphPhase"/>.</summary>
    public required string Phase { get; init; }

    /// <summary>Ordered column names.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Retained records in server order.</summary>
    public IReadOnlyList<JsonObject> Records { get; init; } = [];

    /// <summary>Whether the extension bounded the result.</summary>
    public bool Truncated { get; init; }

    /// <summary>The limits that were reached, from <see cref="GraphLimit"/>.</summary>
    public IReadOnlyList<string> LimitsReached { get; init; } = [];

    /// <summary>Update counters when the server supplied them.</summary>
    public GraphCounters? Counters { get; init; }

    /// <summary>Bounded server statuses.</summary>
    public IReadOnlyList<GraphStatus> Statuses { get; init; } = [];

    /// <summary>The classified failure, or <see langword="null"/> when the operation succeeded.</summary>
    public GraphFailure? Failure { get; init; }

    /// <summary>Creates a failed result that never began or never completed.</summary>
    /// <param name="outcome">The transaction outcome.</param>
    /// <param name="phase">The phase reached.</param>
    /// <param name="failure">The classified failure.</param>
    /// <returns>The failed result.</returns>
    internal static GraphExecutionResult Failed(string outcome, string phase, GraphFailure failure)
    {
        return new GraphExecutionResult { Outcome = outcome, Phase = phase, Failure = failure };
    }
}

/// <summary>One invocation-scoped connection to the configured database.</summary>
/// <remarks>
/// Implementations never throw for database failures: they return a result whose outcome and phase
/// describe exactly what the server confirmed. Only cancellation propagates.
/// </remarks>
internal interface IGraphSession : IAsyncDisposable
{
    /// <summary>Runs one statement in one explicit transaction.</summary>
    /// <param name="statement">The statement and its retained-result limits.</param>
    /// <param name="readOnly">Whether to request the server's read access mode.</param>
    /// <param name="cancellationToken">A token observed at record boundaries.</param>
    /// <returns>The bounded result and its confirmed transaction outcome.</returns>
    Task<GraphExecutionResult> RunAsync(
        GraphStatement statement,
        bool readOnly,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates invocation-scoped sessions. No credential survives the invocation that resolved it.</summary>
internal interface IGraphSessionFactory
{
    /// <summary>Creates a session without performing any network work.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="password">The credential revealed for this invocation.</param>
    /// <returns>The session.</returns>
    IGraphSession Create(Neo4jSettings settings, string password);
}
