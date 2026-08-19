namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Assembles governed mutation context, validates model output, and stages it for review.</summary>
public sealed class MutationProposalApplication :
    ICommandHandler<ProposeMutationSetCommand, StagedMutationSet>
{
    private const string ProposeMutationsToolName = "propose_mutations";
    private const string ProposeMutationsArgumentsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "planRevision", "planStepIds", "mutationSet"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "planRevision": { "type": "integer", "minimum": 1 },
            "planStepIds": {
              "type": "array",
              "minItems": 1,
              "items": {
                "anyOf": [
                  { "type": "string", "format": "uuid" },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["value"],
                    "properties": { "value": { "type": "string", "format": "uuid" } }
                  }
                ]
              }
            },
            "mutationSet": {
              "type": "object",
              "additionalProperties": false,
              "required": ["mutations", "rationale"],
              "properties": {
                "mutations": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": 100,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["type", "relativePath"],
                    "properties": {
                      "type": { "type": "string", "enum": ["CreateFile", "DeleteFile", "ReplaceText", "RenameSymbol", "MoveFile"] },
                      "relativePath": { "type": "string" },
                      "baselineSha256": { "type": "string" },
                      "expectedIdentity": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["sha256", "byteLength"],
                        "properties": {
                          "sha256": { "type": "string" },
                          "byteLength": { "type": "integer", "minimum": 0 }
                        }
                      },
                      "destinationRelativePath": { "type": "string" },
                      "destinationExpectation": { "type": "string", "enum": ["Absent"] },
                      "content": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["text", "encoding", "newline"],
                        "properties": {
                          "text": { "type": "string" },
                          "encoding": { "type": "string", "enum": ["Utf8", "Utf8Bom"] },
                          "newline": { "type": "string", "enum": ["Lf", "CrLf"] },
                          "sha256": { "type": "string" }
                        }
                      },
                      "lifecycleRisk": { "type": "string", "enum": ["Additive", "Relocation", "Destructive", "ProjectSystem"] },
                      "projectFilePath": { "type": "string" },
                      "startOffset": { "type": "integer", "minimum": 0 },
                      "length": { "type": "integer", "minimum": 0 },
                      "expectedText": { "type": "string" },
                      "replacementText": { "type": "string" },
                      "relatedSymbolId": { "type": "string" }
                    }
                  }
                },
                "rationale": { "type": "string" },
                "affectedProjects": { "type": "array", "items": { "type": "string" } },
                "expectedDiagnosticsResolved": { "type": "array", "items": { "type": "string" } },
                "expectedTests": { "type": "array", "items": { "type": "string" } },
                "risk": { "type": "string", "enum": ["Low", "Medium", "High"] },
                "validationPolicy": { "type": "string" }
              }
            },
            "expectedOutcomes": { "type": "array", "items": { "type": "string" } },
            "validationExpectations": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    static MutationProposalApplication()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
        JsonOptions.Converters.Add(new StepIdJsonConverter());
    }

    private static readonly ModelToolDefinition ProposeMutationsTool =
        ModelToolCanonicalizer.Canonicalize(
        [
            new ModelToolDefinition
            {
                Name = ProposeMutationsToolName,
                Description = "Submit one plan-scoped mutation proposal using the exact schema fields. Required envelope fields are schemaVersion, planRevision, planStepIds, and mutationSet. mutationSet requires rationale and mutations. Each mutation item requires type and relativePath; use baselineSha256 for baseline hashes. Do not use plan file-intent or legacy names kind, path, baselineHash, or per-item rationale/risk/validation fields. For C# symbol renames, prefer RenameSymbol with relatedSymbolId and replacementText set to the new identifier; optional MoveFile may rename the declaration file. ReplaceText requires exact expectedText; the host may correct an inaccurate offset only when that text has one match. The host validates the proposal and this call never writes files.",
                ArgumentsJsonSchema = ProposeMutationsArgumentsSchema,
            },
        ])[0];

    private readonly Func<IBudget> _budgetFactory;
    private readonly IContextAssembler _contextAssembler;
    private readonly ModelProfileId? _defaultModelProfileId;
    private readonly ExecutionLimits _limits;
    private readonly IDomainEventStream _events;
    private readonly IModelProvider _model;
    private readonly IPreMutationAnalyzer? _preMutationAnalyzer;
    private readonly ISemanticMutationEngine? _semanticMutations;
    private readonly IOutputSanitizer _sanitizer;
    private readonly SessionModelPreferences? _sessionPreferences;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly ITransactionalWorkspaceResolver _workspaces;

    /// <summary>Initializes a new instance of the <see cref="MutationProposalApplication"/> class.</summary>
    public MutationProposalApplication(
        IModelProvider model,
        IContextAssembler contextAssembler,
        ITransactionalWorkspaceResolver workspaces,
        IBudget budget,
        IOutputSanitizer sanitizer,
        IDomainEventStream events,
        ModelProfileId? defaultModelProfileId = null,
        ExecutionLimits? limits = null,
        SessionModelPreferences? sessionPreferences = null,
        SessionUsageProjection? sessionUsage = null,
        Func<IBudget>? budgetFactory = null,
        ISemanticMutationEngine? semanticMutations = null,
        IPreMutationAnalyzer? preMutationAnalyzer = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(events);
        _model = model;
        _semanticMutations = semanticMutations;
        _preMutationAnalyzer = preMutationAnalyzer;
        _contextAssembler = contextAssembler;
        _workspaces = workspaces;
        _budgetFactory = budgetFactory ?? (() => budget);
        _sanitizer = sanitizer;
        _defaultModelProfileId = defaultModelProfileId;
        _limits = limits ?? ExecutionLimits.Default;
        _sessionPreferences = sessionPreferences;
        _sessionUsage = sessionUsage;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(
        ProposeMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = command;
        int maximumAttempts = Math.Max(1, _limits.MaxMutationProposalRepairAttempts);
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await _events.PublishAsync(
                new MutationProposalStarted(
                    current.SessionId,
                    DateTimeOffset.UtcNow,
                    current.RunId,
                    attempt + 1,
                    maximumAttempts),
                cancellationToken);
            try
            {
                return await HandleCoreAsync(current, cancellationToken);
            }
            catch (MalformedModelOutputException exception)
                when (attempt + 1 < maximumAttempts
                    && IsRepairableMutationProposalFailure(exception))
            {
                string message = _sanitizer.Sanitize(exception.Message);
                string correctionEvidence = FormatMutationProposalCorrectionEvidence(message);
                await _events.PublishAsync(
                    new MutationProposalRepairAttempted(
                        current.SessionId,
                        DateTimeOffset.UtcNow,
                        current.RunId,
                        attempt + 2,
                        maximumAttempts,
                        message),
                    cancellationToken);
                current = current with
                {
                    CorrectionEvidence = string.IsNullOrWhiteSpace(current.CorrectionEvidence)
                        ? correctionEvidence
                        : current.CorrectionEvidence + Environment.NewLine + correctionEvidence,
                };
            }
        }

        throw new InvalidOperationException("Mutation proposal attempts did not execute.");
    }

    private async Task<StagedMutationSet> HandleCoreAsync(
        ProposeMutationSetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Task);
        ArgumentNullException.ThrowIfNull(command.ApprovedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Task.Intent);
        if (command.SessionId == default
            || command.RunId == default
            || command.WorkspaceId == default)
        {
            throw new ArgumentException("Mutation proposal ownership ids cannot be default.", nameof(command));
        }

        var operationBudget = _budgetFactory()
            ?? throw new InvalidOperationException("The execution budget factory returned no budget.");
        ModelOutputValidator.Validate(new PlanModelOutput(command.ApprovedPlan));
        if (command.Phase is not RunPhase.MutationPreparation
            and not RunPhase.ImplementationModelTurn
            and not RunPhase.CorrectionModelTurn)
        {
            throw new MalformedModelOutputException(
                "The model requested propose_mutations outside implementation or correction.");
        }

        var workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        var baseline = workspace.Baseline;
        if (baseline.WorkspaceId != command.WorkspaceId)
        {
            throw new InvalidOperationException(
                "The transactional resolver returned a baseline for a different workspace.");
        }

        var effectiveTask = command.CorrectionEvidence is null
            ? command.Task
            : command.Task with
            {
                UserConstraints =
                [
                    .. command.Task.UserConstraints ?? [],
                    $"Correction evidence: {_sanitizer.Sanitize(command.CorrectionEvidence)}",
                ],
            };
        var context = await _contextAssembler.AssembleAsync(
            new ContextAssemblyRequest
            {
                SessionId = command.SessionId,
                RunId = command.RunId,
                Phase = command.Phase,
                Task = effectiveTask,
                RepositoryPath = baseline.RepositoryPath,
                WorkingScope = RepositoryWorkingScope.Resolve(
                    baseline.RepositoryPath,
                    command.ApprovedPlan.Steps.SelectMany(step => step.GetAffectedPaths())),
                ProhibitedPaths = baseline.ProhibitedPaths ?? [],
                RequiredCapabilities = new ModelCapabilitySet
                {
                    Streaming = true,
                    StructuredOutput = true,
                },
                DefaultModelProfileId = _sessionPreferences?.CurrentProfileId
                    ?? _defaultModelProfileId,
                ApprovedPlan = command.ApprovedPlan,
                MutationBaseline = baseline,
                ToolSchemas =
                [
                    new ContextToolSchema(
                        ProposeMutationsTool.Name,
                        ProposeMutationsTool.Description,
                        ProposeMutationsTool.ArgumentsJsonSchema),
                ],
            },
            cancellationToken);
        IReadOnlyList<ModelToolDefinition> modelTools = [ProposeMutationsTool];
        var textOutput = new StringBuilder();
        MutationSetModelOutput? structured = null;
        MutationProposalEnvelope? envelope = null;
        bool proposalToolObserved = false;
        var usageRequestId = new ModelRequestUsageId(
            command.RunId,
            "mutation",
            0,
            Guid.NewGuid());
        ModelUsage? reportedUsage = null;
        try
        {
            await foreach (var chunk in _model.StreamAsync(
            new ModelStreamRequest
            {
                RunId = command.RunId,
                Input = context.ModelInput,

                // Fixed seed preserves deterministic scripted-provider chunking and reproducible proposal tests.
                Seed = 42,
                WorkloadClass = context.WorkloadClass,
                ContainsSensitiveData = context.ModelConstraints.ContainsSensitiveData,
                RequiredCapabilities = context.RequiredCapabilities,
                SelectionConstraints = context.ModelConstraints,
                ResolvedProfileId = context.ModelResolution?.ProfileId,
                ReasoningLevel = _sessionPreferences?.ResolveFor(context.ModelResolution?.ProfileId)
                    ?? ReasoningLevel.None,
                Tools = modelTools,
                Messages = context.Messages ?? [],
                Layout = context.Layout,
                ToolTransportMode = ToolTransportMode.Native,
                WireEstimate = context.WireEstimate,
            },
            cancellationToken))
            {
                if (chunk.Reasoning is not null)
                {
                    await _events.PublishAsync(
                        new ModelReasoningObserved(
                            command.SessionId,
                            DateTimeOffset.UtcNow,
                            _sanitizer.Sanitize(chunk.Reasoning)),
                        cancellationToken);
                }

                if (chunk.Output is MutationSetModelOutput mutationOutput)
                {
                    ModelOutputValidator.Validate(mutationOutput);
                    structured = mutationOutput;
                }
                else if (chunk.Output is ToolRequestModelOutput toolRequest)
                {
                    long toolOutputCharacters = (long)toolRequest.ToolName.Length
                        + toolRequest.ArgumentsJson.Length;
                    if (toolOutputCharacters > _limits.MaxStructuredOutputCharacters)
                    {
                        throw new MalformedModelOutputException(
                            $"The mutation proposal exceeded the {_limits.MaxStructuredOutputCharacters}-character structured-output limit.");
                    }

                    if (!string.Equals(toolRequest.ToolName, ProposeMutationsToolName, StringComparison.Ordinal)
                        || proposalToolObserved)
                    {
                        throw new MalformedModelOutputException(
                            proposalToolObserved
                                ? "The model called propose_mutations more than once in one turn."
                                : $"Implementation requested unauthorized tool '{toolRequest.ToolName}'.");
                    }

                    proposalToolObserved = true;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<MutationProposalEnvelope>(
                            toolRequest.ArgumentsJson,
                            JsonOptions);
                    }
                    catch (JsonException exception)
                    {
                        string path = string.IsNullOrWhiteSpace(exception.Path)
                            ? "$"
                            : _sanitizer.Sanitize(exception.Path);
                        throw new MalformedModelOutputException(
                            $"The propose_mutations arguments did not match schema 1 at '{path}'.",
                            exception);
                    }

                    if (envelope is null)
                    {
                        throw new MalformedModelOutputException(
                            "The propose_mutations arguments were empty.");
                    }
                }
                else if (chunk.Output is not null and not TextModelOutput)
                {
                    throw new MalformedModelOutputException(
                        $"Mutation preparation returned unsupported output '{chunk.Output.GetType().Name}'.");
                }

                if (chunk.Text is not null && structured is null)
                {
                    if (textOutput.Length + chunk.Text.Length > _limits.MaxStructuredOutputCharacters)
                    {
                        throw new MalformedModelOutputException(
                            $"The mutation proposal exceeded the {_limits.MaxStructuredOutputCharacters}-character structured-output limit.");
                    }

                    textOutput.Append(chunk.Text);
                }

                if (chunk.Usage is not null)
                {
                    reportedUsage = chunk.Usage;
                    _sessionUsage?.Observe(
                        command.SessionId,
                        usageRequestId,
                        chunk.Usage);
                    var budget = operationBudget.Accrue(new BudgetDimensions(
                        chunk.Usage.InputTokens + chunk.Usage.OutputTokens,
                        1,
                        TimeSpan.Zero,
                        chunk.Usage.EstimatedCost));
                    if (budget.IsExhausted)
                    {
                        throw new BudgetExceededException(
                            budget.Reason ?? "Execution budget exhausted during mutation preparation.");
                    }
                }
            }
        }
        finally
        {
            if (reportedUsage is null)
            {
                _sessionUsage?.ObserveMissing(command.SessionId, usageRequestId);
            }
        }

        if (envelope is not null)
        {
            ValidateEnvelope(envelope, command.ApprovedPlan);
            var hostOwned = CreateHostOwnedMutationSet(
                envelope.MutationSet,
                command,
                baseline);
            hostOwned = await ResolveSemanticRenameMutationsAsync(
                hostOwned,
                command,
                baseline,
                cancellationToken);
            structured = new MutationSetModelOutput(hostOwned);
        }
        else if (command.Phase is RunPhase.ImplementationModelTurn or RunPhase.CorrectionModelTurn)
        {
            throw new MalformedModelOutputException(
                "Implementation and correction must call propose_mutations exactly once.");
        }

        structured ??= ModelOutputValidator.ParseMutationSet(textOutput.ToString().Trim());
        var proposed = structured.MutationSet;
        if (proposed.SessionId != command.SessionId
            || proposed.RunId != command.RunId
            || proposed.WorkspaceId != command.WorkspaceId
            || proposed.BaselineCapturedAt != baseline.CapturedAt
            || !string.Equals(
                proposed.BaselineRevision,
                baseline.GitRevision,
                StringComparison.Ordinal))
        {
            throw new MalformedModelOutputException(
                "The model mutation proposal changed host-owned session, run, workspace, or baseline identity.");
        }

        if (envelope is not null)
        {
            proposed = await ResolveModelReplaceTextRangesAsync(
                proposed,
                workspace,
                cancellationToken);
        }

        proposed = proposed with
        {
            Rationale = _sanitizer.Sanitize(proposed.Rationale),
            AffectedProjects = proposed.AffectedProjects.Select(_sanitizer.Sanitize).ToArray(),
            ExpectedDiagnosticsResolved = proposed.ExpectedDiagnosticsResolved
                .Select(_sanitizer.Sanitize)
                .ToArray(),
            ExpectedTests = proposed.ExpectedTests.Select(_sanitizer.Sanitize).ToArray(),
            ValidationPolicy = _sanitizer.Sanitize(proposed.ValidationPolicy),
            IsWithinApprovedPlan = true,
            RequiredApproval = MutationApprovalLevel.EntireSet,
        };
        ModelOutputValidator.Validate(new MutationSetModelOutput(proposed));
        var approvedPlanPathComparer = CreateWorkspacePathComparer(workspace);
        ValidateMutationsWithinPlan(
            proposed.Mutations,
            command.ApprovedPlan.Steps,
            approvedPlanPathComparer,
            "approved plan");

        if (envelope is not null)
        {
            ValidateMutationsWithinPlan(
                proposed.Mutations,
                command.ApprovedPlan.Steps.Where(step => envelope.PlanStepIds.Contains(step.StepId)),
                approvedPlanPathComparer,
                "claimed plan steps");
        }

        await AnalyzePreMutationAsync(
            command,
            workspace,
            baseline,
            proposed,
            cancellationToken);

        var staged = await _workspaces.StageAsync(proposed, cancellationToken);
        return staged with { PlanStepIds = envelope?.PlanStepIds.ToArray() ?? [] };
    }

    private static void ValidateMutationsWithinPlan(
        IReadOnlyList<Mutation> mutations,
        IEnumerable<ImplementationPlanStep> approvedSteps,
        StringComparer pathComparer,
        string scopeDescription)
    {
        var approvedIntents = approvedSteps
            .SelectMany(step => step.FileIntents)
            .ToArray();
        foreach (var mutation in mutations)
        {
            if (approvedIntents.Any(intent => IntentCoversMutation(intent, mutation, pathComparer)))
            {
                continue;
            }

            throw new MalformedModelOutputException(
                $"The mutation proposal targets '{FormatMutationTarget(mutation)}' with '{mutation.Type}', which is outside the {scopeDescription}.");
        }
    }

    private static bool IntentCoversMutation(
        PlanFileIntent intent,
        Mutation mutation,
        StringComparer pathComparer)
    {
        return mutation.Type switch
        {
            MutationType.CreateFile => intent.Kind == PlanFileChangeKind.Create
                && PathsEqual(intent.Path, mutation.RelativePath, pathComparer),
            MutationType.DeleteFile => intent.Kind == PlanFileChangeKind.Delete
                && PathsEqual(intent.Path, mutation.RelativePath, pathComparer),
            MutationType.MoveFile => intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename
                && PathsEqual(intent.Path, mutation.RelativePath, pathComparer)
                && PathsEqual(intent.DestinationPath, mutation.DestinationRelativePath, pathComparer),
            MutationType.ReplaceText or MutationType.ReplaceSyntaxNode or MutationType.RenameSymbol =>
                intent.Kind == PlanFileChangeKind.Modify
                && PathsEqual(intent.Path, mutation.RelativePath, pathComparer),
            _ => false,
        };
    }

    private static bool PathsEqual(string? left, string? right, StringComparer pathComparer)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && pathComparer.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'));
    }

    private static string FormatMutationTarget(Mutation mutation)
    {
        var source = NormalizeProposalPath(mutation.RelativePath);
        return mutation.DestinationRelativePath is null
            ? source
            : $"{source} -> {NormalizeProposalPath(mutation.DestinationRelativePath)}";
    }

    private static bool IsRepairableMutationProposalFailure(MalformedModelOutputException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Message.Contains("propose_mutations arguments did not match schema", StringComparison.Ordinal)
            || exception.Message.Contains("ReplaceText", StringComparison.Ordinal)
            || exception.Message.Contains("RenameSymbol", StringComparison.Ordinal)
            || exception.Message.Contains("expectedText", StringComparison.Ordinal)
            || exception.Message.Contains("expected different text", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Pre-mutation Roslyn", StringComparison.Ordinal);
    }

    private static string FormatMutationProposalCorrectionEvidence(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var prefix = $"Previous propose_mutations failed host validation before staging: {message}.";
        if (message.Contains("propose_mutations arguments did not match schema", StringComparison.Ordinal))
        {
            return prefix
                + " Use exactly the advertised propose_mutations schema: mutationSet.rationale is required;"
                + " each mutation item must use type and relativePath, not kind or path;"
                + " use baselineSha256, not baselineHash;"
                + " keep per-change prose such as rationale, risk, or validation out of mutation items;"
                + " call propose_mutations once with expectedText copied exactly from the current file evidence.";
        }

        return prefix
            + " Re-read the exact target text already present in the file evidence and call propose_mutations once"
            + " with expectedText copied exactly from the current file.";
    }

    private async Task AnalyzePreMutationAsync(
        ProposeMutationSetCommand command,
        ITransactionalWorkspace workspace,
        WorkspaceBaseline baseline,
        MutationSet proposed,
        CancellationToken cancellationToken)
    {
        if (_preMutationAnalyzer is null
            || !proposed.Mutations.Any(IsCSharpMutation))
        {
            return;
        }

        var overlay = await BuildPreMutationOverlayAsync(
            workspace,
            proposed,
            cancellationToken);
        var result = await _preMutationAnalyzer.AnalyzeAsync(
            new PreMutationAnalysisRequest
            {
                SessionId = command.SessionId,
                RunId = command.RunId,
                WorkspaceId = command.WorkspaceId,
                Baseline = baseline,
                MutationSet = proposed,
                OverlayFiles = overlay,
            },
            cancellationToken);
        int blockingDiagnostics = result.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        await _events.PublishAsync(
            new PreMutationAnalysisCompleted(
                command.SessionId,
                DateTimeOffset.UtcNow,
                command.RunId,
                proposed.MutationSetId,
                result.Decision,
                result.Diagnostics.Count,
                blockingDiagnostics,
                result.Omissions.Count,
                result.Confidence),
            cancellationToken);
        if (result.Decision is PreMutationGateDecision.NonRepairableHostFailure
            or PreMutationGateDecision.BudgetExhausted)
        {
            throw new InvalidOperationException(FormatPreMutationCorrection(result));
        }

        if (result.Decision == PreMutationGateDecision.RepairableDiagnostics
            || result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new MalformedModelOutputException(FormatPreMutationCorrection(result));
        }
    }

    private static bool IsCSharpMutation(Mutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return IsCSharpPath(mutation.RelativePath)
            || (mutation.DestinationRelativePath is not null && IsCSharpPath(mutation.DestinationRelativePath));
    }

    private static bool IsCSharpPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<PreMutationOverlayFile>> BuildPreMutationOverlayAsync(
        ITransactionalWorkspace workspace,
        MutationSet proposed,
        CancellationToken cancellationToken)
    {
        var pathComparer = CreateWorkspacePathComparer(workspace);
        var currentByPath = new Dictionary<string, string?>(pathComparer);
        var mutationByPath = new Dictionary<string, MutationId>(pathComparer);
        foreach (var mutation in proposed.Mutations)
        {
            string sourcePath = NormalizeProposalPath(mutation.RelativePath);
            if (!currentByPath.ContainsKey(sourcePath))
            {
                currentByPath[sourcePath] = await workspace.ReadBaselineTextAsync(
                    sourcePath,
                    cancellationToken);
            }

            switch (mutation.Type)
            {
                case MutationType.CreateFile:
                    if (currentByPath[sourcePath] is not null)
                    {
                        throw new InvalidOperationException($"File '{sourcePath}' already exists.");
                    }

                    currentByPath[sourcePath] = mutation.Content?.Text ?? mutation.ReplacementText;
                    mutationByPath[sourcePath] = mutation.MutationId;
                    break;
                case MutationType.DeleteFile:
                    currentByPath[sourcePath] = null;
                    mutationByPath[sourcePath] = mutation.MutationId;
                    break;
                case MutationType.ReplaceText:
                case MutationType.ReplaceSyntaxNode:
                case MutationType.RenameSymbol:
                    currentByPath[sourcePath] = ApplyReplacement(
                        sourcePath,
                        currentByPath[sourcePath],
                        mutation);
                    mutationByPath[sourcePath] = mutation.MutationId;
                    break;
                case MutationType.MoveFile:
                    string destination = NormalizeProposalPath(
                        mutation.DestinationRelativePath
                            ?? throw new MalformedModelOutputException("MoveFile requires a destination path."));
                    string movedText = mutation.Content?.Text
                        ?? currentByPath[sourcePath]
                        ?? throw new MalformedModelOutputException($"MoveFile source '{sourcePath}' was not present in the immutable baseline.");
                    currentByPath[sourcePath] = null;
                    currentByPath[destination] = movedText;
                    mutationByPath[sourcePath] = mutation.MutationId;
                    mutationByPath[destination] = mutation.MutationId;
                    break;
                default:
                    break;
            }
        }

        return currentByPath
            .Where(item => IsCSharpPath(item.Key))
            .Select(item => new PreMutationOverlayFile
            {
                RelativePath = item.Key,
                Text = item.Value,
                RelatedMutationId = mutationByPath.GetValueOrDefault(item.Key),
            })
            .ToArray();
    }

    private static StringComparer CreateWorkspacePathComparer(ITransactionalWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return IsCaseSensitiveFileSystem(workspace.Isolation.RepositoryPath)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    private static bool IsCaseSensitiveFileSystem(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        int letterIndex = -1;
        for (int index = 0; index < name.Length; index++)
        {
            if (char.IsLetter(name[index]))
            {
                letterIndex = index;
                break;
            }
        }

        if (parent is null || letterIndex < 0 || !Directory.Exists(parent))
        {
            return !OperatingSystem.IsWindows();
        }

        char[] toggledNameCharacters = name.ToCharArray();
        char letter = toggledNameCharacters[letterIndex];
        toggledNameCharacters[letterIndex] = char.IsUpper(letter)
            ? char.ToLowerInvariant(letter)
            : char.ToUpperInvariant(letter);
        string toggledName = new(toggledNameCharacters);
        bool distinctToggledEntryExists = Directory.EnumerateFileSystemEntries(parent)
            .Select(Path.GetFileName)
            .Any(entry => string.Equals(entry, toggledName, StringComparison.Ordinal));
        return distinctToggledEntryExists
            || !Directory.Exists(Path.Combine(parent, toggledName));
    }

    private static string ApplyReplacement(
        string relativePath,
        string? current,
        Mutation mutation)
    {
        if (current is null)
        {
            throw new MalformedModelOutputException(
                $"ReplaceText target '{relativePath}' was not present in the immutable baseline.");
        }

        string expected = mutation.ExpectedText
            ?? throw new MalformedModelOutputException(
                $"ReplaceText target '{relativePath}' requires exact expectedText.");
        bool exactRange = mutation.StartOffset >= 0
            && mutation.Length >= 0
            && mutation.StartOffset <= current.Length - mutation.Length
            && mutation.Length == expected.Length
            && current.AsSpan(mutation.StartOffset, mutation.Length).SequenceEqual(expected);
        int startOffset = mutation.StartOffset;
        int length = mutation.Length;
        if (!exactRange)
        {
            if (expected.Length == 0)
            {
                throw new MalformedModelOutputException(
                    $"ReplaceText insertion in '{relativePath}' requires the exact offset.");
            }

            int firstMatch = current.IndexOf(expected, StringComparison.Ordinal);
            if (firstMatch < 0)
            {
                throw new MalformedModelOutputException(
                    $"ReplaceText expectedText was not found in '{relativePath}'.");
            }

            int secondMatch = current.IndexOf(
                expected,
                firstMatch + 1,
                StringComparison.Ordinal);
            if (secondMatch >= 0)
            {
                throw new MalformedModelOutputException(
                    $"ReplaceText expectedText is ambiguous in '{relativePath}'; provide the exact offset.");
            }

            startOffset = firstMatch;
            length = expected.Length;
        }

        return string.Concat(
            current.AsSpan(0, startOffset),
            mutation.ReplacementText,
            current.AsSpan(startOffset + length));
    }

    private static string NormalizeProposalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/');
    }

    private string FormatPreMutationCorrection(PreMutationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.Append("Pre-mutation Roslyn analysis found blocking diagnostics before staging or approval. ");
        builder.Append("Revise the proposed mutation set against the same baseline; no repository files were changed.");
        foreach (var diagnostic in result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(8))
        {
            builder.AppendLine();
            builder.Append("- ");
            builder.Append(SanitizeAndBound(diagnostic.File ?? "<no file>"));
            if (diagnostic.Range is { } range)
            {
                builder.Append(':');
                builder.Append(range.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(range.StartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            builder.Append(' ');
            builder.Append(SanitizeAndBound(diagnostic.Code));
            builder.Append(" (");
            builder.Append(diagnostic.Source);
            builder.Append("): ");
            builder.Append(SanitizeAndBound(diagnostic.Message));
            if (!string.IsNullOrWhiteSpace(diagnostic.ContainingSymbol))
            {
                builder.Append(" [containing ");
                builder.Append(SanitizeAndBound(diagnostic.ContainingSymbol));
                builder.Append(']');
            }

            if (!string.IsNullOrWhiteSpace(diagnostic.ChangedHunk))
            {
                builder.Append(" Hunk: ");
                builder.Append(SanitizeAndBound(diagnostic.ChangedHunk));
            }
        }

        foreach (string omission in result.Omissions.Take(4))
        {
            builder.AppendLine();
            builder.Append("Omission: ");
            builder.Append(SanitizeAndBound(omission));
        }

        return builder.ToString();
    }

    private string SanitizeAndBound(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string sanitized = _sanitizer.Sanitize(value).ReplaceLineEndings(" ");
        return sanitized.Length <= 512
            ? sanitized
            : sanitized[..512] + "…";
    }

    private async Task<MutationSet> ResolveSemanticRenameMutationsAsync(
        MutationSet proposal,
        ProposeMutationSetCommand command,
        WorkspaceBaseline baseline,
        CancellationToken cancellationToken)
    {
        Mutation[] semanticRenameRequests =
        [
            .. proposal.Mutations.Where(mutation => mutation.Type == MutationType.RenameSymbol),
        ];
        if (semanticRenameRequests.Length == 0)
        {
            return proposal;
        }

        if (_semanticMutations is null)
        {
            throw new MalformedModelOutputException(
                "RenameSymbol requires an available semantic mutation engine; use exact ReplaceText only when semantic rename is unavailable.");
        }

        if (semanticRenameRequests.Length > 1)
        {
            throw new MalformedModelOutputException(
                "A mutation proposal may contain only one RenameSymbol operation.");
        }

        var semanticRequest = semanticRenameRequests[0];
        string symbolId = semanticRequest.RelatedSymbolId
            ?? throw new MalformedModelOutputException(
                "RenameSymbol requires relatedSymbolId from semantic symbol evidence.");
        string newName = semanticRequest.ReplacementText;
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new MalformedModelOutputException(
                "RenameSymbol requires replacementText set to the new symbol name.");
        }

        SemanticMutationResult semanticResult;
        try
        {
            semanticResult = await _semanticMutations.RenameSymbolAsync(
                new RenameSymbolMutationRequest
                {
                    SessionId = command.SessionId,
                    RunId = command.RunId,
                    WorkspaceId = command.WorkspaceId,
                    Baseline = baseline,
                    SymbolId = symbolId,
                    NewName = newName,
                    Rationale = proposal.Rationale,
                },
                cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            throw new MalformedModelOutputException(
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new MalformedModelOutputException(
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new MalformedModelOutputException(
                $"RenameSymbol proposal cannot be applied: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }

        await PublishSemanticMutationWarningsAsync(
            command.SessionId,
            command.RunId,
            semanticResult,
            cancellationToken);

        HashSet<string> semanticPaths = semanticResult.MutationSet.Mutations
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Mutation[] nonSemantic =
        [
            .. proposal.Mutations.Where(mutation => mutation.Type != MutationType.RenameSymbol),
        ];
        string? overlappingTextMutation = nonSemantic
            .Where(mutation => mutation.Type != MutationType.MoveFile)
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .FirstOrDefault(semanticPaths.Contains);
        if (overlappingTextMutation is not null)
        {
            throw new MalformedModelOutputException(
                $"RenameSymbol already edits '{overlappingTextMutation}'; do not combine it with another text mutation for the same file.");
        }

        string[] affectedProjects =
        [
            .. NullAsEmpty(proposal.AffectedProjects),
            .. semanticResult.MutationSet.AffectedProjects,
        ];
        return proposal with
        {
            Mutations = [.. semanticResult.MutationSet.Mutations, .. nonSemantic],
            AffectedProjects = affectedProjects
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            Risk = proposal.Risk > semanticResult.MutationSet.Risk
                ? proposal.Risk
                : semanticResult.MutationSet.Risk,
            ValidationPolicy = string.Equals(proposal.ValidationPolicy, "default", StringComparison.OrdinalIgnoreCase)
                ? semanticResult.MutationSet.ValidationPolicy
                : proposal.ValidationPolicy,
        };
    }

    private async Task PublishSemanticMutationWarningsAsync(
        SessionId sessionId,
        RunId runId,
        SemanticMutationResult semanticResult,
        CancellationToken cancellationToken)
    {
        if (semanticResult.Confidence != SemanticConfidenceLevel.FullSemantic)
        {
            await _events.PublishAsync(
                new SemanticMutationWarningObserved(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    semanticResult.Confidence,
                    $"Semantic rename completed with {semanticResult.Confidence} confidence; review incomplete coverage before approving."),
                cancellationToken);
        }

        foreach (string warning in semanticResult.Warnings)
        {
            string sanitized = _sanitizer.Sanitize(warning);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                continue;
            }

            await _events.PublishAsync(
                new SemanticMutationWarningObserved(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    semanticResult.Confidence,
                    sanitized),
                cancellationToken);
        }
    }

    private static async Task<MutationSet> ResolveModelReplaceTextRangesAsync(
        MutationSet proposal,
        ITransactionalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var currentByPath = new Dictionary<string, string?>(StringComparer.Ordinal);
        var resolved = new List<Mutation>(proposal.Mutations.Count);
        foreach (var mutation in proposal.Mutations)
        {
            if (mutation.Type != MutationType.ReplaceText)
            {
                resolved.Add(mutation);
                continue;
            }

            string path = mutation.RelativePath.Replace('\\', '/');
            if (!currentByPath.TryGetValue(path, out string? current))
            {
                current = await workspace.ReadBaselineTextAsync(path, cancellationToken);
            }

            if (current is null)
            {
                throw new MalformedModelOutputException(
                    $"ReplaceText target '{path}' was not present in the immutable baseline.");
            }

            string expected = mutation.ExpectedText
                ?? throw new MalformedModelOutputException(
                    $"ReplaceText target '{path}' requires exact expectedText.");
            bool exactRange = mutation.StartOffset >= 0
                && mutation.Length >= 0
                && mutation.StartOffset <= current.Length - mutation.Length
                && mutation.Length == expected.Length
                && current.AsSpan(mutation.StartOffset, mutation.Length).SequenceEqual(expected);
            var resolvedMutation = mutation;
            if (!exactRange)
            {
                if (expected.Length == 0)
                {
                    throw new MalformedModelOutputException(
                        $"ReplaceText insertion in '{path}' requires the exact offset.");
                }

                int firstMatch = current.IndexOf(expected, StringComparison.Ordinal);
                if (firstMatch < 0)
                {
                    throw new MalformedModelOutputException(
                        $"ReplaceText expectedText was not found in '{path}'.");
                }

                int secondMatch = current.IndexOf(
                    expected,
                    firstMatch + 1,
                    StringComparison.Ordinal);
                if (secondMatch >= 0)
                {
                    throw new MalformedModelOutputException(
                        $"ReplaceText expectedText is ambiguous in '{path}'; provide the exact offset.");
                }

                resolvedMutation = mutation with
                {
                    StartOffset = firstMatch,
                    Length = expected.Length,
                };
            }

            current = string.Concat(
                current.AsSpan(0, resolvedMutation.StartOffset),
                resolvedMutation.ReplacementText,
                current.AsSpan(resolvedMutation.StartOffset + resolvedMutation.Length));
            currentByPath[path] = current;
            resolved.Add(resolvedMutation);
        }

        return proposal with { Mutations = resolved };
    }

    private static MutationSet CreateHostOwnedMutationSet(
        MutationProposalSet proposal,
        ProposeMutationSetCommand command,
        WorkspaceBaseline baseline)
    {
        return new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = command.SessionId,
            RunId = command.RunId,
            WorkspaceId = command.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
            BaselineRevision = baseline.GitRevision,
            Mutations = proposal.Mutations.Select(change => new Mutation
            {
                MutationId = MutationId.New(),
                Type = change.Type,
                RelativePath = change.RelativePath,
                BaselineSha256 = change.BaselineSha256,
                ExpectedIdentity = change.ExpectedIdentity,
                DestinationRelativePath = change.DestinationRelativePath,
                DestinationExpectation = change.DestinationExpectation ?? DestinationExpectation.Absent,
                Content = change.Content,
                LifecycleRisk = change.LifecycleRisk,
                ProjectFilePath = change.ProjectFilePath,
                StartOffset = change.StartOffset ?? 0,
                Length = change.Length ?? 0,
                ExpectedText = change.ExpectedText,
                ReplacementText = GetReplacementText(change),
                RelatedSymbolId = change.RelatedSymbolId,
            }).ToArray(),
            Rationale = proposal.Rationale,
            AffectedProjects = NullAsEmpty(proposal.AffectedProjects),
            ExpectedDiagnosticsResolved = NullAsEmpty(proposal.ExpectedDiagnosticsResolved),
            ExpectedTests = NullAsEmpty(proposal.ExpectedTests),
            Risk = proposal.Risk ?? MutationRisk.Medium,
            ValidationPolicy = proposal.ValidationPolicy ?? "default",
        };
    }

    private static string GetReplacementText(MutationProposalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Type == MutationType.CreateFile
            && string.IsNullOrEmpty(change.ReplacementText)
            && change.Content is { } content
                ? content.Text
                : change.ReplacementText ?? string.Empty;
    }

    private static IReadOnlyList<string> NullAsEmpty(IReadOnlyList<string>? values)
    {
        return values ?? [];
    }

    private static void ValidateEnvelope(
        MutationProposalEnvelope envelope,
        ImplementationPlan approvedPlan)
    {
        if (envelope.SchemaVersion != 1
            || envelope.PlanRevision != approvedPlan.Revision
            || envelope.PlanStepIds.Count == 0)
        {
            throw new MalformedModelOutputException(
                "The mutation proposal schema, plan revision, or plan-step correlation is invalid.");
        }

        HashSet<StepId> approvedStepIds = [.. approvedPlan.Steps.Select(step => step.StepId)];
        var unknownStep = envelope.PlanStepIds
            .FirstOrDefault(stepId => !approvedStepIds.Contains(stepId));
        if (unknownStep != default)
        {
            throw new MalformedModelOutputException(
                $"The mutation proposal references unknown plan step '{unknownStep.Value}'.");
        }

        if (NullAsEmpty(envelope.ExpectedOutcomes).Any(string.IsNullOrWhiteSpace)
            || NullAsEmpty(envelope.ValidationExpectations).Any(string.IsNullOrWhiteSpace))
        {
            throw new MalformedModelOutputException(
                "Mutation proposal outcomes and validation expectations cannot contain empty values.");
        }
    }
}
