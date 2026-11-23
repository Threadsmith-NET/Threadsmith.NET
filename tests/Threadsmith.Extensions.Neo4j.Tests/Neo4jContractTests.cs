namespace Threadsmith.Extensions.Neo4j.Tests;

using Threadsmith.Extensions.Abstractions;
using Xunit;

/// <summary>Configuration, activation, metadata, and argument contracts for the Neo4j extension.</summary>
public sealed class Neo4jContractTests
{
    /// <summary>Unsafe or unrecognized configuration is rejected during activation.</summary>
    /// <param name="key">The setting to override or add.</param>
    /// <param name="value">The offending value.</param>
    [Theory]
    [InlineData("pool", "1")]
    [InlineData("uri", "neo4j+s://operator:hunter2@graph.example.com:7687")]
    [InlineData("uri", "https://graph.example.com:7687")]
    [InlineData("uri", "bolt://graph.example.com:7687")]
    [InlineData("readOnly", "yes")]
    [InlineData("maximumRows", "100000")]
    public void Settings_RejectUnsafeConfiguration(string key, string value)
    {
        var values = TestGraph.Values();
        values[key] = value;

        var failure = Assert.Throws<InvalidOperationException>(() => TestGraph.Settings(values));

        // The message names the rule, never the configured value.
        Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Absent optional settings fall back to the documented safe defaults.</summary>
    [Fact]
    public void Settings_ApplyDocumentedDefaults()
    {
        var values = TestGraph.Values();
        values.Remove("readOnly");
        values["uri"] = "neo4j+s://graph.example.com";

        var settings = TestGraph.Settings(values);

        Assert.True(settings.ReadOnly);
        Assert.True(settings.Routed);
        Assert.True(settings.Encrypted);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.QueryTimeout);
        Assert.Equal(500, settings.MaximumRows);
        Assert.Equal(65_536, settings.MaximumResultBytes);
        Assert.Equal(1_000, settings.SchemaSampleLimit);
        Assert.Equal("neo4j+s://graph.example.com:7687", settings.Destination);
    }

    /// <summary>Activation registers both tools and performs no connection or credential work.</summary>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Fact]
    public async Task Activation_RegistersBothToolsWithoutConnectingOrResolvingSecretsAsync()
    {
        var factory = new FakeGraphSessionFactory();
        var context = new TestActivationContext(TestGraph.Values());
        var extension = new Neo4jExtension(factory);

        await extension.ActivateAsync(context, TestContext.Current.CancellationToken);

        string[] registered =
            [.. context.RecordingCapabilities.Tools.Select(static tool => tool.Definition.Id).Order()];

        Assert.Equal(["neo4j_execute", "neo4j_schema"], registered);
        Assert.Equal(0, factory.Created);
        Assert.Empty(context.RecordingLogger.Errors);
        Assert.Equal(ExtensionTrustRequirement.FullyTrustedAutomation, extension.Descriptor.RequiredTrust);
    }

    /// <summary>Missing settings yield a safe activation diagnostic before any tool or connection exists.</summary>
    [Fact]
    public async Task Activation_ReportsMissingSettingWithoutRegisteringToolsAsync()
    {
        var factory = new FakeGraphSessionFactory();
        var context = new TestActivationContext([]);
        var extension = new Neo4jExtension(factory);

        var failure = await Assert.ThrowsAsync<ExtensionValidationException>(
            () => extension.ActivateAsync(context, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Neo4j extension setting 'uri' is required.", failure.Message);
        Assert.Empty(context.RecordingCapabilities.Tools);
        Assert.Equal(0, factory.Created);
    }

    /// <summary>Approval and side-effect metadata follow the configured driver mode.</summary>
    /// <param name="readOnly">The configured access mode.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecuteMetadata_MatchesConfiguredMode(bool readOnly)
    {
        var settings = TestGraph.Settings(TestGraph.Values(readOnly));
        var capability = new Neo4jExecuteCapability(settings, new FakeGraphSessionFactory());
        var definition = capability.Definition;

        Assert.Equal(ExtensionToolCategory.ExternalService, definition.Category);
        Assert.Equal(
            readOnly ? ExtensionToolSideEffect.ReadOnly : ExtensionToolSideEffect.WritesExternalData,
            definition.SideEffect);
        Assert.Equal(
            readOnly ? ExtensionToolIdempotency.Idempotent : ExtensionToolIdempotency.NonIdempotent,
            definition.Idempotency);
        Assert.Equal(
            readOnly ? ExtensionApprovalRequirement.None : ExtensionApprovalRequirement.User,
            definition.RequiredApproval);

        var review = Assert.IsType<ExtensionStatementReview>(definition.StatementReview);
        Assert.Equal(settings.Destination, review.Destination);
        Assert.Equal(settings.Database, review.Database);
        Assert.Equal("cypher", review.StatementProperty);
        Assert.Equal("parameters", review.ParametersProperty);

        // The declared reference comes from the generation, never from the arguments.
        Assert.Equal([TestGraph.PasswordReference], capability.GetSecretReferences("{}"));
        Assert.Equal(
            [TestGraph.PasswordReference],
            capability.GetSecretReferences("{\"passwordReference\":\"secrets:attacker:key\"}"));
        Assert.Null(capability.GetExecutable("{}"));
    }

    /// <summary>Connection, credential, and access-mode overrides are rejected with the other bounds.</summary>
    /// <param name="argumentsJson">The rejected arguments.</param>
    [Theory]
    [InlineData("{\"cypher\":\"RETURN 1\",\"uri\":\"bolt://evil.example.com:7687\"}")]
    [InlineData("{\"cypher\":\"RETURN 1\",\"readOnly\":false}")]
    [InlineData("{\"cypher\":\"RETURN 1\",\"password\":\"hunter2\"}")]
    [InlineData("{\"parameters\":{\"id\":1}}")]
    [InlineData("{\"cypher\":\"   \"}")]
    [InlineData("{\"cypher\":\"RETURN 1\",\"parameters\":{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":1}}}}}}}}}}")]
    public void Arguments_RejectConnectionAndModeOverrides(string argumentsJson)
    {
        var settings = TestGraph.Settings();

        Assert.Throws<Neo4jArgumentException>(() => Neo4jArguments.ReadStatement(argumentsJson, settings));
    }

    /// <summary>A schema request can lower the configured sample limit but never raise it.</summary>
    [Fact]
    public void SchemaRequest_LowersButNeverRaisesTheSampleLimit()
    {
        var values = TestGraph.Values();
        values["schemaSampleLimit"] = "100";
        var settings = TestGraph.Settings(values);

        Assert.Equal(10, Neo4jArguments.ReadSchemaRequest("{\"sampleLimit\":10}", settings).SampleLimit);
        Assert.Equal(100, Neo4jArguments.ReadSchemaRequest("{\"sampleLimit\":100000}", settings).SampleLimit);

        var defaulted = Neo4jArguments.ReadSchemaRequest("{}", settings);
        Assert.Equal(100, defaulted.SampleLimit);
        Assert.Equal(["nodeSamples", "relationshipEndpoints", "indexes"], defaulted.Sections);
        Assert.False(defaulted.ForceRefresh);
        Assert.True(Neo4jArguments.ReadSchemaRequest("""{"forceRefresh":true}""", settings).ForceRefresh);
        Assert.Throws<Neo4jArgumentException>(() => Neo4jArguments.ReadSchemaRequest("""{"forceRefresh":"true"}""", settings));

        Assert.Equal(
            ["indexes"],
            Neo4jArguments.ReadSchemaRequest("{\"sections\":[\"indexes\",\"indexes\"]}", settings).Sections);
        Assert.Throws<Neo4jArgumentException>(
            () => Neo4jArguments.ReadSchemaRequest("{\"sections\":[\"everything\"]}", settings));
    }

    /// <summary>The default read-only mode requires no operator approval.</summary>
    [Fact]
    public void ExecuteMetadata_DefaultReadOnlyNeedsNoApproval()
    {
        var values = TestGraph.Values();
        values.Remove("readOnly");
        var capability = new Neo4jExecuteCapability(
            TestGraph.Settings(values),
            new FakeGraphSessionFactory());

        Assert.Equal(ExtensionApprovalRequirement.None, capability.Definition.RequiredApproval);
    }

    /// <summary>The local-development exception never covers a remote self-signed endpoint.</summary>
    [Fact]
    public void Settings_RejectRemoteSelfSignedEndpointWhenLocalExceptionEnabled()
    {
        var values = TestGraph.Values();
        values["allowInsecureLocalConnections"] = "true";
        values["uri"] = "bolt+ssc://graph.example.com:7687";

        Assert.Throws<InvalidOperationException>(() => TestGraph.Settings(values));

        // Routing can advertise members on other hosts, so the exception excludes routed schemes.
        values["uri"] = "neo4j+ssc://localhost:7687";
        Assert.Throws<InvalidOperationException>(() => TestGraph.Settings(values));

        values["uri"] = "bolt+ssc://localhost:7687";
        Assert.True(TestGraph.Settings(values).Encrypted);
    }

    /// <summary>Integer parameters reach the statement as 64-bit integers, not rounded doubles.</summary>
    [Fact]
    public void Parameters_PreserveLargeIntegerPrecision()
    {
        var statement = Neo4jArguments.ReadStatement(
            "{\"cypher\":\"RETURN $id\",\"parameters\":{\"id\":9007199254740993,\"ratio\":1.5}}",
            TestGraph.Settings());

        Assert.Equal(9_007_199_254_740_993L, Assert.IsType<long>(statement.Parameters["id"]));
        Assert.Equal(1.5d, Assert.IsType<double>(statement.Parameters["ratio"]));

        // An integral literal beyond Int64 is rejected rather than rounded into a double.
        Assert.Throws<Neo4jArgumentException>(() => Neo4jArguments.ReadStatement(
            "{\"cypher\":\"RETURN $id\",\"parameters\":{\"id\":9223372036854775808}}",
            TestGraph.Settings()));
    }
}
