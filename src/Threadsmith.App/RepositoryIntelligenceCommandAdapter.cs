namespace Threadsmith.App;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Tools;

/// <summary>Routes explicit host commands to the lazily created feature authority.</summary>
internal sealed class RepositoryIntelligenceCommandAdapter :
    ICommandHandler<GetRepositoryIntelligenceControlsCommand, RepositoryIntelligenceControlSnapshot>,
    ICommandHandler<GetRepositoryIntelligenceStatusCommand, RepositoryIntelligenceStatusReceipt>,
    ICommandHandler<InvokeRepositoryIntelligenceOperationCommand, RepositoryIntelligenceOperationReceipt>,
    ICommandHandler<SetRepositoryIntelligenceControlCommand, RepositoryIntelligenceControlSnapshot>,
    ICommandHandler<PreviewRepositoryIntelligenceOperationCommand, RepositoryIntelligenceOperationPreview>
{
    private readonly Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> _resolve;
    private readonly Func<SessionId, CancellationToken, Task<ToolInvocationContext>> _context;
    private readonly IToolInvocationPipeline _pipeline;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceCommandAdapter"/> class.</summary>
    internal RepositoryIntelligenceCommandAdapter(
        Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> resolve,
        Func<SessionId, CancellationToken, Task<ToolInvocationContext>> context,
        IToolInvocationPipeline pipeline)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceControlSnapshot> HandleAsync(
        GetRepositoryIntelligenceControlsCommand command,
        CancellationToken cancellationToken = default)
    {
        var receipt = await HandleAsync(
            new GetRepositoryIntelligenceStatusCommand(command.SessionId, command.RepositoryIdentity),
            cancellationToken);
        return receipt.Succeeded && receipt.Status is { } status
            ? status.Controls
            : throw new InvalidOperationException(receipt.Error ?? "Repository intelligence status was unavailable.");
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceStatusReceipt> HandleAsync(
        GetRepositoryIntelligenceStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(command.SessionId, command.RepositoryIdentity, "{}", cancellationToken);
        var status = result.Succeeded && result.ResultJson is { } json
            ? JsonSerializer.Deserialize<RepositoryIntelligenceStatus>(json)
            : null;
        return new RepositoryIntelligenceStatusReceipt(
            result.ToolInvocationId,
            result.Succeeded,
            status,
            result.Succeeded ? null : result.ErrorClassification.ToString(),
            result.Error);
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceOperationReceipt> HandleAsync(
        InvokeRepositoryIntelligenceOperationCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await InvokeAsync(command.SessionId, command.RepositoryIdentity, command.ArgumentsJson, cancellationToken);
        return new RepositoryIntelligenceOperationReceipt(
            result.ToolInvocationId,
            result.Succeeded,
            result.ResultJson,
            result.Succeeded ? null : result.ErrorClassification.ToString(),
            result.Error);
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceControlSnapshot> HandleAsync(
        SetRepositoryIntelligenceControlCommand command,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _resolve(command.RepositoryIdentity, cancellationToken);
        return await resolved.Feature.SetAsync(command.RepositoryIdentity, command.Control, command.Enabled, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceOperationPreview> HandleAsync(
        PreviewRepositoryIntelligenceOperationCommand command,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _resolve(command.Selection.RepositoryIdentity, cancellationToken);
        return await resolved.Feature.PreviewAsync(
            command.Selection,
            command.OneOffInvestigation,
            resolved.ProviderId,
            cancellationToken);
    }

    private async Task<ToolInvocationResult> InvokeAsync(
        SessionId sessionId, string repositoryIdentity, string argumentsJson, CancellationToken cancellationToken)
    {
        var context = await _context(sessionId, cancellationToken);
        if (!string.Equals(RepositoryIdentity.Create(context.RepositoryPath), repositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Repository intelligence operation belongs to another checkout.");
        }

        return await _pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = sessionId,
                RunId = RunId.New(),
                Phase = RunPhase.Intake,
                ToolId = RepositoryIntelligenceStatusTool.ToolId,
                ArgumentsJson = argumentsJson,
                Context = context with { RequestedBy = "user" },
            },
            cancellationToken);
    }
}
