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
          "required": ["mutationSet"],
          "$defs": {
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
            "expectedIdentity": {
              "type": "object",
              "additionalProperties": false,
              "required": ["sha256", "byteLength"],
              "properties": {
                "sha256": { "type": "string" },
                "byteLength": { "type": "integer", "minimum": 0 }
              }
            },
            "createFile": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "relativePath", "content"],
              "properties": {
                "type": { "type": "string", "const": "CreateFile" },
                "relativePath": { "type": "string" },
                "content": { "$ref": "#/$defs/content" },
                "lifecycleRisk": { "type": "string", "enum": ["Additive", "Relocation", "Destructive", "ProjectSystem"] },
                "projectFilePath": { "type": "string" }
              }
            },
            "deleteFile": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "relativePath", "expectedIdentity"],
              "properties": {
                "type": { "type": "string", "const": "DeleteFile" },
                "relativePath": { "type": "string" },
                "baselineSha256": { "type": "string" },
                "expectedIdentity": { "$ref": "#/$defs/expectedIdentity" },
                "lifecycleRisk": { "type": "string", "enum": ["Additive", "Relocation", "Destructive", "ProjectSystem"] },
                "projectFilePath": { "type": "string" }
              }
            },
            "replaceText": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "relativePath", "startOffset", "length", "expectedText", "replacementText"],
              "properties": {
                "type": { "type": "string", "const": "ReplaceText" },
                "relativePath": { "type": "string" },
                "baselineSha256": { "type": "string" },
                "startOffset": { "type": "integer", "minimum": 0 },
                "length": { "type": "integer", "minimum": 0 },
                "expectedText": { "type": "string" },
                "replacementText": { "type": "string" },
                "relatedSymbolId": { "type": "string" },
                "projectFilePath": { "type": "string" }
              }
            },
            "renameSymbol": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "relativePath", "relatedSymbolId", "replacementText"],
              "properties": {
                "type": { "type": "string", "const": "RenameSymbol" },
                "relativePath": { "type": "string" },
                "baselineSha256": { "type": "string" },
                "relatedSymbolId": { "type": "string" },
                "replacementText": { "type": "string" },
                "projectFilePath": { "type": "string" }
              }
            },
            "moveFile": {
              "type": "object",
              "additionalProperties": false,
              "required": ["type", "relativePath", "destinationRelativePath", "expectedIdentity"],
              "properties": {
                "type": { "type": "string", "const": "MoveFile" },
                "relativePath": { "type": "string" },
                "baselineSha256": { "type": "string" },
                "expectedIdentity": { "$ref": "#/$defs/expectedIdentity" },
                "destinationRelativePath": { "type": "string" },
                "content": { "$ref": "#/$defs/content" },
                "lifecycleRisk": { "type": "string", "enum": ["Additive", "Relocation", "Destructive", "ProjectSystem"] },
                "projectFilePath": { "type": "string" }
              }
            }
          },
          "properties": {
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
                    "oneOf": [
                      { "$ref": "#/$defs/createFile" },
                      { "$ref": "#/$defs/deleteFile" },
                      { "$ref": "#/$defs/replaceText" },
                      { "$ref": "#/$defs/renameSymbol" },
                      { "$ref": "#/$defs/moveFile" }
                    ]
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    static MutationProposalApplication()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly Func<IBudget> _budgetFactory;
    private readonly IContextAssembler _contextAssembler;
    private readonly CorrectiveMessageFactory _correctiveMessages;
    private readonly ModelProfileId? _defaultModelProfileId;
    private readonly ExecutionLimits _limits;
    private readonly IDomainEventStream _events;
    private readonly IModelProvider _model;
    private readonly IPreMutationAnalyzer? _preMutationAnalyzer;
    private readonly ISemanticMutationEngine? _semanticMutations;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IPromptLoader _prompts;
    private readonly ModelToolDefinition _proposeMutationsTool;
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
        IPreMutationAnalyzer? preMutationAnalyzer = null,
        CorrectiveMessageFactory? correctiveMessages = null,
        IPromptLoader? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(correctiveMessages);
        ArgumentNullException.ThrowIfNull(prompts);
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
        _correctiveMessages = correctiveMessages;
        _prompts = prompts;
        _proposeMutationsTool = CreateProposeMutationsTool(RequirePrompts());
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(
        ProposeMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var correctiveTurns = new CorrectiveTurnState(Math.Max(0, _limits.MaxCorrectiveTurns));
        var correctiveMessages = new List<ModelMessage>();
        for (var proposalAttempt = 1; ; proposalAttempt++)
        {
            await _events.PublishAsync(
                new MutationProposalStarted(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RunId,
                    proposalAttempt,
                    correctiveTurns.MaximumTurns + 1),
                cancellationToken);
            try
            {
                return await HandleCoreAsync(command, correctiveMessages, cancellationToken);
            }
            catch (RepairableMutationProposalException exception)
            {
                await AppendCorrectionMessageOrThrowAsync(
                    command,
                    correctiveTurns,
                    correctiveMessages,
                    exception.Category,
                    exception.Diagnostic,
                    exception,
                    cancellationToken);
            }
            catch (MalformedInvocationException exception)
            {
                await AppendCorrectionMessageOrThrowAsync(
                    command,
                    correctiveTurns,
                    correctiveMessages,
                    ModelCorrectionCategory.ProviderInvocation,
                    exception.Diagnostic,
                    exception,
                    cancellationToken);
            }
        }
    }

    private async Task AppendCorrectionMessageOrThrowAsync(
        ProposeMutationSetCommand command,
        CorrectiveTurnState correctiveTurns,
        List<ModelMessage> correctiveMessages,
        ModelCorrectionCategory category,
        MalformedInvocationDiagnostic diagnostic,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(correctiveTurns);
        ArgumentNullException.ThrowIfNull(correctiveMessages);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(exception);
        var sanitizedReason = _sanitizer.Sanitize(diagnostic.SafeMessage);
        var safeReason = string.IsNullOrWhiteSpace(sanitizedReason)
            ? "The mutation proposal was rejected before staging."
            : BoundCorrectionReason(sanitizedReason);
        if (!correctiveTurns.TryBeginAttempt(out var correctionAttempt))
        {
            throw new MalformedModelOutputException(
                "The mutation proposal corrective-turn budget was exhausted: " + safeReason,
                exception);
        }

        await _events.PublishAsync(
            new ModelCorrectionAttempted(
                command.SessionId,
                DateTimeOffset.UtcNow,
                command.RunId,
                category,
                correctionAttempt,
                correctiveTurns.MaximumTurns,
                safeReason),
            cancellationToken);
        correctiveMessages.Add(RequireCorrectiveMessages().CreateMutationProposalDeveloperMessage(
            diagnostic with { SafeMessage = safeReason },
            correctionAttempt,
            correctiveTurns.MaximumTurns));
    }

    private IReadOnlyList<ModelMessage> CreateRequestLocalCorrectionMessages(
        MutationCorrectionContext? correction,
        IReadOnlyList<ModelMessage> correctiveMessages)
    {
        ArgumentNullException.ThrowIfNull(correctiveMessages);
        return correction is null
            ? [.. correctiveMessages]
            :
            [
                RequireCorrectiveMessages().CreateMutationCorrectionDeveloperMessage(correction),
                .. correctiveMessages,
            ];
    }

    private async Task<StagedMutationSet> HandleCoreAsync(
        ProposeMutationSetCommand command,
        IReadOnlyList<ModelMessage> correctiveMessages,
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
        var additionalMessages = CreateRequestLocalCorrectionMessages(
            command.Correction,
            correctiveMessages);
        if (baseline.WorkspaceId != command.WorkspaceId)
        {
            throw new InvalidOperationException(
                "The transactional resolver returned a baseline for a different workspace.");
        }

        var context = await _contextAssembler.AssembleAsync(
            new ContextAssemblyRequest
            {
                SessionId = command.SessionId,
                RunId = command.RunId,
                Phase = command.Phase,
                Task = command.Task,
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
                        _proposeMutationsTool.Name,
                        _proposeMutationsTool.Description,
                        _proposeMutationsTool.ArgumentsJsonSchema,
                        _proposeMutationsTool.PreferStrictArguments),
                ],
                AdditionalMessages = additionalMessages,
            },
            cancellationToken);
        var requestMessages = new List<ModelMessage>();
        if (context.Messages is not null)
        {
            requestMessages.AddRange(context.Messages);
        }

        IReadOnlyList<ModelToolDefinition> modelTools = [_proposeMutationsTool];
        var wireEstimate = EstimateAndValidateCompleteRequest(context, requestMessages, modelTools);
        var textOutput = new StringBuilder();
        MutationSetModelOutput? structured = null;
        MutationProposalEnvelope? envelope = null;
        var proposalToolObserved = false;
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
                AllowMultipleToolCalls = false,
                Messages = requestMessages,
                Layout = context.Layout,
                ToolTransportMode = ToolTransportMode.Native,
                WireEstimate = wireEstimate,
                ProviderInstructions = context.ProviderInstructions,
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
                    if (mutationOutput.MutationSet is not null)
                    {
                        FailIfMutationPathPolicyViolation(mutationOutput.MutationSet);
                    }

                    try
                    {
                        ModelOutputValidator.Validate(mutationOutput);
                    }
                    catch (MalformedModelOutputException exception)
                    {
                        throw CreateRepairableMutationFailure(
                            ModelCorrectionCategory.MutationProposal,
                            MalformedInvocationFailureKind.MutationSchemaMismatch,
                            "The structured mutation proposal did not match the required schema.",
                            exception);
                    }

                    structured = mutationOutput;
                }
                else if (chunk.Output is ToolRequestModelOutput toolRequest)
                {
                    var toolOutputCharacters = (long)toolRequest.ToolName.Length
                        + toolRequest.ArgumentsJson.Length;
                    if (toolOutputCharacters > _limits.MaxStructuredOutputCharacters)
                    {
                        throw new MalformedModelOutputException(
                            $"The mutation proposal exceeded the {_limits.MaxStructuredOutputCharacters}-character structured-output limit.");
                    }

                    if (!string.Equals(toolRequest.ToolName, ProposeMutationsToolName, StringComparison.Ordinal)
                        || proposalToolObserved)
                    {
                        var requestedTool = string.IsNullOrWhiteSpace(toolRequest.ToolName)
                            ? "<missing>"
                            : BoundCorrectionReason(toolRequest.ToolName);
                        var safeReason = proposalToolObserved
                            ? "The model called propose_mutations more than once in one turn."
                            : $"Implementation requested unauthorized tool '{requestedTool}'.";
                        var diagnosticKind = proposalToolObserved
                            ? MalformedInvocationFailureKind.MultipleToolProducingOutputs
                            : MalformedInvocationFailureKind.UnknownTool;
                        throw CreateRepairableMutationFailure(
                            ModelCorrectionCategory.MutationProposal,
                            diagnosticKind,
                            safeReason);
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
                        var path = string.IsNullOrWhiteSpace(exception.Path)
                            ? "$"
                            : _sanitizer.Sanitize(exception.Path);
                        throw CreateRepairableMutationFailure(
                            ModelCorrectionCategory.MutationProposal,
                            MalformedInvocationFailureKind.InvalidJsonArguments,
                            $"The propose_mutations arguments did not match the operation-specific schema at '{path}'.",
                            exception);
                    }

                    if (envelope is null)
                    {
                        throw CreateRepairableMutationFailure(
                            ModelCorrectionCategory.MutationProposal,
                            MalformedInvocationFailureKind.MutationSchemaMismatch,
                            "The propose_mutations arguments were empty.");
                    }
                }
                else if (chunk.Output is not null and not TextModelOutput)
                {
                    throw CreateRepairableMutationFailure(
                        ModelCorrectionCategory.MutationProposal,
                        MalformedInvocationFailureKind.MutationSchemaMismatch,
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
            ValidateEnvelope(envelope);
            var hostOwned = CreateHostOwnedMutationSet(
                envelope.MutationSet,
                command,
                baseline);
            FailIfMutationPathPolicyViolation(hostOwned);
            hostOwned = await ResolveSemanticRenameMutationsAsync(
                hostOwned,
                command,
                baseline,
                cancellationToken);
            FailIfMutationPathPolicyViolation(hostOwned);
            structured = new MutationSetModelOutput(hostOwned);
        }
        else if (command.Phase is RunPhase.ImplementationModelTurn or RunPhase.CorrectionModelTurn)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.MissingToolName,
                _prompts.Get(PromptFileNames.CorrectionMutationImplementationRequiresTool));
        }

        if (structured is null)
        {
            try
            {
                structured = ModelOutputValidator.ParseMutationSet(textOutput.ToString().Trim());
            }
            catch (MalformedModelOutputException exception)
            {
                throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.MutationSchemaMismatch,
                    "The mutation proposal did not match the required structured mutation schema.",
                    exception);
            }
        }

        var proposed = structured.MutationSet
            ?? throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The mutation proposal did not include a mutation set.");
        FailIfMutationPathPolicyViolation(proposed);
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
        try
        {
            ModelOutputValidator.Validate(new MutationSetModelOutput(proposed));
        }
        catch (MalformedModelOutputException exception)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The mutation proposal did not match the required mutation-set schema after host normalization.",
                exception);
        }

        var approvedPlanPathComparer = CreateWorkspacePathComparer(workspace);
        ValidateMutationsWithinPlan(
            proposed.Mutations,
            command.ApprovedPlan.Steps,
            approvedPlanPathComparer,
            "approved plan");
        var planStepIds = ResolvePlanStepIds(
            proposed.Mutations,
            command.ApprovedPlan.Steps,
            approvedPlanPathComparer);

        await AnalyzePreMutationAsync(
            command,
            workspace,
            baseline,
            proposed,
            cancellationToken);

        var staged = await _workspaces.StageAsync(proposed, cancellationToken);
        return staged with { PlanStepIds = planStepIds };
    }

    private CorrectiveMessageFactory RequireCorrectiveMessages()
    {
        return _correctiveMessages;
    }

    private IPromptLoader RequirePrompts()
    {
        return _prompts;
    }

    private static ModelToolDefinition CreateProposeMutationsTool(IPromptLoader prompts)
    {
        return ModelToolCanonicalizer.Canonicalize(
        [
            new ModelToolDefinition
            {
                Name = ProposeMutationsToolName,
                Description = prompts.Get(PromptFileNames.ToolProposeMutationsDescription),
                ArgumentsJsonSchema = ProposeMutationsArgumentsSchema,
                PreferStrictArguments = true,
            },
        ])[0];
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

            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
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

    private static IReadOnlyList<StepId> ResolvePlanStepIds(
        IReadOnlyList<Mutation> mutations,
        IReadOnlyList<ImplementationPlanStep> approvedSteps,
        StringComparer pathComparer)
    {
        return mutations
            .Select(mutation => approvedSteps
                .Where(step => step.FileIntents.Any(intent => IntentCoversMutation(intent, mutation, pathComparer)))
                .Select(step => step.StepId)
                .ToArray())
            .Where(matches => matches.Length == 1)
            .Select(matches => matches[0])
            .Distinct()
            .ToArray();
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

    private static void FailIfMutationPathPolicyViolation(MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        if (mutationSet.Mutations is null)
        {
            return;
        }

        foreach (var mutation in mutationSet.Mutations)
        {
            if (mutation is null)
            {
                continue;
            }

            if (IsMutationPathPolicyViolation(mutation.RelativePath)
                || IsMutationPathPolicyViolation(mutation.DestinationRelativePath)
                || IsMutationPathPolicyViolation(mutation.ProjectFilePath))
            {
                throw new MalformedModelOutputException(
                    "The mutation proposal violates repository path confinement.");
            }
        }
    }

    private static bool IsMutationPathPolicyViolation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.IsPathRooted(path)
            || segments.Contains("..", StringComparer.Ordinal);
    }

    private static ModelWireEstimate EstimateAndValidateCompleteRequest(
        ContextAssemblyResult context,
        IReadOnlyList<ModelMessage> requestMessages,
        IReadOnlyList<ModelToolDefinition> modelTools)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestMessages);
        ArgumentNullException.ThrowIfNull(modelTools);
        var stablePrefixMessageCount = context.Layout is null
            ? 0
            : Math.Min(context.Layout.StablePrefixMessageCount, requestMessages.Count);
        var outputReserveTokens = context.WireEstimate?.OutputReserveTokens
            ?? context.ModelResolution?.EffectiveRequestOutputTokenReserve
            ?? 0;
        var wireEstimate = ModelWireEstimator.Estimate(
            requestMessages,
            modelTools,
            ToolTransportMode.Native,
            stablePrefixMessageCount,
            outputReserveTokens,
            context.ProviderInstructions);
        if (wireEstimate.WireInputTokens > context.Inspection.TokenBudget)
        {
            throw new InvalidOperationException(
                $"Structured mutation provider wire input requires {wireEstimate.WireInputTokens} tokens but the budget is "
                + $"{context.Inspection.TokenBudget}.");
        }

        return wireEstimate;
    }

    private static RepairableMutationProposalException CreateRepairableMutationFailure(
        ModelCorrectionCategory category,
        MalformedInvocationFailureKind kind,
        string safeMessage,
        Exception? innerException = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        var diagnostic = CreateRepairableMutationDiagnostic(kind, safeMessage);
        return innerException is null
            ? new RepairableMutationProposalException(category, diagnostic)
            : new RepairableMutationProposalException(category, diagnostic, innerException);
    }

    private static MalformedInvocationDiagnostic CreateRepairableMutationDiagnostic(
        MalformedInvocationFailureKind kind,
        string safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        return new MalformedInvocationDiagnostic
        {
            Kind = kind,
            SafeMessage = BoundCorrectionReason(safeMessage),
            ToolName = ProposeMutationsToolName,
            ToolOrdinal = 0,
            ToolCallCount = 1,
        };
    }

    private static string BoundCorrectionReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sanitized = value.ReplaceLineEndings(" ");
        var builder = new StringBuilder(Math.Min(sanitized.Length, 512));
        foreach (var character in sanitized)
        {
            if (builder.Length == 512)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
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
        var blockingDiagnostics = result.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
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
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.PreMutationAnalysis,
                MalformedInvocationFailureKind.PreMutationDiagnostics,
                FormatPreMutationCorrection(result));
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

    private async Task<IReadOnlyList<PreMutationOverlayFile>> BuildPreMutationOverlayAsync(
        ITransactionalWorkspace workspace,
        MutationSet proposed,
        CancellationToken cancellationToken)
    {
        var pathComparer = CreateWorkspacePathComparer(workspace);
        var currentByPath = new Dictionary<string, string?>(pathComparer);
        var mutationByPath = new Dictionary<string, MutationId>(pathComparer);
        foreach (var mutation in proposed.Mutations)
        {
            var sourcePath = NormalizeProposalPath(mutation.RelativePath);
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
                    var destination = NormalizeProposalPath(
                        mutation.DestinationRelativePath
                            ?? throw CreateRepairableMutationFailure(
                                ModelCorrectionCategory.MutationProposal,
                                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                                "MoveFile requires a destination path."));
                    var movedText = mutation.Content?.Text
                        ?? currentByPath[sourcePath]
                        ?? throw CreateRepairableMutationFailure(
                            ModelCorrectionCategory.MutationProposal,
                            MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                            $"MoveFile source '{sourcePath}' was not present in the immutable baseline.");
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
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var parent = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        var letterIndex = -1;
        for (var index = 0; index < name.Length; index++)
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

        var toggledNameCharacters = name.ToCharArray();
        var letter = toggledNameCharacters[letterIndex];
        toggledNameCharacters[letterIndex] = char.IsUpper(letter)
            ? char.ToLowerInvariant(letter)
            : char.ToUpperInvariant(letter);
        string toggledName = new(toggledNameCharacters);
        var distinctToggledEntryExists = Directory.EnumerateFileSystemEntries(parent)
            .Select(Path.GetFileName)
            .Any(entry => string.Equals(entry, toggledName, StringComparison.Ordinal));
        return distinctToggledEntryExists
            || !Directory.Exists(Path.Combine(parent, toggledName));
    }

    private string ApplyReplacement(
        string relativePath,
        string? current,
        Mutation mutation)
    {
        if (current is null)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"ReplaceText target '{relativePath}' was not present in the immutable baseline.");
        }

        var expected = mutation.ExpectedText
            ?? throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"ReplaceText target '{relativePath}' requires exact expectedText.");
        var exactRange = mutation.StartOffset >= 0
            && mutation.Length >= 0
            && mutation.StartOffset <= current.Length - mutation.Length
            && mutation.Length == expected.Length
            && current.AsSpan(mutation.StartOffset, mutation.Length).SequenceEqual(expected);
        var startOffset = mutation.StartOffset;
        var length = mutation.Length;
        if (!exactRange)
        {
            if (expected.Length == 0)
            {
                throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    $"ReplaceText insertion in '{relativePath}' requires the exact offset.");
            }

            var firstMatch = current.IndexOf(expected, StringComparison.Ordinal);
            if (firstMatch < 0)
            {
                throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    $"ReplaceText expectedText was not found in '{relativePath}'.");
            }

            var secondMatch = current.IndexOf(
                expected,
                firstMatch + 1,
                StringComparison.Ordinal);
            if (secondMatch >= 0)
            {
                throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    _prompts.Render(
                        PromptFileNames.CorrectionMutationReplaceTextAmbiguousExpectedText,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["RelativePath"] = relativePath,
                        }));
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
        var diagnosticItems = new StringBuilder();
        foreach (var diagnostic in result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(8))
        {
            var file = SanitizeAndBound(
                diagnostic.File
                    ?? _prompts.Get(PromptFileNames.CorrectionPreMutationDiagnosticFileFallback));
            var range = diagnostic.Range is { } sourceRange
                ? ":"
                    + sourceRange.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":"
                    + sourceRange.StartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            var containingSymbol = !string.IsNullOrWhiteSpace(diagnostic.ContainingSymbol)
                ? _prompts.Render(
                    PromptFileNames.CorrectionPreMutationContainingSymbolBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ContainingSymbol"] = SanitizeAndBound(diagnostic.ContainingSymbol),
                    })
                : string.Empty;
            var changedHunk = !string.IsNullOrWhiteSpace(diagnostic.ChangedHunk)
                ? _prompts.Render(
                    PromptFileNames.CorrectionPreMutationChangedHunkBlock,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ChangedHunk"] = SanitizeAndBound(diagnostic.ChangedHunk),
                    })
                : string.Empty;
            diagnosticItems.Append(PromptAssetRenderer.RenderWithPlatformLineEndings(
                _prompts,
                PromptFileNames.CorrectionPreMutationDiagnosticItem,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["File"] = file,
                    ["Range"] = range,
                    ["Code"] = SanitizeAndBound(diagnostic.Code),
                    ["Source"] = diagnostic.Source.ToString(),
                    ["Message"] = SanitizeAndBound(diagnostic.Message),
                    ["ContainingSymbolBlock"] = containingSymbol,
                    ["ChangedHunkBlock"] = changedHunk,
                }));
        }

        var omissionItems = new StringBuilder();
        foreach (var omission in result.Omissions.Take(4))
        {
            omissionItems.Append(PromptAssetRenderer.RenderWithPlatformLineEndings(
                _prompts,
                PromptFileNames.CorrectionPreMutationOmissionItem,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Omission"] = SanitizeAndBound(omission),
                }));
        }

        return RequireCorrectiveMessages().CreatePreMutationBlockingDiagnostics(
            diagnosticItems.ToString(),
            omissionItems.ToString());
    }

    private string SanitizeAndBound(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = _sanitizer.Sanitize(value).ReplaceLineEndings(" ");
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
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                _prompts.Get(PromptFileNames.CorrectionMutationRenameSymbolSemanticUnavailable));
        }

        if (semanticRenameRequests.Length > 1)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "A mutation proposal may contain only one RenameSymbol operation.");
        }

        var semanticRequest = semanticRenameRequests[0];
        var symbolId = semanticRequest.RelatedSymbolId
            ?? throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "RenameSymbol requires relatedSymbolId from semantic symbol evidence.");
        var newName = semanticRequest.ReplacementText;
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
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
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"RenameSymbol proposal cannot be applied: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }

        await PublishSemanticMutationWarningsAsync(
            command.SessionId,
            command.RunId,
            semanticResult,
            cancellationToken);

        var semanticPaths = semanticResult.MutationSet.Mutations
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Mutation[] nonSemantic =
        [
            .. proposal.Mutations.Where(mutation => mutation.Type != MutationType.RenameSymbol),
        ];
        var overlappingTextMutation = nonSemantic
            .Where(mutation => mutation.Type != MutationType.MoveFile)
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .FirstOrDefault(semanticPaths.Contains);
        if (overlappingTextMutation is not null)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                _prompts.Render(
                    PromptFileNames.CorrectionMutationRenameSymbolOverlap,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RelativePath"] = overlappingTextMutation,
                    }));
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

        foreach (var warning in semanticResult.Warnings)
        {
            var sanitized = _sanitizer.Sanitize(warning);
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

    private async Task<MutationSet> ResolveModelReplaceTextRangesAsync(
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

            var path = mutation.RelativePath.Replace('\\', '/');
            if (!currentByPath.TryGetValue(path, out var current))
            {
                current = await workspace.ReadBaselineTextAsync(path, cancellationToken);
            }

            if (current is null)
            {
                throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    $"ReplaceText target '{path}' was not present in the immutable baseline.");
            }

            var expected = mutation.ExpectedText
                ?? throw CreateRepairableMutationFailure(
                    ModelCorrectionCategory.MutationProposal,
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    $"ReplaceText target '{path}' requires exact expectedText.");
            var exactRange = mutation.StartOffset >= 0
                && mutation.Length >= 0
                && mutation.StartOffset <= current.Length - mutation.Length
                && mutation.Length == expected.Length
                && current.AsSpan(mutation.StartOffset, mutation.Length).SequenceEqual(expected);
            var resolvedMutation = mutation;
            if (!exactRange)
            {
                if (expected.Length == 0)
                {
                    throw CreateRepairableMutationFailure(
                        ModelCorrectionCategory.MutationProposal,
                        MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                        $"ReplaceText insertion in '{path}' requires the exact offset.");
                }

                var firstMatch = current.IndexOf(expected, StringComparison.Ordinal);
                if (firstMatch < 0)
                {
                    throw CreateRepairableMutationFailure(
                        ModelCorrectionCategory.MutationProposal,
                        MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                        $"ReplaceText expectedText was not found in '{path}'.");
                }

                var secondMatch = current.IndexOf(
                    expected,
                    firstMatch + 1,
                    StringComparison.Ordinal);
                if (secondMatch >= 0)
                {
                    throw CreateRepairableMutationFailure(
                        ModelCorrectionCategory.MutationProposal,
                        MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                        _prompts.Render(
                            PromptFileNames.CorrectionMutationReplaceTextAmbiguousExpectedText,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["RelativePath"] = path,
                            }));
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
            Mutations = proposal.Mutations.Select(CreateHostOwnedMutation).ToArray(),
            Rationale = proposal.Rationale,
            AffectedProjects = NullAsEmpty(proposal.AffectedProjects),
            ExpectedDiagnosticsResolved = NullAsEmpty(proposal.ExpectedDiagnosticsResolved),
            ExpectedTests = NullAsEmpty(proposal.ExpectedTests),
            Risk = proposal.Risk ?? MutationRisk.Medium,
            ValidationPolicy = proposal.ValidationPolicy ?? "default",
        };
    }

    private static Mutation CreateHostOwnedMutation(MutationProposalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change switch
        {
            CreateFileMutationProposal create => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.CreateFile,
                RelativePath = create.RelativePath,
                Content = create.Content,
                LifecycleRisk = create.LifecycleRisk,
                ProjectFilePath = create.ProjectFilePath,
                ReplacementText = create.Content?.Text ?? string.Empty,
            },
            DeleteFileMutationProposal delete => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.DeleteFile,
                RelativePath = delete.RelativePath,
                BaselineSha256 = delete.BaselineSha256,
                ExpectedIdentity = delete.ExpectedIdentity,
                LifecycleRisk = delete.LifecycleRisk,
                ProjectFilePath = delete.ProjectFilePath,
            },
            ReplaceTextMutationProposal replace => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.ReplaceText,
                RelativePath = replace.RelativePath,
                BaselineSha256 = replace.BaselineSha256,
                ProjectFilePath = replace.ProjectFilePath,
                StartOffset = replace.StartOffset,
                Length = replace.Length,
                ExpectedText = replace.ExpectedText,
                ReplacementText = replace.ReplacementText,
                RelatedSymbolId = replace.RelatedSymbolId,
            },
            RenameSymbolMutationProposal rename => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.RenameSymbol,
                RelativePath = rename.RelativePath,
                BaselineSha256 = rename.BaselineSha256,
                ProjectFilePath = rename.ProjectFilePath,
                ReplacementText = rename.ReplacementText,
                RelatedSymbolId = rename.RelatedSymbolId,
            },
            MoveFileMutationProposal move => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.MoveFile,
                RelativePath = move.RelativePath,
                BaselineSha256 = move.BaselineSha256,
                ExpectedIdentity = move.ExpectedIdentity,
                DestinationRelativePath = move.DestinationRelativePath,
                Content = move.Content,
                LifecycleRisk = move.LifecycleRisk,
                ProjectFilePath = move.ProjectFilePath,
            },
            _ => throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "The mutation proposal contains an unsupported operation."),
        };
    }

    private static IReadOnlyList<string> NullAsEmpty(IReadOnlyList<string>? values)
    {
        return values ?? [];
    }

    private static void ValidateEnvelope(MutationProposalEnvelope envelope)
    {
        if (envelope.MutationSet is null
            || envelope.MutationSet.Mutations is null
            || envelope.MutationSet.Mutations.Count is < 1 or > 100
            || string.IsNullOrWhiteSpace(envelope.MutationSet.Rationale))
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The mutation proposal requires a rationale and 1..100 operation-specific mutations.");
        }

        var invalidChange = envelope.MutationSet.Mutations.FirstOrDefault(change => change switch
        {
            CreateFileMutationProposal create => string.IsNullOrWhiteSpace(create.RelativePath)
                || create.Content is null,
            DeleteFileMutationProposal delete => string.IsNullOrWhiteSpace(delete.RelativePath)
                || delete.ExpectedIdentity is null,
            ReplaceTextMutationProposal replace => string.IsNullOrWhiteSpace(replace.RelativePath)
                || replace.ExpectedText is null
                || replace.ReplacementText is null,
            RenameSymbolMutationProposal rename => string.IsNullOrWhiteSpace(rename.RelativePath)
                || string.IsNullOrWhiteSpace(rename.RelatedSymbolId)
                || string.IsNullOrWhiteSpace(rename.ReplacementText),
            MoveFileMutationProposal move => string.IsNullOrWhiteSpace(move.RelativePath)
                || string.IsNullOrWhiteSpace(move.DestinationRelativePath)
                || move.ExpectedIdentity is null,
            _ => true,
        });
        if (invalidChange is not null)
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "A mutation operation omitted a required operation-specific field.");
        }

        if (NullAsEmpty(envelope.ExpectedOutcomes).Any(string.IsNullOrWhiteSpace)
            || NullAsEmpty(envelope.ValidationExpectations).Any(string.IsNullOrWhiteSpace))
        {
            throw CreateRepairableMutationFailure(
                ModelCorrectionCategory.MutationProposal,
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "Mutation proposal outcomes and validation expectations cannot contain empty values.");
        }
    }

    private sealed class RepairableMutationProposalException : MalformedModelOutputException
    {
        public RepairableMutationProposalException()
            : this(
                ModelCorrectionCategory.MutationProposal,
                CreateRepairableMutationDiagnostic(
                    MalformedInvocationFailureKind.MutationSchemaMismatch,
                    "The mutation proposal was rejected before staging."))
        {
        }

        public RepairableMutationProposalException(string message)
            : this(
                ModelCorrectionCategory.MutationProposal,
                CreateRepairableMutationDiagnostic(
                    MalformedInvocationFailureKind.MutationSchemaMismatch,
                    message))
        {
        }

        public RepairableMutationProposalException(string message, Exception innerException)
            : this(
                ModelCorrectionCategory.MutationProposal,
                CreateRepairableMutationDiagnostic(
                    MalformedInvocationFailureKind.MutationSchemaMismatch,
                    message),
                innerException)
        {
        }

        public RepairableMutationProposalException(
            ModelCorrectionCategory category,
            MalformedInvocationDiagnostic diagnostic)
            : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage)
        {
            Category = category;
            Diagnostic = diagnostic;
        }

        public RepairableMutationProposalException(
            ModelCorrectionCategory category,
            MalformedInvocationDiagnostic diagnostic,
            Exception innerException)
            : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).SafeMessage, innerException)
        {
            Category = category;
            Diagnostic = diagnostic;
        }

        public ModelCorrectionCategory Category { get; }

        public MalformedInvocationDiagnostic Diagnostic { get; }
    }
}
