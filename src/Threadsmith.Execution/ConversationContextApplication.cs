namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;

/// <summary>Handles shared context inspection and retired manual fact-promotion commands.</summary>
public sealed class ConversationContextApplication :
    ICommandHandler<GetContextInspectionCommand, ContextInspectionProjection?>,
    ICommandHandler<RequestConversationCompactionCommand, bool>
{
    private readonly IContextAssembler _assembler;

    /// <summary>Initializes a new instance of the <see cref="ConversationContextApplication"/> class.</summary>
    public ConversationContextApplication(IContextAssembler assembler)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        _assembler = assembler;
    }

    /// <inheritdoc />
    public Task<ContextInspectionProjection?> HandleAsync(GetContextInspectionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_assembler.GetInspection(command.RunId));
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(RequestConversationCompactionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Automatic conversation fact promotion has been retired. Model-generated active-turn compaction still runs when needed; use /memory remember <text> for explicit repository recall.");
    }
}
