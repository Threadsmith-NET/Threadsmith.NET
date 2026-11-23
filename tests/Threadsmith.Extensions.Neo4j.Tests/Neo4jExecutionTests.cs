namespace Threadsmith.Extensions.Neo4j.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

/// <summary>Execution and schema outcomes reported through the host tool-result contract.</summary>
public sealed class Neo4jExecutionTests
{
    private const string _readQuery =
        "{\"cypher\":\"MATCH (p:Person {id: $id}) RETURN p.name\",\"parameters\":{\"id\":\"person-1\"}}";

    /// <summary>A bounded result keeps its confirmed commit separate from truncation.</summary>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Fact]
    public async Task Execute_KeepsCommitOutcomeSeparateFromTruncationAsync()
    {
        var session = new FakeGraphSession(new GraphExecutionResult
        {
            Outcome = GraphOutcome.Committed,
            Phase = GraphPhase.Completed,
            Columns = ["p.name"],
            Records = [new JsonObject { ["p.name"] = "Ada" }],
            Truncated = true,
            LimitsReached = [GraphLimit.Rows],
            Counters = new GraphCounters(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        });
        var factory = new FakeGraphSessionFactory(session);
        var capability = new Neo4jExecuteCapability(TestGraph.Settings(), factory);

        var result = await capability.ExecuteAsync(
            _readQuery,
            TestGraph.Context(new TestSecrets(TestGraph.Password)),
            TestContext.Current.CancellationToken);
        var payload = Payload(result.ResultJson);

        Assert.True(result.Succeeded);
        Assert.True(result.IsTruncated);
        Assert.Null(result.Error);
        Assert.Equal(GraphOutcome.Committed, (string?)payload["outcome"]);
        Assert.True((bool?)payload["truncated"]);
        Assert.Equal(1, (int?)payload["returnedRows"]);
        Assert.Equal(GraphLimit.Rows, (string?)payload["limitsReached"]!.AsArray()[0]);
        Assert.Null(payload["error"]);

        // The accepted arguments reached the session unchanged under the invocation-scoped credential.
        var statement = Assert.Single(session.Statements);
        Assert.Equal("MATCH (p:Person {id: $id}) RETURN p.name", statement.Cypher);
        Assert.Equal("person-1", statement.Parameters["id"]);
        Assert.Equal(TestGraph.Password, factory.Password);
        Assert.True(session.Disposed);
    }

    /// <summary>An unproven commit is reported as unknown and is never replayed.</summary>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Fact]
    public async Task Execute_ReportsUnknownCommitWithoutReplayAsync()
    {
        var session = new FakeGraphSession(GraphExecutionResult.Failed(
            GraphOutcome.Unknown,
            GraphPhase.Commit,
            new GraphFailure(GraphFailureKind.Transient, "Neo.TransientError.Transaction.Terminated")));
        var factory = new FakeGraphSessionFactory(session);
        var settings = TestGraph.Settings(TestGraph.Values(readOnly: false));
        var capability = new Neo4jExecuteCapability(settings, factory);

        var result = await capability.ExecuteAsync(
            "{\"cypher\":\"CREATE (p:Person {id: $id})\",\"parameters\":{\"id\":\"person-1\"}}",
            TestGraph.Context(new TestSecrets(TestGraph.Password)),
            TestContext.Current.CancellationToken);
        var payload = Payload(result.ResultJson);

        Assert.False(result.Succeeded);
        Assert.Equal(GraphOutcome.Unknown, (string?)payload["outcome"]);
        Assert.Equal(GraphPhase.Commit, (string?)payload["phase"]);
        Assert.Equal(GraphFailureKind.Transient, (string?)payload["error"]!["kind"]);
        Assert.Single(session.Statements);
        Assert.True(session.Disposed);
        Assert.DoesNotContain(TestGraph.Password, result.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(TestGraph.Password, result.Error, StringComparison.Ordinal);
    }

    /// <summary>A denied or unresolvable credential stops the operation before any database work.</summary>
    /// <param name="admitted">Whether the host supplied a secret service at all.</param>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_ReturnsNotStartedWhenTheCredentialIsUnavailableAsync(bool admitted)
    {
        var factory = new FakeGraphSessionFactory();
        var capability = new Neo4jExecuteCapability(TestGraph.Settings(), factory);

        var result = await capability.ExecuteAsync(
            _readQuery,
            TestGraph.Context(admitted ? new TestSecrets(null) : null),
            TestContext.Current.CancellationToken);
        var payload = Payload(result.ResultJson);

        Assert.False(result.Succeeded);
        Assert.Equal(GraphOutcome.NotStarted, (string?)payload["outcome"]);
        Assert.Equal(GraphPhase.Admission, (string?)payload["phase"]);
        Assert.Equal(GraphFailureKind.CredentialUnavailable, (string?)payload["error"]!["kind"]);
        Assert.Equal(0, factory.Created);
    }

    /// <summary>Schema output separates declared rules from observations and states each section limit.</summary>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Fact]
    public async Task Schema_SeparatesDeclaredFromObservedAndReportsSectionLimitsAsync()
    {
        var session = new FakeGraphSession(
            GraphExecutionResult.Failed(
                GraphOutcome.NotStarted,
                GraphPhase.Execution,
                new GraphFailure(GraphFailureKind.Authorization, "Neo.ClientError.Security.Forbidden")),
            new GraphExecutionResult
            {
                Outcome = GraphOutcome.Committed,
                Phase = GraphPhase.Completed,
                Columns = ["nodeLabels", "propertyName", "propertyTypes"],
                Records = [new JsonObject { ["propertyName"] = "name" }],
            },
            new GraphExecutionResult
            {
                Outcome = GraphOutcome.Committed,
                Phase = GraphPhase.Completed,
                Records = [Payload("""{"relationshipType":"WORKS_AT","endpoints":[{"sourceLabels":["Person"],"targetLabels":["Company"]}]}""")],
            });
        var factory = new FakeGraphSessionFactory(session);
        var values = TestGraph.Values(readOnly: false);
        values["schemaSampleLimit"] = "250";
        var capability = new Neo4jSchemaCapability(TestGraph.Settings(values), factory);

        var result = await capability.ExecuteAsync(
            "{\"sections\":[\"constraints\",\"nodes\",\"relationshipEndpoints\"]}",
            TestGraph.Context(new TestSecrets(TestGraph.Password)),
            TestContext.Current.CancellationToken);
        var sections = Payload(result.ResultJson)["sections"]!;

        Assert.True(result.Succeeded);
        Assert.Equal("declared", (string?)sections["constraints"]!["kind"]);
        Assert.Equal("permissionDenied", (string?)sections["constraints"]!["status"]);
        Assert.Equal("SHOW CONSTRAINTS", (string?)sections["constraints"]!["sourceMethod"]);
        Assert.Equal("observed", (string?)sections["nodes"]!["kind"]);
        Assert.Equal("complete", (string?)sections["nodes"]!["status"]);
        Assert.Equal(250, (int?)sections["nodes"]!["limit"]);
        Assert.Equal(1, (int?)sections["nodes"]!["returned"]);
        var endpoints = sections["relationshipEndpoints"]!;
        Assert.Equal("complete", (string?)endpoints["status"]);
        var pair = endpoints["entries"]![0]!["endpoints"]![0]!;
        Assert.Equal("Person", (string?)pair["sourceLabels"]![0]);
        Assert.Equal("Company", (string?)pair["targetLabels"]![0]);
        Assert.Equal(3, session.Statements.Count);
        Assert.All(session.ReadOnlyRequests, Assert.True);
    }

    /// <summary>Whole payload sizing cannot turn a confirmed commit into a host output-limit failure.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_CompletePayloadFitsBoundAndPreservesCommit(bool manySmallRecords)
    {
        var values = TestGraph.Values(readOnly: false);
        values["maximumRows"] = "10000";
        values["maximumResultBytes"] = manySmallRecords ? "262144" : "4096";
        JsonObject[] records = manySmallRecords
            ? [.. Enumerable.Range(0, 9709).Select(_ => new JsonObject { ["abcdefghijklmnopqrstu"] = 1 })]
            : [new JsonObject { ["value"] = "retained" }];
        var execution = new GraphExecutionResult
        {
            Outcome = GraphOutcome.Committed,
            Phase = GraphPhase.Completed,
            Records = records,
            Columns = manySmallRecords ? ["abcdefghijklmnopqrstu"] : [new string('<', 3000), "value"],
            Statuses = manySmallRecords ? [] : Enumerable.Range(0, 16).Select(_ => new GraphStatus("00000", new string('<', 512), "INFORMATION")).ToArray(),
            Counters = new GraphCounters(true, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        };
        var session = new FakeGraphSession(execution);
        var capability = new Neo4jExecuteCapability(TestGraph.Settings(values), new FakeGraphSessionFactory(session));

        var result = await capability.ExecuteAsync("{\"cypher\":\"CREATE () RETURN 1\"}", TestGraph.Context(new TestSecrets(TestGraph.Password)), TestContext.Current.CancellationToken);
        var payload = Payload(result.ResultJson);
        // Match the proxy's decoded JsonElement and the pipeline's default JSON serialization.
        using var decoded = JsonDocument.Parse(result.ResultJson!);
        var hostBytes = JsonSerializer.SerializeToUtf8Bytes(decoded.RootElement).Length;

        Assert.InRange(hostBytes, 1, capability.Definition.MaximumOutputBytes);
        Assert.True(result.Succeeded);
        Assert.True(result.IsTruncated);
        Assert.True((bool?)payload["truncated"]);
        Assert.Equal(GraphOutcome.Committed, (string?)payload["outcome"]);
        Assert.Equal(GraphPhase.Completed, (string?)payload["phase"]);
        Assert.Equal(1, (int?)payload["counters"]!["nodesCreated"]);
        Assert.Equal(payload["records"]!.AsArray().Count, (int?)payload["returnedRows"]);
        Assert.Contains(payload["limitsReached"]!.AsArray(), limit => (string?)limit == GraphLimit.ResultBytes);
        Assert.Single(session.Statements);
        Assert.True(session.Disposed);
    }

    /// <summary>Aggregate schema output retains section meaning and accurate counts when trimmed.</summary>
    [Fact]
    public async Task Schema_CompletePayloadFitsBound()
    {
        static GraphExecutionResult OversizedSection() => new()
        {
            Outcome = GraphOutcome.Committed,
            Phase = GraphPhase.Completed,
            Records = Enumerable.Range(0, 8).Select(_ => new JsonObject { ["name"] = new string('<', 512) }).ToArray(),
        };
        var values = TestGraph.Values();
        values["maximumResultBytes"] = "4096";
        var session = new FakeGraphSession(
            OversizedSection(),
            GraphExecutionResult.Failed(GraphOutcome.NotStarted, GraphPhase.Execution, new GraphFailure(GraphFailureKind.Authorization, "Neo.ClientError.Security.Forbidden")),
            OversizedSection(),
            new GraphExecutionResult { Outcome = GraphOutcome.Committed, Phase = GraphPhase.Completed });
        var capability = new Neo4jSchemaCapability(TestGraph.Settings(values), new FakeGraphSessionFactory(session));

        var result = await capability.ExecuteAsync("{\"sections\":[\"constraints\",\"indexes\",\"nodes\",\"relationshipEndpoints\"]}", TestGraph.Context(new TestSecrets(TestGraph.Password)), TestContext.Current.CancellationToken);
        var payload = Payload(result.ResultJson);
        using var decoded = JsonDocument.Parse(result.ResultJson!);
        var hostBytes = JsonSerializer.SerializeToUtf8Bytes(decoded.RootElement).Length;

        Assert.InRange(hostBytes, 1, capability.Definition.MaximumOutputBytes);
        Assert.True(result.Succeeded);
        Assert.True(result.IsTruncated);
        var sections = payload["sections"]!.AsObject();
        Assert.Equal(4, sections.Count);
        Assert.Equal("permissionDenied", (string?)sections["indexes"]!["status"]);
        Assert.Equal("complete", (string?)sections["relationshipEndpoints"]!["status"]);
        foreach (var name in new[] { "constraints", "nodes" })
        {
            var section = sections[name]!;
            Assert.Equal("truncated", (string?)section["status"]);
            Assert.Equal(section["entries"]!.AsArray().Count, (int?)section["returned"]);
            Assert.NotNull(section["sourceMethod"]);
        }
    }

    /// <summary>Compact discovery reuses successful sections and preserves them across failed refreshes.</summary>
    /// <returns>A task that completes when the assertions finish.</returns>
    [Fact]
    public async Task Schema_DefaultReusesSnapshotUntilUserRefreshAsync()
    {
        static GraphExecutionResult Row(string json) => new()
        {
            Outcome = GraphOutcome.Committed,
            Phase = GraphPhase.Completed,
            Records = [Payload(json)],
        };
        const string indexes = """{"sections":["indexes"]}""";
        const string refresh = """{"sections":["indexes"],"forceRefresh":true}""";
        var session = new FakeGraphSession(
            Row("""{"label":"Person","properties":[{"name":"age","types":["INTEGER NOT NULL"]}]}"""),
            Row("""{"relationshipType":"WORKS_AT"}"""),
            Row("""{"name":"old_index"}"""),
            Row("""{"name":"new_index"}"""),
            GraphExecutionResult.Failed(GraphOutcome.NotStarted, GraphPhase.Execution, new GraphFailure(GraphFailureKind.Timeout, null)));
        var capability = new Neo4jSchemaCapability(TestGraph.Settings(), new FakeGraphSessionFactory(session));
        var context = TestGraph.Context(new TestSecrets(TestGraph.Password));
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(TimeSpan.FromMinutes(5), capability.Definition.Timeout);
        var first = await capability.ExecuteAsync("{}", context, token);
        Assert.Equal(first.ResultJson, (await capability.ExecuteAsync("{}", context, token)).ResultJson);
        Assert.Contains("old_index", (await capability.ExecuteAsync(indexes, context, token)).ResultJson, StringComparison.Ordinal);
        Assert.Collection(
            session.Statements,
            s => Assert.Contains("db.labels()", s.Cypher, StringComparison.Ordinal),
            s => Assert.Contains("db.relationshipTypes()", s.Cypher, StringComparison.Ordinal),
            s => Assert.StartsWith("SHOW INDEXES", s.Cypher, StringComparison.Ordinal));
        foreach (var statement in session.Statements.Take(2))
        {
            var sample = statement.Cypher.IndexOf("WITH entity LIMIT $entityBudget", StringComparison.Ordinal);
            var expand = statement.Cypher.IndexOf("UNWIND names AS name", StringComparison.Ordinal);
            Assert.True(sample >= 0 && sample < expand);
            Assert.DoesNotContain("MATCH", statement.Cypher[expand..], StringComparison.Ordinal);
            Assert.Contains("valueType(e[k])", statement.Cypher, StringComparison.Ordinal);
            Assert.Equal(256, statement.Parameters["entityBudget"]);
            Assert.Equal(8, statement.Parameters["perName"]);
        }

        var updated = await capability.ExecuteAsync(refresh, context, token);
        Assert.Contains("new_index", updated.ResultJson, StringComparison.Ordinal);
        var failed = await capability.ExecuteAsync(refresh, context, token);
        Assert.Equal("unavailable", (string?)Payload(failed.ResultJson)["sections"]!["indexes"]!["status"]);
        Assert.Equal(updated.ResultJson, (await capability.ExecuteAsync(indexes, context, token)).ResultJson);
        Assert.Equal(5, session.Statements.Count);
    }

    private static JsonObject Payload(string? resultJson)
    {
        return JsonNode.Parse(Assert.IsType<string>(resultJson))!.AsObject();
    }
}
