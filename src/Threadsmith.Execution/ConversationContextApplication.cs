namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Handles shared context inspection and retired manual fact-promotion commands.</summary>
public sealed class ConversationContextApplication :
    ICommandHandler<GetContextInspectionCommand, ContextInspectionProjection?>,
    ICommandHandler<RequestConversationCompactionCommand, bool>
{
    private readonly IContextAssembler _assembler;
    private readonly SessionUsageProjection? _usage;

    /// <summary>Initializes a new instance of the <see cref="ConversationContextApplication"/> class.</summary>
    public ConversationContextApplication(IContextAssembler assembler, SessionUsageProjection? usage = null)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        _assembler = assembler;
        _usage = usage;
    }

    /// <inheritdoc />
    public Task<ContextInspectionProjection?> HandleAsync(GetContextInspectionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usage = command.SessionId is { } session ? _usage?.GetRequestStatus(session)?.ContextUsage : null;
        var inspection = _assembler.GetInspection(usage?.RunId ?? command.RunId);
        return Task.FromResult(usage is null ? inspection : (inspection ?? new ContextInspectionProjection { RunId = usage.RunId }) with { RequestUsage = usage });
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(RequestConversationCompactionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Automatic conversation fact promotion has been retired. Model-generated active-turn compaction still runs when needed; use /memory remember <text> for explicit repository recall.");
    }
}
