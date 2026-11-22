namespace Threadsmith.Extensions.Neo4j.Tests;

using Threadsmith.Extensions.Abstractions;

/// <summary>Shared in-memory doubles. No test opens a socket, a file, or a database.</summary>
internal static class TestGraph
{
    /// <summary>The credential the fake secret service reveals.</summary>
    internal const string Password = "super-secret-value";

    /// <summary>The configured logical reference for that credential.</summary>
    internal const string PasswordReference = "secrets:neo4j:primary:password";

    /// <summary>Builds a complete, valid settings section.</summary>
    /// <param name="forceReadOnly">The configured access mode.</param>
    /// <returns>The settings values.</returns>
    internal static Dictionary<string, string> Values(bool forceReadOnly = true)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["uri"] = "neo4j+s://graph.example.com:7687",
            ["database"] = "neo4j",
            ["username"] = "threadsmith",
            ["passwordReference"] = PasswordReference,
            ["forceReadOnly"] = forceReadOnly ? "true" : "false",
        };
    }

    /// <summary>Builds validated settings from a values map.</summary>
    /// <param name="values">The settings values, or <see langword="null"/> for the valid defaults.</param>
    /// <returns>The validated settings.</returns>
    internal static Neo4jSettings Settings(Dictionary<string, string>? values = null)
    {
        return Neo4jSettings.Create(new TestConfiguration(values ?? Values()));
    }

    /// <summary>Builds an invocation context.</summary>
    /// <param name="secrets">The admitted secret service, or <see langword="null"/> when denied.</param>
    /// <returns>The invocation context.</returns>
    internal static ExtensionToolInvocationContext Context(IExtensionSecretAccess? secrets)
    {
        return new ExtensionToolInvocationContext { RequestedBy = "test", Secrets = secrets };
    }
}

/// <summary>A dictionary-backed configuration view.</summary>
internal sealed class TestConfiguration : IExtensionConfiguration
{
    private readonly Dictionary<string, string> _values;

    /// <summary>Initializes a new instance of the <see cref="TestConfiguration"/> class.</summary>
    /// <param name="values">The settings values.</param>
    internal TestConfiguration(Dictionary<string, string> values)
    {
        _values = values;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Keys => [.. _values.Keys];

    /// <inheritdoc />
    public string? this[string key] => _values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>Records log messages instead of writing them.</summary>
internal sealed class TestLogger : IExtensionLogger
{
    /// <summary>The recorded informational and warning messages.</summary>
    internal List<string> Messages { get; } = [];

    /// <summary>The recorded error messages.</summary>
    internal List<string> Errors { get; } = [];

    /// <inheritdoc />
    public void Information(string message) => Messages.Add(message);

    /// <inheritdoc />
    public void Warning(string message) => Messages.Add(message);

    /// <inheritdoc />
    public void Error(string message) => Errors.Add(message);
}

/// <summary>Captures the capabilities an extension registers.</summary>
internal sealed class TestRegistrar : IExtensionCapabilityRegistrar
{
    /// <summary>The registered tool capabilities, in registration order.</summary>
    internal List<IToolCapability> Tools { get; } = [];

    /// <inheritdoc />
    public void RegisterTool(IToolCapability capability) => Tools.Add(capability);

    /// <inheritdoc />
    public void RegisterModelPreferenceContributor(IModelPreferenceContributor contributor)
    {
    }
}

/// <summary>A minimal activation context.</summary>
internal sealed class TestActivationContext : IExtensionActivationContext
{
    /// <summary>Initializes a new instance of the <see cref="TestActivationContext"/> class.</summary>
    /// <param name="values">The settings values.</param>
    internal TestActivationContext(Dictionary<string, string> values)
    {
        Configuration = new TestConfiguration(values);
    }

    /// <inheritdoc />
    public ExtensionHostInformation HostInformation { get; } = new()
    {
        HostVersion = "0.0.0-test",
        ContractVersion = ExtensionContractVersion.Current,
    };

    /// <inheritdoc />
    public IExtensionConfiguration Configuration { get; }

    /// <summary>The recording logger.</summary>
    public TestLogger RecordingLogger { get; } = new();

    /// <inheritdoc />
    public IExtensionLogger Logger => RecordingLogger;

    /// <summary>The recording registrar.</summary>
    public TestRegistrar RecordingCapabilities { get; } = new();

    /// <inheritdoc />
    public IExtensionCapabilityRegistrar Capabilities => RecordingCapabilities;

    /// <inheritdoc />
    public CancellationToken Lifetime => CancellationToken.None;
}

/// <summary>Reveals or denies the configured credential without touching a secret store.</summary>
internal sealed class TestSecrets : IExtensionSecretAccess
{
    private readonly string? _value;

    /// <summary>Initializes a new instance of the <see cref="TestSecrets"/> class.</summary>
    /// <param name="value">The revealed value, or <see langword="null"/> to deny resolution.</param>
    internal TestSecrets(string? value)
    {
        _value = value;
    }

    /// <summary>The references the caller asked for.</summary>
    internal List<string> Requested { get; } = [];

    /// <inheritdoc />
    public Task<ExtensionSecretValue> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        Requested.Add(reference);
        return _value is null
            ? Task.FromException<ExtensionSecretValue>(new InvalidOperationException("The host denied the reference."))
            : Task.FromResult(new ExtensionSecretValue(_value));
    }
}

/// <summary>Returns scripted results and records everything the capability asked of it.</summary>
internal sealed class FakeGraphSession : IGraphSession
{
    private readonly Queue<GraphExecutionResult> _results;

    /// <summary>Initializes a new instance of the <see cref="FakeGraphSession"/> class.</summary>
    /// <param name="results">The scripted results, consumed in call order.</param>
    internal FakeGraphSession(params GraphExecutionResult[] results)
    {
        _results = new Queue<GraphExecutionResult>(results);
    }

    /// <summary>The statements the capability ran, in call order.</summary>
    internal List<GraphStatement> Statements { get; } = [];

    /// <summary>The access mode requested for each call.</summary>
    internal List<bool> ReadOnlyRequests { get; } = [];

    /// <summary>Whether the session was disposed.</summary>
    internal bool Disposed { get; private set; }

    /// <inheritdoc />
    public Task<GraphExecutionResult> RunAsync(
        GraphStatement statement,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        Statements.Add(statement);
        ReadOnlyRequests.Add(readOnly);
        return Task.FromResult(_results.Dequeue());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }
}

/// <summary>Hands out one fake session and records the credential it received.</summary>
internal sealed class FakeGraphSessionFactory : IGraphSessionFactory
{
    /// <summary>Initializes a new instance of the <see cref="FakeGraphSessionFactory"/> class.</summary>
    /// <param name="session">The session to return, or <see langword="null"/> for an unscripted one.</param>
    internal FakeGraphSessionFactory(FakeGraphSession? session = null)
    {
        Session = session ?? new FakeGraphSession();
    }

    /// <summary>The number of sessions created.</summary>
    internal int Created { get; private set; }

    /// <summary>The credential passed to the most recent session.</summary>
    internal string? Password { get; private set; }

    /// <summary>The session this factory hands out.</summary>
    internal FakeGraphSession Session { get; }

    /// <inheritdoc />
    public IGraphSession Create(Neo4jSettings settings, string password)
    {
        Created++;
        Password = password;
        return Session;
    }
}
