namespace Threadsmith.Models;

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
                if (string.IsNullOrWhiteSpace(tool.ToolName))
                {
                    throw new MalformedModelOutputException("Tool name is empty.");
                }

                try
                {
                    using var arguments = JsonDocument.Parse(tool.ArgumentsJson);
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new MalformedModelOutputException(
                            "Tool arguments must be a JSON object.");
                    }
                }
                catch (JsonException exception)
                {
                    throw new MalformedModelOutputException(
                        "Tool arguments are not valid JSON.",
                        exception);
                }

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
            output = JsonSerializer.Deserialize<PlanModelOutput>(json, _jsonOptions)
                ?? throw new JsonException("The structured plan output was empty.");
        }
        catch (JsonException exception)
        {
            throw new MalformedModelOutputException(
                "The provider did not return strict plan JSON.",
                exception);
        }

        Validate(output);
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
            throw new MalformedModelOutputException(
                "The provider did not return strict mutation-set JSON.",
                exception);
        }

        Validate(output);
        return output;
    }

    private static void ValidatePlan(ImplementationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != 2)
        {
            throw new MalformedModelOutputException(
                $"Unsupported plan schema version {plan.SchemaVersion}; expected 2.");
        }

        if (plan.Revision <= 0
            || string.IsNullOrWhiteSpace(plan.Summary)
            || plan.Summary.Length > 4096
            || plan.Steps.Count is < 1 or > 100
            || plan.Risks.Count > 100
            || plan.Risks.Any(risk => string.IsNullOrWhiteSpace(risk) || risk.Length > 4096)
            || plan.OutstandingQuestions.Count > 100
            || plan.OutstandingQuestions.Any(question =>
                string.IsNullOrWhiteSpace(question) || question.Length > 4096))
        {
            throw new MalformedModelOutputException(
                "A plan requires a positive revision, summary, and 1..100 steps.");
        }

        var stepIds = new HashSet<StepId>();
        foreach (var step in plan.Steps)
        {
            if (step.StepId == default
                || !stepIds.Add(step.StepId)
                || string.IsNullOrWhiteSpace(step.Title)
                || step.Title.Length > 256
                || string.IsNullOrWhiteSpace(step.Description)
                || step.Description.Length > 8192
                || string.IsNullOrWhiteSpace(step.ExpectedOutcome)
                || step.ExpectedOutcome.Length > 4096
                || step.FileIntents.Count > 100
                || step.Validation.Count > 100
                || step.Validation.Any(expectation =>
                    string.IsNullOrWhiteSpace(expectation)
                    || expectation.Length > 4096)
                || step.FileIntents.Any(IsInvalidPlanFileIntent))
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

        bool hasDestination = !string.IsNullOrWhiteSpace(intent.DestinationPath);
        bool destinationAllowed = intent.Kind is PlanFileChangeKind.Move or PlanFileChangeKind.Rename;
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

        string[] segments = path.Replace('\\', '/')
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
            string[] segments = mutation.RelativePath.Replace('\\', '/')
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
                string destination = mutation.DestinationRelativePath ?? string.Empty;
                string[] destinationSegments = destination.Replace('\\', '/')
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

    private static bool IsSha256(string value)
    {
        return value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');
    }
}
