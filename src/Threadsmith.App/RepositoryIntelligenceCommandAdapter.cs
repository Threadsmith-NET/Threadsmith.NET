namespace Threadsmith.App;

using Threadsmith.Core;
using Threadsmith.RepositoryIntelligence;

/// <summary>Routes explicit host commands to the lazily created feature authority.</summary>
internal sealed class RepositoryIntelligenceCommandAdapter :
    ICommandHandler<GetRepositoryIntelligenceControlsCommand, RepositoryIntelligenceControlSnapshot>,
    ICommandHandler<SetRepositoryIntelligenceControlCommand, RepositoryIntelligenceControlSnapshot>,
    ICommandHandler<PreviewRepositoryIntelligenceOperationCommand, RepositoryIntelligenceOperationPreview>
{
    private readonly Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> _resolve;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceCommandAdapter"/> class.</summary>
    internal RepositoryIntelligenceCommandAdapter(
        Func<string, CancellationToken, Task<(RepositoryIntelligenceFeature Feature, string? ProviderId)>> resolve)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceControlSnapshot> HandleAsync(
        GetRepositoryIntelligenceControlsCommand command,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _resolve(command.RepositoryIdentity, cancellationToken);
        return await resolved.Feature.CaptureAsync(command.RepositoryIdentity, cancellationToken);
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
}
