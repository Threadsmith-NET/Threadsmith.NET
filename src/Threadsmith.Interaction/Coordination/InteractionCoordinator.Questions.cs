namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;

#pragma warning disable SA1601 // The canonical partial-type documentation is owned by InteractionCoordinator.cs.
public sealed partial class InteractionCoordinator
{
#pragma warning restore SA1601
    /// <summary>Serializes question presentation with the existing web approval input owner.</summary>
    public async Task<UserQuestionResult> PresentUserQuestionAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
    {
        await _modalGate.WaitAsync(cancellationToken);
        try
        {
            return _surface.Surface is IUserQuestionSurface questions
                ? await questions.AskAsync(request, cancellationToken)
                : new UserQuestionResult(UserQuestionStatus.Interrupted, []);
        }
        finally
        {
            _modalGate.Release();
        }
    }
}
