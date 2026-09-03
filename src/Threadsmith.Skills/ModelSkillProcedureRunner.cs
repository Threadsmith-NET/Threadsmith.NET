namespace Threadsmith.Skills;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;

/// <summary>Runs bounded skill procedure turns through the configured provider and central tool pipeline.</summary>
public sealed class ModelSkillProcedureRunner : ISkillProcedureRunner
{
    private readonly ConfiguredModelCatalog? _catalog;
    private readonly IModelProvider _models;
    private readonly IModelProviderInstructionResolver? _providerInstructionResolver;
    private readonly IPromptLoader _prompts;
    private readonly SecretOutputSanitizer _sanitizer;
    private readonly Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> _toolContext;
    private readonly IToolInvocationPipeline _toolPipeline;
    private readonly ToolRegistry _tools;

    /// <summary>Initializes a new instance of the <see cref="ModelSkillProcedureRunner"/> class.</summary>
    public ModelSkillProcedureRunner(
        IModelProvider models,
        ToolRegistry tools,
        IToolInvocationPipeline toolPipeline,
        SecretOutputSanitizer sanitizer,
        Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> toolContext,
        IPromptLoader prompts,
        ConfiguredModelCatalog? catalog = null,
        IModelProviderInstructionResolver? providerInstructionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(prompts);
        _models = models;
        _tools = tools;
        _toolPipeline = toolPipeline;
        _sanitizer = sanitizer;
        _toolContext = toolContext;
        _prompts = prompts;
        _catalog = catalog;
        _providerInstructionResolver = providerInstructionResolver;
    }

    /// <inheritdoc />
    public async Task<SkillProcedureResult> RunAsync(
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        IReadOnlyList<SkillContextSegment> content,
        string inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);
        if (plan.ModelProfileId is null)
        {
            throw new InvalidOperationException("Skill procedure has no compatible configured model.");
        }

        var profileId = plan.ModelProfileId.Value;
        var profile = _catalog?.Get(profileId);
        var providerInstructions = _providerInstructionResolver?.Resolve(profileId);
        var modelTools = BuildToolDefinitions(plan.AvailableToolIds);
        var maximumRounds = Math.Max(1, plan.EffectiveBudget.ModelTurns);
        var maximumToolCalls = plan.EffectiveBudget.ToolCalls;
        var toolCalls = 0;
        var prompt = BuildPrompt(plan, step, iteration, content, inputJson);
        var maximumPromptCharacters = checked(plan.EffectiveBudget.ContentTokens * 8);
        if (prompt.Length > maximumPromptCharacters)
        {
            throw new InvalidOperationException("Skill procedure prompt exceeds its context bound.");
        }

        var seenCalls = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; round < maximumRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            ToolRequestModelOutput? toolRequest = null;
            IReadOnlyList<ModelMessage> messages =
            [
                new ModelMessage
                {
                    Role = ModelMessageRole.User,
                    SectionId = "skill-procedure",
                    Content = [new ModelContentPart { Content = prompt }],
                },
            ];
            var outputReserveTokens = profile?.EffectiveRequestOutputTokenReserve ?? 0;
            var canonicalModelTools = ModelToolCanonicalizer.Canonicalize(modelTools);
            var wireEstimate = ModelWireEstimator.Estimate(
                messages,
                canonicalModelTools,
                ToolTransportMode.Native,
                stablePrefixMessageCount: 0,
                outputReserveTokens,
                providerInstructions);
            if (profile is not null && wireEstimate.TotalCapacityTokens > profile.ContextWindow)
            {
                throw new InvalidOperationException(
                    "The complete skill procedure request exceeds the selected model context window.");
            }

            await foreach (var chunk in _models.StreamAsync(
                new ModelStreamRequest
                {
                    RunId = plan.Request.RunId,
                    Input = prompt,
                    Seed = HashCode.Combine(plan.Request.InvocationId, step.StepId, round),
                    ToolContinuationRound = round,
                    WorkloadClass = ResolveWorkload(step.Kind),
                    RequiredCapabilities = new ModelCapabilitySet
                    {
                        Streaming = true,
                        ToolCalls = plan.AvailableToolIds.Count > 0,
                        StructuredOutput = true,
                    },
                    SelectionConstraints = new ModelSelectionConstraints
                    {
                        MinimumContextWindow = plan.EffectiveBudget.ContentTokens,
                        ContainsSensitiveData = plan.Request.Sensitivity == ConversationSensitivity.Sensitive,
                    },
                    ResolvedProfileId = profileId,
                    MaximumOutputTokens = profile?.EffectiveRequestOutputTokenReserve,
                    Tools = canonicalModelTools,
                    AllowMultipleToolCalls = false,
                    Messages = messages,
                    WireEstimate = wireEstimate,
                    ProviderInstructions = providerInstructions,
                },
                cancellationToken))
            {
                if (chunk.Text is { } delta)
                {
                    text.Append(delta);
                    if (text.Length > 1024 * 1024)
                    {
                        throw new InvalidDataException("Skill procedure output exceeds its byte-oriented bound.");
                    }
                }

                if (chunk.Output is ToolRequestModelOutput requested)
                {
                    toolRequest = requested;
                }
                else if (chunk.Output is not null)
                {
                    throw new InvalidDataException(
                        "Skill procedure returned a structured output type not declared for skill workflows.");
                }
            }

            if (toolRequest is null)
            {
                var output = _sanitizer.Sanitize(text.ToString()).Trim();
                if (string.IsNullOrWhiteSpace(output))
                {
                    throw new InvalidDataException("Skill procedure returned empty output.");
                }

                return new SkillProcedureResult(output, round + 1, toolCalls);
            }

            if (!plan.AvailableToolIds.Contains(toolRequest.ToolName, StringComparer.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Skill procedure requested an undeclared tool.");
            }

            toolCalls++;
            if (toolCalls > maximumToolCalls)
            {
                throw new InvalidOperationException("Skill procedure tool-call budget is exhausted.");
            }

            var callKey = $"{toolRequest.ToolName}\n{SkillCanonicalJson.CanonicalizeValue(toolRequest.ArgumentsJson)}";
            if (!seenCalls.Add(callKey))
            {
                throw new InvalidOperationException("Skill procedure repeated an identical tool request.");
            }

            var context = await _toolContext(plan.Request, cancellationToken);
            var result = await _toolPipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = plan.Request.SessionId,
                    RunId = plan.Request.RunId,
                    Phase = plan.Request.Phase,
                    ToolId = toolRequest.ToolName,
                    ArgumentsJson = toolRequest.ArgumentsJson,
                    Context = context with
                    {
                        AllowedToolIds = plan.AvailableToolIds,
                        RequestedBy = $"skill:{plan.Package.SkillId.Value}:{plan.Request.InvocationId.Value:D}",
                    },
                },
                cancellationToken);
            var boundedResult = result.Succeeded
                ? result.ResultJson ?? "null"
                : SkillCanonicalJson.CanonicalizeValue(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        error = result.ErrorClassification.ToString(),
                        message = result.Error,
                    }));
            prompt += _prompts.Render(
                PromptFileNames.SkillProcedureContinuation,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ToolName"] = toolRequest.ToolName,
                    ["ToolResult"] = boundedResult,
                });
            if (prompt.Length > maximumPromptCharacters)
            {
                throw new InvalidOperationException("Skill procedure continuation exceeds its context bound.");
            }
        }

        throw new InvalidOperationException("Skill procedure model-turn budget is exhausted.");
    }

    private IReadOnlyList<ModelToolDefinition> BuildToolDefinitions(IReadOnlyList<string> toolIds)
    {
        return toolIds.Select(toolId =>
        {
            var definition = _tools.Get(toolId).Definition;
            return new ModelToolDefinition
            {
                Name = definition.Id,
                Description = definition.Description,
                ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
                PreferStrictArguments = definition.PreferStrictArguments,
            };
        }).ToArray();
    }

    private string BuildPrompt(
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        IReadOnlyList<SkillContextSegment> content,
        string inputJson)
    {
        var skillAssets = new StringBuilder();
        foreach (var segment in content)
        {
            skillAssets.AppendLine($"<skill_asset path=\"{segment.AssetPath}\" sha256=\"{segment.Sha256}\">");
            skillAssets.AppendLine(segment.Content);
            skillAssets.AppendLine("</skill_asset>");
        }

        return _prompts.Get(PromptFileNames.SkillProcedureSystem).ReplaceLineEndings(Environment.NewLine)
            + PromptAssetRenderer.RenderWithPlatformLineEndings(
                _prompts,
                PromptFileNames.SkillProcedureRequest,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PackageId"] = plan.Package.SkillId.Value,
                    ["PackageVersion"] = plan.Package.Version,
                    ["PackageDigest"] = plan.Package.Digest.Value,
                    ["StepId"] = step.StepId,
                    ["StepKind"] = $"{step.Kind}",
                    ["Iteration"] = $"{iteration}",
                    ["MaximumIterations"] = $"{step.MaximumIterations}",
                    ["SkillAssets"] = skillAssets.ToString(),
                    ["InputJson"] = inputJson,
                });
    }

    private static WorkloadClass ResolveWorkload(SkillWorkflowStepKind kind)
    {
        return kind switch
        {
            SkillWorkflowStepKind.Summarize => WorkloadClass.Summary,
            SkillWorkflowStepKind.RequestReviews => WorkloadClass.Review,
            _ => WorkloadClass.Planning,
        };
    }
}
