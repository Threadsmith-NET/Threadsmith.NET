namespace Threadsmith.RepositoryIntelligence;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Focused semantic operations over the same bounded execution and evidence path.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RepositoryInterpretationKind>))]
internal enum RepositoryInterpretationKind
{
    Significance,
    Intent,
    FailureOrReversal,
    Reevaluation,
    Relationships,
    CrossEpisodeSynthesis,
}

/// <summary>Independent candidate assessment dimensions; identifiers are invocation-local only.</summary>
internal sealed record RepositoryInterpretationCandidate(
    string Key,
    string Kind,
    string Title,
    string Statement,
    string Applicability,
    string Confidence,
    string EvidenceClass,
    string? Uncertainty,
    string? Then,
    string? Replacement,
    string? AtTarget,
    bool CausalClaim,
    string? RationaleQuote,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> ConflictingEvidenceIds);

/// <summary>Typed directed relation between candidates in this response, never durable identities.</summary>
internal sealed record RepositoryInterpretationRelationship(
    string From, string To, string Kind, string Explanation, string Confidence, string? RationaleQuote, IReadOnlyList<string> EvidenceIds);

/// <summary>One bounded internal expansion; it cannot supply scope, revisions or a collection mode.</summary>
internal sealed record RepositoryInterpretationExpansion(string Kind, string? EvidenceId, int? StartLine, int? EndLine);

/// <summary>Independent evidence assessment shared by answers and candidate validation.</summary>
internal sealed record RepositoryInterpretationAssessment(
    string Applicability,
    string Confidence,
    string EvidenceClass,
    string? Uncertainty,
    string? Then,
    string? Replacement,
    string? AtTarget,
    bool CausalClaim,
    string? RationaleQuote);

/// <summary>Schema-bound response, validated before returning to any caller.</summary>
internal sealed record RepositoryInterpretationResponse(
    string OperationId,
    string Outcome,
    string? Answer,
    RepositoryInterpretationAssessment? AnswerAssessment,
    IReadOnlyList<string> SupportingEvidenceIds,
    IReadOnlyList<string> ConflictingEvidenceIds,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<RepositoryInterpretationCandidate> Candidates,
    IReadOnlyList<RepositoryInterpretationRelationship> Relationships,
    RepositoryInterpretationExpansion? Expansion);

/// <summary>Validated interpretation plus host-owned provenance and cumulative consumption.</summary>
internal sealed record RepositoryInterpretationResult(
    string OperationId,
    RepositoryInterpretationKind Kind,
    RepositoryInterpretationResponse Response,
    BoundedModelSelection? Model,
    int ModelCalls,
    long ModelInputBytes,
    long ModelOutputBytes,
    IReadOnlyList<RepositoryEvidenceExcerpt> Evidence,
    IReadOnlyList<RepositoryEvidenceOmission> Omissions,
    RepositoryEvidencePacket LastPacket,
    IReadOnlyList<RepositoryEpisodeCandidate>? Episodes = null);

/// <summary>Invocation-only interpretation, validation and bounded evidence continuation.</summary>
internal sealed class RepositoryInterpreter
{
    private static readonly string[] Outcomes = ["Findings", "InsufficientEvidence", "NoSignificantFinding"];
    private static readonly string[] CandidateKinds = ["Decision", "Constraint", "Convention", "Migration", "Reversal", "HistoricalFailure", "PersistentPattern", "OpenTension"];
    private static readonly string[] Applicabilities = ["Current", "Superseded", "PartiallySuperseded", "HistoricalOnly", "Uncertain"];
    private static readonly string[] EvidenceClasses = ["Documented", "StronglyReconstructed", "Inferred"];
    private static readonly string[] Confidences = ["Low", "Medium", "High"];
    private static readonly string[] RelationshipKinds = ["Motivation", "Preservation", "Supersession", "Reversal", "Correction", "Implementation", "Reinforcement", "Association"];
    private static readonly string[] ExpansionKinds = ["Inspect", "NextPacket"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
    };

    private readonly IBoundedModelInference _inference;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="RepositoryInterpreter"/> class.</summary>
    internal RepositoryInterpreter(IBoundedModelInference inference, IPromptLoader prompts)
    {
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
    }

    /// <summary>Interprets only the live collector's evidence under the admitted original scope.</summary>
    internal async Task<RepositoryInterpretationResult> InterpretAsync(
        RepositoryInterpretationKind kind,
        RepositoryEvidenceCollector collector,
        RepositoryEvidenceSelection selection,
        RepositoryEvidencePacket initial,
        ToolExecutionContext context,
        int maximumCalls,
        IBoundedModelOperation? admittedOperation = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var operationId = Guid.NewGuid().ToString("N");
        maximumCalls = Math.Clamp(maximumCalls, 0, 32);
        var packet = initial;
        var maximumInferenceBytes = Math.Min(selection.MaximumOutputBytes, initial.Selection.MaximumOutputBytes);
        var evidence = initial.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var packets = new List<RepositoryEvidencePacket>();
        var calls = 0;
        long inputBytes = 0;
        long outputBytes = 0;
        var operation = admittedOperation;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumCalls < 1)
            {
                return Limited("Unavailable", "The invocation does not allow model calls.");
            }

            operation ??= _inference.Open(context, Math.Min(maximumCalls, 32), 32768);
            var instructions = _prompts.Get(PromptFileNames.RepositoryIntelligenceInterpretation);
            while (calls < maximumCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in packet.Evidence)
                {
                    evidence.TryAdd(item.Id, item);
                }

                packets.Add(packet);
                var data = JsonSerializer.Serialize(new { OperationId = operationId, Kind = kind.ToString(), Packets = packets });
                var bytes = Encoding.UTF8.GetByteCount(data);
                if (bytes > maximumInferenceBytes - inputBytes)
                {
                    return Limited("InsufficientEvidence", "Cumulative inference input allowance is exhausted; narrow the question.");
                }

                inputBytes += bytes;
                calls++;
                var json = await operation.ExecuteAsync(new BoundedModelRequest(instructions, data, "repository-interpretation-v1", ResponseSchema, Math.Min(4096, operation.MaximumOutputTokens)), cancellationToken);
                outputBytes += Encoding.UTF8.GetByteCount(json);
                if (outputBytes > maximumInferenceBytes)
                {
                    return Limited("InsufficientEvidence", "Cumulative inference output allowance is exhausted.");
                }

                RepositoryInterpretationResponse response;
                try
                {
                    response = JsonSerializer.Deserialize<RepositoryInterpretationResponse>(json, JsonOptions)
                        ?? throw new InvalidDataException("The interpretation result is empty.");
                    Validate(response, operationId, evidence, initial.Target.Commit);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
                {
                    return Limited("InsufficientEvidence", "The model response failed host schema or evidence validation; no proposed finding was accepted.");
                }

                if (response.Expansion is not { } expansion)
                {
                    return Result(response);
                }

                if (calls >= maximumCalls)
                {
                    return Limited("InsufficientEvidence", "The interpretation requested detail after its model-call allowance was exhausted.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (expansion.Kind == "Inspect" && expansion.EvidenceId is { } id && evidence.ContainsKey(id)
                    && expansion.StartLine is { } start && expansion.EndLine is { } end)
                {
                    packet = collector.Inspect(id, start, end, cancellationToken);
                }
                else if (expansion.Kind == "NextPacket" && expansion.EvidenceId is null && expansion.StartLine is null && expansion.EndLine is null
                    && packet.Continuation is { } continuation)
                {
                    packet = await collector.CollectAsync(selection, continuation, cancellationToken);
                }
                else
                {
                    return Limited("InsufficientEvidence", "Requested evidence is unknown, expired, outside scope, or unavailable in the admitted collection mode.");
                }
            }

            return Limited("InsufficientEvidence", "The bounded model-call allowance is exhausted.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            return Limited("Unavailable", "Inference or bounded evidence expansion is unavailable; deterministic evidence remains usable.");
        }
        finally
        {
            operation?.Dispose();
        }

        RepositoryInterpretationResult Limited(string outcome, string reason) => Result(new(operationId, outcome, null, null, [], [], [reason], [], [], null));
        RepositoryInterpretationResult Result(RepositoryInterpretationResponse response)
        {
            var collected = packets.Prepend(initial).ToArray();
            var episodes = collected.SelectMany(item => item.Episodes).GroupBy(item => item.Id, StringComparer.Ordinal)
                .Select(group => group.First() with
                {
                    EvidenceIds = group.SelectMany(item => item.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray(),
                    Signals = group.SelectMany(item => item.Signals).Distinct(StringComparer.Ordinal).ToArray(),
                }).ToArray();
            return new(operationId, kind, response, operation?.Selection, calls, inputBytes, outputBytes, evidence.Values.ToArray(), collected.SelectMany(item => item.Omissions).Distinct().ToArray(), packet, episodes);
        }
    }

    /// <summary>Reuses interpretation admission checks before any retained candidate is reconciled.</summary>
    internal static void Validate(RepositoryInterpretationResponse response, string operationId, IReadOnlyDictionary<string, RepositoryEvidenceExcerpt> evidence, string? targetCommit)
    {
        if (response.OperationId != operationId || !Outcomes.Contains(response.Outcome, StringComparer.Ordinal)
            || response.SupportingEvidenceIds is null || response.ConflictingEvidenceIds is null || response.Limitations is null
            || response.Candidates is null || response.Relationships is null || response.Candidates.Count > 16 || response.Relationships.Count > 32
            || response.Limitations.Count > 16 || response.Limitations.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            || response.Answer?.Length > 4096 || (response.Outcome == "Findings" && string.IsNullOrWhiteSpace(response.Answer)))
        {
            throw new InvalidDataException("Invalid interpretation envelope.");
        }

        References(response.SupportingEvidenceIds);
        References(response.ConflictingEvidenceIds);
        if ((response.Answer is not null && response.SupportingEvidenceIds.Count == 0)
            || (response.Answer is null) != (response.AnswerAssessment is null)
            || (response.Outcome != "Findings" && (response.Candidates.Count != 0 || response.Relationships.Count != 0)))
        {
            throw new InvalidDataException("An answer requires admitted supporting evidence.");
        }

        if (response.AnswerAssessment is { } answerAssessment)
        {
            Assessment(answerAssessment, response.SupportingEvidenceIds, response.ConflictingEvidenceIds);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in response.Candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Key) || candidate.Key.Length > 64 || !keys.Add(candidate.Key)
                || !CandidateKinds.Contains(candidate.Kind, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(candidate.Title) || candidate.Title.Length > 256
                || string.IsNullOrWhiteSpace(candidate.Statement) || candidate.Statement.Length > 2048
                || candidate.EvidenceIds is null || candidate.ConflictingEvidenceIds is null || candidate.EvidenceIds.Count == 0)
            {
                throw new InvalidDataException("Invalid interpretation candidate.");
            }

            References(candidate.EvidenceIds);
            References(candidate.ConflictingEvidenceIds);
            Assessment(new RepositoryInterpretationAssessment(candidate.Applicability, candidate.Confidence, candidate.EvidenceClass, candidate.Uncertainty, candidate.Then, candidate.Replacement, candidate.AtTarget, candidate.CausalClaim, candidate.RationaleQuote), candidate.EvidenceIds, candidate.ConflictingEvidenceIds);
        }

        foreach (var relation in response.Relationships)
        {
            if (relation is null || !keys.Contains(relation.From) || !keys.Contains(relation.To) || relation.From == relation.To
                || !RelationshipKinds.Contains(relation.Kind, StringComparer.Ordinal)
                || !Confidences.Contains(relation.Confidence, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(relation.Explanation) || relation.Explanation.Length > 1024
                || relation.EvidenceIds is null || relation.EvidenceIds.Count == 0)
            {
                throw new InvalidDataException("Invalid or unsupported relationship endpoints or causal relationship.");
            }

            References(relation.EvidenceIds);
            if ((relation.Kind == "Motivation" && relation.RationaleQuote is not { Length: >= 12 and <= 1024 })
                || (relation.RationaleQuote is { } quote && !relation.EvidenceIds.Select(id => evidence[id]).Any(item => item.Source.Kind is "Document" or "CommitMetadata"
                    && item.Text?.Contains(quote, StringComparison.Ordinal) == true)))
            {
                throw new InvalidDataException("Supplied relationship quotations require exact documented support; motivation cannot rely on temporal adjacency.");
            }
        }

        if (response.Expansion is { } expansion && (!ExpansionKinds.Contains(expansion.Kind, StringComparer.Ordinal)
            || response.Candidates.Count != 0 || response.Relationships.Count != 0))
        {
            throw new InvalidDataException("Expansion cannot publish candidate interpretations.");
        }

        void References(IReadOnlyList<string> ids)
        {
            if (ids.Count > 64 || ids.Any(id => id is null || !evidence.ContainsKey(id)))
            {
                throw new InvalidDataException("Interpretation references unknown evidence.");
            }
        }

        void Assessment(RepositoryInterpretationAssessment assessment, IReadOnlyList<string> supporting, IReadOnlyList<string> conflicting)
        {
            if (!Applicabilities.Contains(assessment.Applicability, StringComparer.Ordinal)
                || !EvidenceClasses.Contains(assessment.EvidenceClass, StringComparer.Ordinal)
                || !Confidences.Contains(assessment.Confidence, StringComparer.Ordinal)
                || assessment.Uncertainty?.Length > 1024 || assessment.Then?.Length > 1024 || assessment.Replacement?.Length > 1024
                || assessment.AtTarget?.Length > 1024 || assessment.RationaleQuote?.Length > 1024)
            {
                throw new InvalidDataException("Invalid interpretation assessment.");
            }

            if (supporting.Select(id => evidence[id]).All(item => string.IsNullOrWhiteSpace(item.Text))
                || (assessment.Applicability == "Current" && (string.IsNullOrWhiteSpace(assessment.AtTarget)
                    || !supporting.Select(id => evidence[id]).Any(item => item.Source.Revision == targetCommit))))
            {
                throw new InvalidDataException("Evidence cannot substantiate the claimed applicability.");
            }

            if ((assessment.Applicability == "Uncertain" || assessment.Confidence != "High" || conflicting.Count > 0)
                && string.IsNullOrWhiteSpace(assessment.Uncertainty))
            {
                throw new InvalidDataException("Material uncertainty must be retained.");
            }

            if (assessment.CausalClaim && (assessment.EvidenceClass != "Documented" || assessment.RationaleQuote is not { Length: >= 12 and <= 1024 } quote
                || !supporting.Select(id => evidence[id]).Any(item => item.Source.Kind is "Document" or "CommitMetadata"
                    && item.Text?.Contains(quote, StringComparison.Ordinal) == true)))
            {
                throw new InvalidDataException("Causal intent needs an exact documented rationale citation; sequence is insufficient.");
            }
        }
    }

    /// <summary>Code-owned schema: deployed wording cannot alter fields or authority.</summary>
    internal static string ResponseSchema { get; } = CreateResponseSchema();

    private static string CreateResponseSchema()
    {
        var schema = JsonNode.Parse(ToolDefinitionFactory.Create<RepositoryInterpretationExpansion, RepositoryInterpretationResponse>(
            "interpret_repository_evidence", string.Empty, ToolCategory.RepositoryInspection, RepositoryTrustLevel.UntrustedInspection, ApprovalLevel.None, ToolSideEffect.ReadOnly, TimeSpan.FromSeconds(60), 32768).OutputSchema.JsonSchema)
            ?? throw new InvalidOperationException("Interpretation schema is unavailable.");
        Seal(schema);
        return schema.ToJsonString();

        static void SetEnum(JsonObject properties, string name, IReadOnlyList<string> values)
        {
            if (properties[name] is JsonObject property)
            {
                property["enum"] = new JsonArray([.. values.Select(value => (JsonNode?)JsonValue.Create(value))]);
            }
        }

        static void Seal(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                foreach (var child in obj.Select(item => item.Value).OfType<JsonNode>().ToArray())
                {
                    Seal(child);
                }

                if (obj["properties"] is JsonObject properties)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray([.. properties.Select(item => (JsonNode?)JsonValue.Create(item.Key))]);
                    SetEnum(properties, "outcome", Outcomes);
                    SetEnum(properties, "applicability", Applicabilities);
                    SetEnum(properties, "evidenceClass", EvidenceClasses);
                    SetEnum(properties, "confidence", Confidences);
                    if (properties.ContainsKey("statement"))
                    {
                        SetEnum(properties, "kind", CandidateKinds);
                    }
                    else if (properties.ContainsKey("from"))
                    {
                        SetEnum(properties, "kind", RelationshipKinds);
                    }
                    else if (properties.ContainsKey("startLine"))
                    {
                        SetEnum(properties, "kind", ExpansionKinds);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array.OfType<JsonNode>())
                {
                    Seal(child);
                }
            }
        }
    }
}
