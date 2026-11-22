namespace Threadsmith.Extensions.Neo4j;

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Threadsmith.Extensions.Abstractions;

/// <summary>Builds the versioned, machine-readable tool output.</summary>
/// <remarks>
/// Every result states its transaction outcome and execution phase separately from truncation and
/// tool failure, so reaching an output limit is never reported as a failed transaction.
/// </remarks>
internal static class Neo4jResults
{
    /// <summary>The output contract version.</summary>
    internal const int OutputSchemaVersion = 1;

    /// <summary>The complete payload bound, including the reserved envelope allowance.</summary>
    internal static int MaximumOutputBytes(Neo4jSettings settings) => settings.MaximumResultBytes + 8_192;

    /// <summary>Builds the result of one execution attempt.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="execution">The bounded execution outcome.</param>
    /// <returns>The host-owned tool result.</returns>
    internal static ExtensionToolResult FromExecution(Neo4jSettings settings, GraphExecutionResult execution)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(execution);
        var records = Nodes(execution.Records);
        var columns = Texts(execution.Columns);
        var statuses = Statuses(execution.Statuses);
        var limits = Texts(execution.LimitsReached);
        var payload = new JsonObject
        {
            ["schemaVersion"] = OutputSchemaVersion,
            ["outcome"] = execution.Outcome,
            ["phase"] = execution.Phase,
            ["readOnly"] = settings.ForceReadOnly,
            ["destination"] = settings.Destination,
            ["database"] = settings.Database,
            ["columns"] = columns,
            ["records"] = records,
            ["returnedRows"] = execution.Records.Count,
            ["truncated"] = execution.Truncated,
            ["limitsReached"] = limits,
            ["counters"] = Counters(execution.Counters),
            ["statuses"] = statuses,
            ["error"] = Error(execution.Failure),
        };
        var truncated = execution.Truncated;
        var maximumBytes = MaximumOutputBytes(settings);
        if (ByteCount(payload) > maximumBytes)
        {
            // Only presentation data is shed: a confirmed commit must never become unknown.
            truncated = true;
            payload["truncated"] = true;
            if (!execution.LimitsReached.Contains(GraphLimit.ResultBytes, StringComparer.Ordinal))
            {
                limits.Add(GraphLimit.ResultBytes);
            }

            var bytes = ByteCount(payload);
            TrimArray(statuses, ref bytes, maximumBytes);
            TrimArray(columns, ref bytes, maximumBytes);
            TrimArray(records, ref bytes, maximumBytes);
            payload["returnedRows"] = records.Count;
        }

        return new ExtensionToolResult
        {
            Succeeded = execution.Failure is null,
            ResultJson = payload.ToJsonString(),
            IsTruncated = truncated,
            Sources = [Provenance(settings)],
            Error = execution.Failure is null ? null : Describe(execution.Failure, execution.Outcome, execution.Phase),
        };
    }

    /// <summary>Builds the result of a request rejected before any database work.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="kind">The failure classification.</param>
    /// <param name="reason">The safe rejection reason.</param>
    /// <returns>The host-owned tool result.</returns>
    internal static ExtensionToolResult NotStarted(Neo4jSettings settings, string kind, string reason)
    {
        return FromExecution(
            settings,
            GraphExecutionResult.Failed(
                GraphOutcome.NotStarted,
                GraphPhase.Admission,
                new GraphFailure(kind, null, reason)));
    }

    /// <summary>Builds the schema discovery result.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="sampleLimit">The effective sample limit.</param>
    /// <param name="sections">The reported sections in request order.</param>
    /// <returns>The host-owned tool result.</returns>
    internal static ExtensionToolResult FromSchema(
        Neo4jSettings settings,
        int sampleLimit,
        IReadOnlyList<GraphSchemaSection> sections)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sections);
        var reported = new JsonObject();
        var truncated = false;
        foreach (var section in sections)
        {
            truncated |= section.Status is GraphSchemaStatus.Truncated;
            reported[section.Name] = section.ToJson();
        }

        var payload = new JsonObject
        {
            ["schemaVersion"] = OutputSchemaVersion,
            ["destination"] = settings.Destination,
            ["database"] = settings.Database,
            ["sampleLimit"] = sampleLimit,
            ["note"] = "Declared sections come from server metadata. Observed sections describe sampled data and cannot prove that a label, property, or relationship does not exist. A missing section is not an empty schema.",
            ["sections"] = reported,
        };
        var maximumBytes = MaximumOutputBytes(settings);
        if (ByteCount(payload) > maximumBytes)
        {
            payload["truncated"] = true;
            payload["limitsReached"] = Texts([GraphLimit.ResultBytes]);
            var bytes = ByteCount(payload);
            foreach (var section in reported.Select(pair => pair.Value).OfType<JsonObject>().Reverse())
            {
                if (bytes <= maximumBytes)
                {
                    break;
                }

                if (section["entries"] is not JsonArray { Count: > 0 } entries)
                {
                    continue;
                }

                // Reserve the status marker first. Returned-count changes only shorten JSON,
                // so the running count remains conservative while whole entries are removed.
                var previousBytes = ByteCount(section);
                section["status"] = "truncated";
                bytes += ByteCount(section) - previousBytes;
                TrimArray(entries, ref bytes, maximumBytes);
                section["returned"] = entries.Count;
            }

            truncated = true;
        }

        return new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = payload.ToJsonString(),
            IsTruncated = truncated,
            Sources = [Provenance(settings)],
        };
    }

    private static int ByteCount(JsonNode? value) => Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null");

    private static void TrimArray(JsonArray values, ref int bytes, int maximumBytes)
    {
        while (bytes > maximumBytes && values.Count > 0)
        {
            var last = values.Count - 1;
            bytes -= ByteCount(values[last]) + (last == 0 ? 0 : 1);
            values.RemoveAt(last);
        }
    }

    private static ExtensionProvenanceSource Provenance(Neo4jSettings settings)
    {
        return new ExtensionProvenanceSource("neo4j", settings.Destination, settings.Database);
    }

    private static string Describe(GraphFailure failure, string outcome, string phase)
    {
        var code = failure.Code is null ? string.Empty : $" code={failure.Code}";
        var detail = failure.Detail is null ? string.Empty : $" {failure.Detail}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Neo4j {failure.Kind} failure in phase '{phase}'; transaction outcome '{outcome}'.{code}{detail}");
    }

    private static JsonArray Texts(IReadOnlyList<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static JsonArray Nodes(IReadOnlyList<JsonObject> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static JsonNode? Counters(GraphCounters? counters)
    {
        if (counters is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["containsUpdates"] = counters.ContainsUpdates,
            ["nodesCreated"] = counters.NodesCreated,
            ["nodesDeleted"] = counters.NodesDeleted,
            ["relationshipsCreated"] = counters.RelationshipsCreated,
            ["relationshipsDeleted"] = counters.RelationshipsDeleted,
            ["propertiesSet"] = counters.PropertiesSet,
            ["labelsAdded"] = counters.LabelsAdded,
            ["labelsRemoved"] = counters.LabelsRemoved,
            ["indexesAdded"] = counters.IndexesAdded,
            ["indexesRemoved"] = counters.IndexesRemoved,
            ["constraintsAdded"] = counters.ConstraintsAdded,
            ["constraintsRemoved"] = counters.ConstraintsRemoved,
        };
    }

    private static JsonArray Statuses(IReadOnlyList<GraphStatus> statuses)
    {
        var result = new JsonArray();
        foreach (var status in statuses)
        {
            result.Add(new JsonObject
            {
                ["status"] = status.Status,
                ["description"] = status.Description,
                ["severity"] = status.Severity,
            });
        }

        return result;
    }

    private static JsonNode? Error(GraphFailure? failure)
    {
        if (failure is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["kind"] = failure.Kind,
            ["code"] = failure.Code,
            ["detail"] = failure.Detail,
        };
    }
}
