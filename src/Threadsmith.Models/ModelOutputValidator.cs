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
    public static void Validate(ModelOutput output, int supportedSchemaVersion = 1)
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
                ValidatePlan(plan.Plan);
                break;
            case MutationSetModelOutput mutationSet:
                ValidateMutationSet(mutationSet.MutationSet);
                break;
            case TextModelOutput:
                break;
            default:
                throw new MalformedModelOutputException(
                    $"Unsupported model-output type '{output.GetType().Name}'.");
        }
    }

    /// <summary>Parses strict JSON into a validated structured plan output.</summary>
    public static PlanModelOutput ParsePlan(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PlanModelOutput output;
        try
        {
            var proposal = JsonSerializer.Deserialize<PlanProposalInput>(json, _planJsonOptions)
                ?? throw new JsonException("The structured plan proposal was empty.");
            output = CreatePlanOutput(proposal);
        }
        catch (JsonException exception)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.PlanSchemaMismatch,
                "The propose_plan arguments did not match the required plan schema.",
                toolName: "propose_plan",
                argumentsJson: json,
                providerFamily: null,
                toolOrdinal: null,
                toolCallCount: null,
                jsonException: exception,
                innerException: exception);
        }

        try
        {
            Validate(output);
        }
        catch (MalformedInvocationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MalformedModelOutputException or ArgumentException)
        {
            throw CreateInvocationException(
                MalformedInvocationFailureKind.PlanSchemaMismatch,
                "The propose_plan arguments did not match the required plan schema.",
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
    public static MutationSetModelOutput ParseMutationSet(string json)
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
            Validate(output);
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
            using var arguments = JsonDocument.Parse(tool.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
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
        if (proposal.Steps is null
            || proposal.Risks is null
            || proposal.OutstandingQuestions is null)
        {
            throw new JsonException("Plan collections cannot be null.");
        }

        var steps = new List<ImplementationPlanStep>(proposal.Steps.Count);
        foreach (var step in proposal.Steps)
        {
            if (step is null || step.FileIntents is null || step.Validation is null)
            {
                throw new JsonException("Plan collections cannot be null.");
            }

            if (!Guid.TryParseExact(step.StepId, "D", out var stepId))
            {
                throw new JsonException("Plan step ids must be UUID strings.");
            }

            steps.Add(new ImplementationPlanStep
            {
                StepId = new StepId(stepId),
                Title = step.Title,
                Description = step.Description,
                FileIntents = step.FileIntents,
                ExpectedOutcome = step.ExpectedOutcome,
                Validation = step.Validation,
            });
        }

        return new PlanModelOutput(new ImplementationPlan
        {
            SchemaVersion = proposal.SchemaVersion,
            Revision = proposal.Revision,
            Summary = proposal.Summary,
            Steps = steps,
            Risks = proposal.Risks,
            OutstandingQuestions = proposal.OutstandingQuestions,
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

    private static void ValidatePlan(ImplementationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != 2)
        {
            throw new MalformedModelOutputException(
                $"Unsupported plan schema version {plan.SchemaVersion}; expected 2.");
        }

        var steps = plan.Steps;
        var risks = plan.Risks;
        var outstandingQuestions = plan.OutstandingQuestions;
        if (plan.Revision <= 0
            || string.IsNullOrWhiteSpace(plan.Summary)
            || plan.Summary.Length > 4096
            || steps is null
            || steps.Count is < 1 or > 100
            || risks is null
            || risks.Count > 100
            || risks.Any(risk => string.IsNullOrWhiteSpace(risk) || risk.Length > 4096)
            || outstandingQuestions is null
            || outstandingQuestions.Count > 100
            || outstandingQuestions.Any(question =>
                string.IsNullOrWhiteSpace(question) || question.Length > 4096))
        {
            throw new MalformedModelOutputException(
                "A plan requires a positive revision, summary, and 1..100 steps.");
        }

        var stepIds = new HashSet<StepId>();
        foreach (var step in steps)
        {
            if (step is null)
            {
                throw new MalformedModelOutputException(
                    "Plan steps require unique ids, bounded text, and repository-relative file intents.");
            }

            var fileIntents = step.FileIntents;
            var validation = step.Validation;
            if (step.StepId == default
                || !stepIds.Add(step.StepId)
                || string.IsNullOrWhiteSpace(step.Title)
                || step.Title.Length > 256
                || string.IsNullOrWhiteSpace(step.Description)
                || step.Description.Length > 8192
                || string.IsNullOrWhiteSpace(step.ExpectedOutcome)
                || step.ExpectedOutcome.Length > 4096
                || fileIntents is null
                || fileIntents.Count > 100
                || validation is null
                || validation.Count > 100
                || validation.Any(expectation =>
                    string.IsNullOrWhiteSpace(expectation)
                    || expectation.Length > 4096)
                || fileIntents.Any(IsInvalidPlanFileIntent))
            {
                throw new MalformedModelOutputException(
                    "Plan steps require unique ids, bounded text, and repository-relative file intents.");
            }
        }
    }

    private static bool IsInvalidPlanFileIntent(PlanFileIntent? intent)
    {
        if (intent is null)
        {
            return true;
        }

        var hasDestination = !string.IsNullOrWhiteSpace(intent.DestinationPath);
        var destinationAllowed = intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename;
        return !Enum.IsDefined(intent.Kind)
            || IsInvalidPlanPath(intent.Path)
            || (destinationAllowed != hasDestination)
            || (hasDestination && IsInvalidPlanPath(intent.DestinationPath ?? string.Empty));
    }

    private static bool IsInvalidPlanPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return path.Length > 1024
            || Path.IsPathRooted(path)
            || segments.Contains("..", StringComparer.Ordinal);
    }

    private static void ValidateMutationSet(MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        if (mutationSet.MutationSetId == default
            || mutationSet.SessionId == default
            || mutationSet.WorkspaceId == default
            || mutationSet.BaselineCapturedAt == default
            || string.IsNullOrWhiteSpace(mutationSet.Rationale)
            || mutationSet.Rationale.Length > 8192
            || mutationSet.Mutations is null
            || mutationSet.Mutations.Count is < 1 or > 100
            || mutationSet.AffectedProjects is null
            || mutationSet.AffectedProjects.Count > 100
            || mutationSet.ExpectedDiagnosticsResolved is null
            || mutationSet.ExpectedDiagnosticsResolved.Count > 100
            || mutationSet.ExpectedTests is null
            || mutationSet.ExpectedTests.Count > 100
            || string.IsNullOrWhiteSpace(mutationSet.ValidationPolicy)
            || mutationSet.ValidationPolicy.Length > 256
            || !Enum.IsDefined(mutationSet.Risk)
            || !Enum.IsDefined(mutationSet.RequiredApproval))
        {
            throw new MalformedModelOutputException(
                "A mutation set requires stable ownership, an exact baseline, rationale, and 1..100 mutations.");
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
                || mutation.RelativePath.Length > 1024
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
                || mutation.ExpectedText?.Length > 4 * 1024 * 1024
                || mutation.RelatedSymbolId?.Length > 4096
                || !Enum.IsDefined(mutation.DestinationExpectation)
                || (mutation.LifecycleRisk is not null && !Enum.IsDefined(mutation.LifecycleRisk.Value))
                || (mutation.ProjectFilePath is not null
                    && (mutation.ProjectFilePath.Length > 1024
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
                    || destination.Length > 1024
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
                && (content.Text.Length > 4 * 1024 * 1024
                    || (content.Sha256 is not null && !IsSha256(content.Sha256))
                    || !Enum.IsDefined(content.Encoding)
                    || !Enum.IsDefined(content.Newline)))
            {
                throw new MalformedModelOutputException("Lifecycle content encoding, newline, hash, or size is invalid.");
            }
        }

        if (replacementCharacters > 4 * 1024 * 1024)
        {
            throw new MalformedModelOutputException(
                "Mutation replacement content exceeds the 4 MiB proposal limit.");
        }
    }

    private sealed record PlanProposalInput(
        int SchemaVersion,
        int Revision,
        string Summary,
        IReadOnlyList<PlanProposalStepInput?>? Steps,
        IReadOnlyList<string>? Risks,
        IReadOnlyList<string>? OutstandingQuestions);

    private sealed record PlanProposalStepInput(
        string StepId,
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
