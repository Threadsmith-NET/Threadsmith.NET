namespace Threadsmith.Skills;

using System.Text.Json;
using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>Checks focused-only schema, assignment identity, actual delivered ranges and criterion provenance.</summary>
internal sealed class FocusedReviewCompletionPolicy : IFocusedReviewCompletionPolicy
{
    private readonly BoundedJsonSchemaValidator _schemas;
    private readonly SkillCompiledSchema _compiled;
    private readonly FocusedReviewTarget _target;
    private readonly Dictionary<string, List<FocusedReviewRange>> _reads = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly SkillBudget _budget;
    private readonly IReadOnlyList<string> _privateFragments;
    private int _modelTurns;
    private int _toolCalls;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewCompletionPolicy"/> class.</summary>
    internal FocusedReviewCompletionPolicy(
        FocusedReviewBinding binding,
        string instructions,
        string schema,
        BoundedJsonSchemaValidator schemas,
        FocusedReviewTarget target,
        int maximumCorrections,
        SkillBudget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCorrections);
        Binding = binding;
        Instructions = instructions;
        OutputSchema = schema;
        MaximumCorrections = maximumCorrections;
        _budget = budget;
        _privateFragments = new[] { instructions, schema }.SelectMany(text =>
        {
            var normalized = NormalizeDefinition(text);
            return Enumerable.Range(0, Math.Max(0, ((normalized.Length - 96) / 32) + 1))
                .Select(index => normalized.Substring(index * 32, 96));
        }).ToArray();
        _schemas = schemas;
        _compiled = schemas.Compile(schema);
        _target = target;
    }

    /// <inheritdoc />
    public FocusedReviewBinding Binding { get; }

    /// <inheritdoc />
    public string Instructions { get; }

    /// <inheritdoc />
    public string OutputSchema { get; }

    /// <inheritdoc />
    public int MaximumCorrections { get; }

    /// <inheritdoc />
    public void AdmitModelTurn()
    {
        if (++_modelTurns > _budget.ModelTurns / 4)
        {
            throw new InvalidOperationException("The focused review model-turn allocation is exhausted.");
        }
    }

    /// <inheritdoc />
    public void AdmitToolCalls(int count)
    {
        _toolCalls = checked(_toolCalls + count);
        if (_toolCalls > _budget.ToolCalls / 4)
        {
            throw new InvalidOperationException("The focused review tool-call allocation is exhausted.");
        }
    }

    /// <inheritdoc />
    public void RecordRead(
        string path,
        int startLine,
        int endLine)
    {
        lock (_gate)
        {
            if (!_reads.TryGetValue(path, out var ranges))
            {
                _reads.Add(path, ranges = []);
            }

            ranges.Add(new FocusedReviewRange(startLine, endLine));
        }
    }

    /// <inheritdoc />
    public string Validate(
        AgentAssignment assignment,
        string response)
    {
        if (assignment.FocusedReview != Binding || assignment.Role != Binding.Role
            || Binding.ContractVersion != 1 || Binding.SnapshotIdentity != _target.Identity)
        {
            throw new InvalidDataException("Focused assignment provenance does not match.");
        }

        var canonical = _schemas.Validate(_compiled, response);
        using var document = JsonDocument.Parse(canonical);
        var root = document.RootElement;
        foreach (var value in StringValues(root))
        {
            var normalized = NormalizeDefinition(value);
            if (_privateFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Private procedure definitions cannot appear in public review data.");
            }
        }

        if (root.GetProperty("issues").GetArrayLength() > _budget.ReviewerFindings / 4)
        {
            throw new InvalidDataException("The focused review finding allocation is exhausted.");
        }

        foreach (var section in new[] { "strengths", "architecture", "issues", "observations", "criteria" })
        {
            foreach (var item in root.GetProperty(section).EnumerateArray())
            {
                var citations = item.GetProperty("evidence").EnumerateArray().ToArray();
                if (section is "strengths" or "architecture" or "issues" && citations.Length == 0)
                {
                    throw new InvalidDataException("Positive, architectural and defect claims require inspected evidence.");
                }

                foreach (var citation in citations)
                {
                    ValidateLocation(citation, requireScope: false);
                }

                if (section == "issues")
                {
                    ValidateLocation(item.GetProperty("location"), requireScope: true);
                    if (!double.IsFinite(item.GetProperty("confidence").GetDouble()))
                    {
                        throw new InvalidDataException("Confidence must be finite.");
                    }
                }
            }
        }

        var criterionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assessment in root.GetProperty("criteria").EnumerateArray())
        {
            var id = assessment.GetProperty("criterionId").GetString();
            var criterion = _target.Requirements?.Criteria.SingleOrDefault(item => item.Id == id);
            if (criterion is null || !criterionIds.Add(criterion.Id))
            {
                throw new InvalidDataException("Criterion is unknown or duplicated.");
            }

            var status = assessment.GetProperty("status").GetString();
            if (status is "Met" or "Partially met" or "Not met" && assessment.GetProperty("evidence").GetArrayLength() == 0)
            {
                throw new InvalidDataException("Assessed criteria require inspected evidence.");
            }

            if (criterion.RequiresExecution && status == "Met")
            {
                throw new InvalidDataException("Runtime/manual acceptance cannot be marked Met by static review.");
            }
        }

        foreach (var secret in new[]
        {
            Binding.Package.SkillId.Value, Binding.Package.PackageId, Binding.Package.Digest.Value,
            Binding.InstructionDigest, Binding.SchemaDigest, Binding.RecipeDigest,
        })
        {
            if (canonical.Contains(secret, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Private procedure provenance cannot appear in public review data.");
            }
        }

        return canonical;
    }

    /// <summary>Classifies actual validated coverage for both workflow status and report presentation.</summary>
    internal static string GetReviewStatus(FocusedReviewTarget target, IReadOnlyList<AgentRunOutcome> outcomes)
    {
        var completed = outcomes.Count(outcome => outcome.Status == AgentRunStatus.Completed
            && outcome.FocusedReviewValidated && outcome.Response is not null);
        return completed == 0 ? "failed" : completed == 4 && target.Exclusions.Count == 0 ? "complete" : "partial";
    }

    private static string NormalizeDefinition(string value) => Regex.Replace(System.Net.WebUtility.HtmlDecode(value), @"\s+", string.Empty).ToLowerInvariant();

    private static IEnumerable<string> StringValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            yield return value.GetString() ?? string.Empty;
        }
        else if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            IEnumerable<JsonElement> children = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : value.EnumerateObject().Select(property => property.Value);
            foreach (var child in children.SelectMany(StringValues))
            {
                yield return child;
            }
        }
    }

    private void ValidateLocation(
        JsonElement location,
        bool requireScope)
    {
        var path = location.GetProperty("path").GetString() ?? string.Empty;
        FocusedReviewInput.ValidateRelativePath(path);
        var start = location.GetProperty("startLine").GetInt32();
        var end = location.GetProperty("endLine").GetInt32();
        var file = _target.Files.SingleOrDefault(item => item.Path == path);
        if (file is null || start < 1 || end < start || end > FocusedReviewTargetCapture.Lines(file.Content).Length)
        {
            throw new InvalidDataException("Citation is outside the captured source range.");
        }

        lock (_gate)
        {
            if (!_reads.TryGetValue(path, out var reads) || !reads.Any(range => range.Start <= start && range.End >= end))
            {
                throw new InvalidDataException("Citation refers to source that was not delivered to this reviewer.");
            }
        }

        if (requireScope && (!file.InScope || ((_target.ComparisonRevision ?? _target.MergeBase) is not null && !file.ChangedRanges.Any(range => range.Start <= end && range.End >= start))))
        {
            throw new InvalidDataException("Issue location is outside the selected snapshot or changed lines.");
        }
    }
}
