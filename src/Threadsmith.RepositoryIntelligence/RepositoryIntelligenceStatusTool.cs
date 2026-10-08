namespace Threadsmith.RepositoryIntelligence;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Closed, argument-free status request; future analysis actions need separate reviewed contracts.</summary>
internal sealed record RepositoryIntelligenceStatusInput;

/// <summary>Reports trusted controls through the ordinary governed tool path.</summary>
internal sealed class RepositoryIntelligenceStatusTool : Tool<RepositoryIntelligenceStatusInput, RepositoryIntelligenceStatus>
{
    /// <summary>The only completed repository-intelligence model action in this increment.</summary>
    internal const string ToolId = "repository_intelligence";
    private readonly Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> _resolve;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceStatusTool"/> class.</summary>
    internal RepositoryIntelligenceStatusTool(
        Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> resolve,
        IPromptLoader prompts)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        ArgumentNullException.ThrowIfNull(prompts);
        Definition = ToolDefinitionFactory.Create<RepositoryIntelligenceStatusInput, RepositoryIntelligenceStatus>(
            ToolId,
            prompts.Get(PromptFileNames.ToolRepositoryIntelligenceDescription),
            ToolCategory.RepositoryInspection,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(15),
            8192) with
        {
            SubagentAvailable = false,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<RepositoryIntelligenceStatus>> ExecuteAsync(
        RepositoryIntelligenceStatusInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(context.Invocation.RepositoryPath);
        var resolved = await _resolve(identity, cancellationToken);
        var controls = await resolved.Feature.CaptureAsync(identity, cancellationToken);
        return new ToolExecution<RepositoryIntelligenceStatus>(
            new RepositoryIntelligenceStatus(
                controls,
                AnalysisAvailable: false,
                "Analysis is unavailable until the governed operation and evidence pipeline is implemented."),
            []);
    }

    /// <inheritdoc />
    protected override void ValidateInput(RepositoryIntelligenceStatusInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        RepositoryIntelligenceStatusInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }
}
