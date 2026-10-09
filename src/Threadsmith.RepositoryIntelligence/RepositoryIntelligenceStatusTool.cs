namespace Threadsmith.RepositoryIntelligence;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Status request with an optional explicit, bounded deterministic capture.</summary>
internal sealed record RepositoryIntelligenceStatusInput
{
    /// <summary>Optional explicit deterministic profile request; absence remains status-only.</summary>
    public RepositoryProfileSelection? Profile { get; init; }

    /// <summary>Optional explicit invocation-only question and scope.</summary>
    public RepositoryInvestigationSelection? Investigate { get; init; }
}

/// <summary>Reports trusted controls through the ordinary governed tool path.</summary>
internal sealed class RepositoryIntelligenceStatusTool : Tool<RepositoryIntelligenceStatusInput, RepositoryIntelligenceOutput>, IPostSanitizationToolOutputBoundary
{
    /// <summary>Shared status and explicit profiling operation identity.</summary>
    internal const string ToolId = "repository_intelligence";
    private readonly IToolInvocationPipeline _pipeline;
    private readonly RepositoryInvestigation _investigation;
    private readonly bool _inferenceConfigured;
    private readonly Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> _resolve;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceStatusTool"/> class.</summary>
    internal RepositoryIntelligenceStatusTool(
        Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> resolve,
        IPromptLoader prompts,
        IToolInvocationPipeline pipeline,
        IBoundedModelInference? inference = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        ArgumentNullException.ThrowIfNull(prompts);
        _investigation = new RepositoryInvestigation(pipeline, inference, prompts);
        _inferenceConfigured = inference is not null;
        Definition = ToolDefinitionFactory.Create<RepositoryIntelligenceStatusInput, RepositoryIntelligenceOutput>(
            ToolId,
            prompts.Get(PromptFileNames.ToolRepositoryIntelligenceDescription),
            ToolCategory.RepositoryInspection,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(90),
            256 * 1024) with
        {
            SubagentAvailable = false,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<RepositoryIntelligenceOutput>> ExecuteAsync(
        RepositoryIntelligenceStatusInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(context.Invocation.RepositoryPath);
        var resolved = await _resolve(identity, cancellationToken);
        var controls = await resolved.Feature.CaptureAsync(identity, cancellationToken);
        RepositoryStructuralProfile? profile = null;
        RepositoryInvestigationResult? investigation = null;
        if (input.Investigate is { } question)
        {
            investigation = await _investigation.ExecuteAsync(resolved.Feature, question, context, cancellationToken);
        }

        if (input.Profile is { } selection)
        {
            await using var admission = await resolved.Feature.AdmitAsync(
                identity, RepositoryIntelligenceControl.Archeology, oneOff: true, cancellationToken);
            profile = await resolved.Feature.PrepareAsync(
                admission,
                token => new RepositoryProfileCollector(_pipeline, resolved.Feature.EvidenceLimits, resolved.Feature.GitLimits).CaptureAsync(
                    selection with { MaximumFiles = Math.Min(selection.MaximumFiles, resolved.Feature.MaximumFiles) },
                    context,
                    token),
                cancellationToken);
        }

        var output = new RepositoryIntelligenceOutput(
            controls,
            AnalysisAvailable: _inferenceConfigured && resolved.ProviderId is not null && resolved.Feature.ResourceLimits.MaximumModelCalls > 0,
            "Invocation-only investigation uses configured inference when available; deterministic profiling remains available. Persistent analysis and onboarding remain unavailable.",
            profile,
            investigation);
        output = BoundOutput(output, context.MaximumOutputBytes ?? Definition.MaximumOutputBytes, out var truncated);
        return new ToolExecution<RepositoryIntelligenceOutput>(output, [], truncated);
    }

    /// <inheritdoc />
    public PostSanitizationToolOutput BoundSanitizedOutput(
        string resultJson,
        string? modelResultContent,
        ToolInvocationContext context,
        int maximumOutputBytes)
    {
        var output = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(resultJson)
            ?? throw new InvalidDataException("Sanitized profile output is unavailable.");
        output = BoundOutput(output, maximumOutputBytes, out var truncated);
        return new PostSanitizationToolOutput(JsonSerializer.SerializeToElement(output).GetRawText(), modelResultContent, truncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(RepositoryIntelligenceStatusInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Investigate is { } investigation)
        {
            if (input.Profile is not null || string.IsNullOrWhiteSpace(investigation.Question) || investigation.Question.Length > 2048
                || investigation.Paths is null || investigation.Paths.Count is < 1 or > 8 || investigation.Paths.Any(string.IsNullOrWhiteSpace)
                || investigation.Paths.Any(path => path.Length > 2048) || string.IsNullOrWhiteSpace(investigation.Revision) || investigation.Revision.Length > 2048
                || !Enum.IsDefined(investigation.Kind) || investigation.MaximumFiles is < 1 or > 32
                || investigation.MaximumCommits is < 0 or > 100 || investigation.MaximumModelCalls is < 0 or > 32)
            {
                throw new ArgumentException("Choose one operation with a concrete question, explicit paths and supported work bounds.");
            }
        }

        if (input.Profile is not { } selection)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Revision);
        if (selection.Paths.Count > 8 || selection.Paths.Any(string.IsNullOrWhiteSpace)
            || selection.MaximumScannedPaths is < 1 or > 10000
            || selection.MaximumPaths is < 1 or > 200 || selection.MaximumFiles is < 1 or > 32
            || selection.MaximumBytes is < 1 or > 65536 || selection.MaximumSeconds is < 1 or > 60)
        {
            throw new ArgumentException("Profile bounds exceed the supported path, file, byte or time limits.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        RepositoryIntelligenceStatusInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    private static RepositoryIntelligenceOutput BoundOutput(RepositoryIntelligenceOutput output, int maximumBytes, out bool truncated)
    {
        truncated = false;
        while (Encoding.UTF8.GetByteCount(JsonSerializer.SerializeToElement(output).GetRawText()) > maximumBytes)
        {
            if (output.Investigation is { } investigation && investigation.Evidence.Any(item => item.Text is not null))
            {
                output = output with
                {
                    Investigation = investigation with
                    {
                        Evidence = investigation.Evidence.Select(item => item with { Text = null, State = "ExcerptOmittedByOutputBound" }).ToArray(),
                        Omissions = [.. investigation.Omissions, new RepositoryEvidenceOmission("ResultExcerptOutputByteLimit")],
                    },
                };
                truncated = true;
                continue;
            }

            if (output.Profile is not { } profile)
            {
                throw new InvalidDataException("The configured output bound cannot hold the required operation result and provenance.");
            }

            truncated = true;
            var omissions = profile.Omissions.Any(item => item.Reason == RepositoryProfileOmissionReason.OutputByteLimit)
                ? profile.Omissions : [.. profile.Omissions, new RepositoryProfileOmission(RepositoryProfileOmissionReason.OutputByteLimit)];
            profile = profile with { Omissions = omissions };

            // Halving keeps serialized admission logarithmic even for heavily escaped, long source paths.
            if (profile.Facts.Count > 0)
            {
                profile = profile with { Facts = profile.Facts.Take(profile.Facts.Count / 2).ToArray() };
            }
            else if (profile.DiscoveredFiles.Count > 0)
            {
                profile = profile with { DiscoveredFiles = profile.DiscoveredFiles.Take(profile.DiscoveredFiles.Count / 2).ToArray() };
            }
            else if (profile.Overlay.Count > 0)
            {
                profile = profile with { Overlay = profile.Overlay.Take(profile.Overlay.Count / 2).ToArray() };
            }
            else if (profile.Omissions.Count > 1 || profile.Omissions.Any(item => item.Path is not null))
            {
                profile = profile with { Omissions = [new RepositoryProfileOmission(RepositoryProfileOmissionReason.OutputByteLimit)] };
            }
            else
            {
                throw new InvalidDataException("The configured output bound cannot hold the required snapshot and selected scope.");
            }

            output = output with { Profile = profile };
        }

        return output;
    }
}
