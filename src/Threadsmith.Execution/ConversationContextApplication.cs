namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;

/// <summary>Handles shared interactive and headless conversation-context inspection and compaction commands.</summary>
public sealed class ConversationContextApplication :
    ICommandHandler<GetContextInspectionCommand, ContextInspectionProjection?>,
    ICommandHandler<RequestConversationCompactionCommand, bool>
{
    private readonly IContextAssembler _assembler;
    private readonly IConversationCompactor _compactor;
    private readonly IEvidenceStore _evidence;

    /// <summary>Initializes a new instance of the <see cref="ConversationContextApplication"/> class.</summary>
    public ConversationContextApplication(
        IContextAssembler assembler,
        IConversationCompactor compactor,
        IEvidenceStore evidence)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(compactor);
        ArgumentNullException.ThrowIfNull(evidence);
        _assembler = assembler;
        _compactor = compactor;
        _evidence = evidence;
    }

    /// <inheritdoc />
    public Task<ContextInspectionProjection?> HandleAsync(
        GetContextInspectionCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_assembler.GetInspection(command.RunId));
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        RequestConversationCompactionCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _compactor.CompactAtTurnBoundaryAsync(
            command.SessionId,
            _evidence.Snapshot(command.SessionId),
            force: true,
            cancellationToken);
        return result.Outcome is ConversationCompactionOutcomeKind.Completed
            or ConversationCompactionOutcomeKind.AlreadyCompacted
            or ConversationCompactionOutcomeKind.NotRequired;
    }
}
