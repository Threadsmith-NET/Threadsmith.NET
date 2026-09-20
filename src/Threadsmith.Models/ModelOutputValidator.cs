namespace Threadsmith.Models;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Validates versioned host-owned model outputs before execution.</summary>
public static class ModelOutputValidator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions _planJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    /// <summary>Validates the supported schema version and type-specific invariants.</summary>
    public static void Validate(ModelOutput output, int supportedSchemaVersion = 1, WorkspaceResourceLimits? mutationLimits = null, PlanResourceLimits? planLimits = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.SchemaVersion != supportedSchemaVersion)
        {
            throw new MalformedModelOutputException(
                $"Unsupported model-output schema version {output.SchemaVersion}; "
                + $"expected {supportedSchemaVersion}.");
        }

        switch (output)
        {
            case TextModelOutput text when string.IsNullOrWhiteSpace(text.Text):
                throw new MalformedModelOutputException("Text model output is empty.");
            case ToolRequestModelOutput tool:
                ValidateInvocation(tool);
                break;
            case PlanModelOutput plan:
                ValidatePlan(plan.Plan, planLimits ?? new());
                break;
            case MutationSetModelOutput mutationSet:
                ValidateMutationSet(mutationSet.MutationSet, mutationLimits ?? new());
                break;
            case TextModelOutput:
                break;
            default:
                throw new MalformedModelOutputException(
                    $"Unsupported model-output type '{output.GetType().Name}'.");
        }
    }

    /// <summary>Parses plan content, assigns host metadata, and validates the resulting plan.</summary>
    public static PlanModelOutput ParsePlan(string json, PlanResourceLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PlanModelOutput output;
        try
        {
            var proposal = JsonSerializer.Deserialize<PlanProposalInput>(json, _planJsonOptions)
                ?? throw new JsonException("The structured plan proposal was empty.");
            output = CreatePlanOutput(proposal);
            Validate(output, planLimits: limits);
        }
        catch (JsonException exception)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.PlanSchemaMismatch,
                $"Check JSON field names and value types at {exception.Path ?? "$"}. The propose_plan arguments did not match the required plan schema.",
                toolName: "propose_plan",
                argumentsJson: json,
                providerFamily: null,
                toolOrdinal: null,
                toolCallCount: null,
                jsonException: exception,
                innerException: exception);
        }
        catch (Exception exception) when (exception is MalformedModelOutputException or ArgumentException)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.PlanSchemaMismatch,
                $"{exception.Message} The propose_plan arguments did not match the required plan schema.",
                toolName: "propose_plan",
                argumentsJson: json,
                providerFamily: null,
                toolOrdinal: null,
                toolCallCount: null,
                jsonException: null,
                innerException: exception);
        }

        return output;
    }

    /// <summary>Parses strict JSON into a validated bounded mutation-set output.</summary>
    public static MutationSetModelOutput ParseMutationSet(string json, WorkspaceResourceLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        MutationSetModelOutput output;
        try
        {
            output = JsonSerializer.Deserialize<MutationSetModelOutput>(json, _jsonOptions)
                ?? throw new JsonException("The structured mutation-set output was empty.");
        }
        catch (JsonException exception)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The propose_mutations arguments did not match the required mutation schema.",
                toolName: "propose_mutations",
                argumentsJson: json,
                providerFamily: null,
                toolOrdinal: null,
                toolCallCount: null,
                jsonException: exception,
                innerException: exception);
        }

        try
        {
            Validate(output, mutationLimits: limits);
        }
        catch (MalformedInvocationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MalformedModelOutputException or ArgumentException)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                "The propose_mutations arguments did not match the required mutation schema.",
                toolName: "propose_mutations",
                argumentsJson: json,
                providerFamily: null,
                toolOrdinal: null,
                toolCallCount: null,
                jsonException: null,
                innerException: exception);
        }

        return output;
    }

    /// <summary>Validates a model-authored tool invocation before it reaches execution.</summary>
    public static void ValidateInvocation(
        ToolRequestModelOutput tool,
        string? providerFamily = null,
        int? toolOrdinal = null,
        int? toolCallCount = null)
    {
        using var arguments = ParseInvocationArguments(tool, providerFamily, toolOrdinal, toolCallCount);
    }

    /// <summary>Validates a tool invocation whose schema accepts only an empty JSON object.</summary>
    public static void ValidateNoArgumentInvocation(
        ToolRequestModelOutput tool,
        string? providerFamily = null,
        int? toolOrdinal = null,
        int? toolCallCount = null)
    {
        using var arguments = ParseInvocationArguments(tool, providerFamily, toolOrdinal, toolCallCount);
        if (arguments.RootElement.EnumerateObject().Any())
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"The {tool.ToolName} tool does not accept arguments; use an empty JSON object.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                jsonException: null,
                innerException: null);
        }
    }

    private static JsonDocument ParseInvocationArguments(
        ToolRequestModelOutput tool,
        string? providerFamily,
        int? toolOrdinal,
        int? toolCallCount)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.ToolName))
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.MissingToolName,
                "Tool name is missing.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                jsonException: null,
                innerException: null);
        }

        try
        {
            var arguments = JsonDocument.Parse(tool.ArgumentsJson);
            if (arguments.RootElement.ValueKind == JsonValueKind.Object)
            {
                return arguments;
            }

            arguments.Dispose();
            throw CreateInvocationException(
                MalformedInvocationFailureKind.NonObjectArguments,
                "Tool arguments must be a JSON object.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                jsonException: null,
                innerException: null);
        }
        catch (MalformedInvocationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.InvalidJsonArguments,
                "Tool arguments are not valid JSON.",
                tool.ToolName,
                tool.ArgumentsJson,
                providerFamily,
                toolOrdinal,
                toolCallCount,
                exception,
                exception);
        }
    }

    private static PlanModelOutput CreatePlanOutput(PlanProposalInput proposal)
    {
        if (proposal.Steps is null)
        {
            throw new MalformedModelOutputException("steps must be a non-null array.");
        }

        var steps = new List<ImplementationPlanStep>(proposal.Steps.Count);
        for (var index = 0; index < proposal.Steps.Count; index++)
        {
            var step = proposal.Steps[index] ?? throw new MalformedModelOutputException($"steps[{index}] must be an object.");

            if (step.FileIntents is null)
            {
                throw new MalformedModelOutputException($"steps[{index}].fileIntents must be a non-null array.");
            }

            steps.Add(new ImplementationPlanStep
            {
                StepId = StepId.New(),
                Title = step.Title,
                Description = step.Description,
                FileIntents = step.FileIntents,
                ExpectedOutcome = step.ExpectedOutcome,
                Validation = step.Validation ?? throw new MalformedModelOutputException($"steps[{index}].validation must be a non-null array."),
            });
        }

        return new PlanModelOutput(new ImplementationPlan
        {
            Summary = proposal.Summary,
            Steps = steps,
            Risks = proposal.Risks ?? throw new MalformedModelOutputException("risks must be a non-null array."),
            OutstandingQuestions = proposal.OutstandingQuestions ?? throw new MalformedModelOutputException("outstandingQuestions must be a non-null array."),
        });
    }

    private static MalformedInvocationException CreateInvocationException(
        MalformedInvocationFailureKind kind,
        string safeMessage,
        string? toolName,
        string? argumentsJson,
        string? providerFamily,
        int? toolOrdinal,
        int? toolCallCount,
        JsonException? jsonException,
        Exception? innerException)
    {
        var diagnostic = new MalformedInvocationDiagnostic
        {
            Kind = kind,
            SafeMessage = BoundSingleLine(safeMessage, 512),
            ToolName = BoundNullable(toolName, 128),
            ToolOrdinal = toolOrdinal,
            ToolCallCount = toolCallCount,
            ProviderFamily = BoundNullable(providerFamily, 64),
            ArgumentCharacterCount = argumentsJson?.Length,
            ArgumentSha256 = argumentsJson is null
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(argumentsJson))).ToLowerInvariant(),
            JsonPath = BoundNullable(jsonException?.Path, 256),
            JsonLineNumber = jsonException?.LineNumber,
            JsonBytePositionInLine = jsonException?.BytePositionInLine,
        };
        return innerException is null
            ? new MalformedInvocationException(diagnostic)
            : new MalformedInvocationException(diagnostic, innerException);
    }

    private static string? BoundNullable(string? value, int maximumCharacters)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : BoundSingleLine(value, maximumCharacters);
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var character in value)
        {
            if (builder.Length == maximumCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static void ValidatePlan(ImplementationPlan plan, PlanResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        limits.Validate();
        if (plan.SchemaVersion != 2)
        {
            throw new MalformedModelOutputException($"Unsupported plan schema version {plan.SchemaVersion}; expected 2.");
        }

        if (plan.Revision <= 0)
        {
            throw new MalformedModelOutputException("Plan revision must be positive.");
        }

        ValidatePlanText(plan.Summary, limits.MaximumSummaryCharacters, "summary");
        if (plan.Steps is null || plan.Steps.Count < 1 || plan.Steps.Count > limits.MaximumSteps)
        {
            throw new MalformedModelOutputException($"steps must contain between 1 and {limits.MaximumSteps} entries.");
        }

        ValidatePlanTextItems(plan.Risks, limits.MaximumMetadataItems, limits.MaximumSummaryCharacters, "risks");
        ValidatePlanTextItems(plan.OutstandingQuestions, limits.MaximumMetadataItems, limits.MaximumSummaryCharacters, "outstandingQuestions");
        var stepIds = new HashSet<StepId>();
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var path = $"steps[{index}]";
            if (step is null)
            {
                throw new MalformedModelOutputException($"{path} must be an object.");
            }

            if (step.StepId == default || !stepIds.Add(step.StepId))
            {
                throw new MalformedModelOutputException($"{path}.stepId must be a unique nonempty host identity.");
            }

            ValidatePlanText(step.Title, limits.MaximumTitleCharacters, $"{path}.title");
            ValidatePlanText(step.Description, limits.MaximumDescriptionCharacters, $"{path}.description");
            ValidatePlanText(step.ExpectedOutcome, limits.MaximumSummaryCharacters, $"{path}.expectedOutcome");
            ValidatePlanTextItems(step.Validation, limits.MaximumMetadataItems, limits.MaximumSummaryCharacters, $"{path}.validation");
            if (step.FileIntents is null
                || step.FileIntents.Count < 1
                || step.FileIntents.Count > limits.MaximumMetadataItems)
            {
                throw new MalformedModelOutputException($"{path}.fileIntents must contain between 1 and {limits.MaximumMetadataItems} entries.");
            }

            for (var intentIndex = 0; intentIndex < step.FileIntents.Count; intentIndex++)
            {
                var intent = step.FileIntents[intentIndex];
                var intentPath = $"{path}.fileIntents[{intentIndex}]";
                if (intent is null || !Enum.IsDefined(intent.Kind))
                {
                    throw new MalformedModelOutputException($"{intentPath}.kind must be Modify, Create, Delete, Move, or Rename.");
                }

                if (IsInvalidPlanPath(intent.Path, limits))
                {
                    throw new MalformedModelOutputException($"{intentPath}.path must be a nonempty repository-relative path without '..', at most {limits.MaximumPathCharacters} characters.");
                }

                var hasDestination = !string.IsNullOrWhiteSpace(intent.DestinationPath);
                var requiresDestination = intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename;
                if (requiresDestination != hasDestination || (hasDestination && IsInvalidPlanPath(intent.DestinationPath, limits)))
                {
                    throw new MalformedModelOutputException($"{intentPath}.destinationPath must be a bounded repository-relative path for Move/Rename and omitted for other kinds.");
                }
            }
        }
    }

    private static void ValidatePlanText(string? value, int maximumCharacters, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters)
        {
            throw new MalformedModelOutputException($"{path} must be nonempty text with at most {maximumCharacters} characters.");
        }
    }

    private static void ValidatePlanTextItems(IReadOnlyList<string>? values, int maximumItems, int maximumCharacters, string path)
    {
        if (values is null || values.Count > maximumItems)
        {
            throw new MalformedModelOutputException($"{path} must be an array with at most {maximumItems} entries.");
        }

        for (var index = 0; index < values.Count; index++)
        {
            ValidatePlanText(values[index], maximumCharacters, $"{path}[{index}]");
        }
    }

    private static bool IsInvalidPlanPath(string? path, PlanResourceLimits limits)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length > limits.MaximumPathCharacters
            || Path.IsPathRooted(path)
            || segments.Contains("..", StringComparer.Ordinal);
    }

    private static void ValidateMutationSet(MutationSet mutationSet, WorkspaceResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        limits.Validate();
        if (mutationSet.MutationSetId == default
            || mutationSet.SessionId == default
            || mutationSet.WorkspaceId == default
            || mutationSet.BaselineCapturedAt == default
            || string.IsNullOrWhiteSpace(mutationSet.Rationale)
            || mutationSet.Rationale.Length > limits.MaximumRationaleCharacters
            || mutationSet.Mutations is null
            || (mutationSet.Mutations.Count < 1 || mutationSet.Mutations.Count > limits.MaximumMutations)
            || mutationSet.AffectedProjects is null
            || mutationSet.AffectedProjects.Count > limits.MaximumMutationMetadataItems
            || mutationSet.ExpectedDiagnosticsResolved is null
            || mutationSet.ExpectedDiagnosticsResolved.Count > limits.MaximumMutationMetadataItems
            || mutationSet.ExpectedTests is null
            || mutationSet.ExpectedTests.Count > limits.MaximumMutationMetadataItems
            || string.IsNullOrWhiteSpace(mutationSet.ValidationPolicy)
            || mutationSet.ValidationPolicy.Length > limits.MaximumValidationPolicyCharacters
            || !Enum.IsDefined(mutationSet.Risk)
            || !Enum.IsDefined(mutationSet.RequiredApproval))
        {
            throw new MalformedModelOutputException(
                $"A mutation set requires stable ownership, an exact baseline, rationale, and 1..{limits.MaximumMutations} mutations within the configured metadata limits.");
        }

        var ids = new HashSet<MutationId>();
        long replacementCharacters = 0;
        foreach (var mutation in mutationSet.Mutations)
        {
            if (mutation is null || mutation.ReplacementText is null)
            {
                throw new MalformedModelOutputException(
                    "Mutation entries and replacement content cannot be null.");
            }

            replacementCharacters += mutation.ReplacementText.Length;
            var segments = mutation.RelativePath.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (mutation.MutationId == default
                || !ids.Add(mutation.MutationId)
                || string.IsNullOrWhiteSpace(mutation.RelativePath)
                || mutation.RelativePath.Length > limits.MaximumMutationPathCharacters
                || Path.IsPathRooted(mutation.RelativePath)
                || segments.Contains("..", StringComparer.Ordinal)
                || mutation.SchemaVersion != 1
                || mutation.Type is not MutationType.CreateFile
                    and not MutationType.DeleteFile
                    and not MutationType.MoveFile
                    and not MutationType.ReplaceText
                    and not MutationType.RenameSymbol
                || mutation.StartOffset < 0
                || mutation.Length < 0
                || mutation.ExpectedText?.Length > limits.MaximumMutationCharacters
                || mutation.RelatedSymbolId?.Length > limits.MaximumMutationSymbolIdCharacters
                || !Enum.IsDefined(mutation.DestinationExpectation)
                || (mutation.LifecycleRisk is not null && !Enum.IsDefined(mutation.LifecycleRisk.Value))
                || (mutation.ProjectFilePath is not null
                    && (mutation.ProjectFilePath.Length > limits.MaximumMutationPathCharacters
                        || Path.IsPathRooted(mutation.ProjectFilePath)
                        || mutation.ProjectFilePath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))))
            {
                throw new MalformedModelOutputException(
                    "Mutations require unique ids, bounded text, non-negative ranges, and repository-relative paths.");
            }

            if (mutation.Type == MutationType.CreateFile
                && (mutation.StartOffset != 0
                    || mutation.Length != 0
                    || mutation.ExpectedText is not null
                    || mutation.BaselineSha256 is not null
                    || mutation.ExpectedIdentity is not null
                    || mutation.DestinationRelativePath is not null
                    || mutation.ReplacementText != (mutation.Content?.Text ?? mutation.ReplacementText)))
            {
                throw new MalformedModelOutputException(
                    "Create-file mutations require absent destination state, zero offsets, and no baseline identity.");
            }

            if (mutation.Type == MutationType.DeleteFile
                && (mutation.StartOffset != 0
                    || mutation.Length != 0
                    || mutation.ExpectedText is not null
                    || mutation.ReplacementText.Length != 0
                    || mutation.Content is not null
                    || mutation.DestinationRelativePath is not null
                    || mutation.ExpectedIdentity is null))
            {
                throw new MalformedModelOutputException(
                    "Delete-file mutations require an exact identity and no replacement content or destination.");
            }

            if (mutation.Type == MutationType.MoveFile)
            {
                var destination = mutation.DestinationRelativePath ?? string.Empty;
                var destinationSegments = destination.Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (mutation.StartOffset != 0
                    || mutation.Length != 0
                    || mutation.ExpectedText is not null
                    || mutation.ReplacementText.Length != 0
                    || mutation.ExpectedIdentity is null
                    || string.IsNullOrWhiteSpace(destination)
                    || destination.Length > limits.MaximumMutationPathCharacters
                    || Path.IsPathRooted(destination)
                    || destinationSegments.Contains("..", StringComparer.Ordinal)
                    || string.Equals(
                        mutation.RelativePath.Replace('\\', '/'),
                        destination.Replace('\\', '/'),
                        StringComparison.Ordinal))
                {
                    throw new MalformedModelOutputException(
                        "Move-file mutations require an exact source identity and a distinct absent repository-relative destination.");
                }
            }

            if (mutation.ExpectedIdentity is { } identity
                && (identity.ByteLength < 0 || !IsSha256(identity.Sha256)))
            {
                throw new MalformedModelOutputException("Lifecycle source identities require an exact SHA-256 and byte count.");
            }

            if (mutation.Content is { } content
                && (content.Text.Length > limits.MaximumMutationCharacters
                    || (content.Sha256 is not null && !IsSha256(content.Sha256))
                    || (content.Encoding is { } encoding && !Enum.IsDefined(encoding))
                    || (content.Newline is { } newline && !Enum.IsDefined(newline))))
            {
                throw new MalformedModelOutputException("Lifecycle content encoding, newline, hash, or size is invalid.");
            }
        }

        if (replacementCharacters > limits.MaximumMutationCharacters)
        {
            throw new MalformedModelOutputException(
                $"Mutation replacement content exceeds the configured {limits.MaximumMutationCharacters} character proposal limit.");
        }
    }

    private sealed record PlanProposalInput(
        string Summary,
        IReadOnlyList<PlanProposalStepInput?>? Steps,
        IReadOnlyList<string>? Risks,
        IReadOnlyList<string>? OutstandingQuestions);

    private sealed record PlanProposalStepInput(
        string Title,
        string Description,
        IReadOnlyList<PlanFileIntent>? FileIntents,
        string ExpectedOutcome,
        IReadOnlyList<string>? Validation);

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
    }
}
