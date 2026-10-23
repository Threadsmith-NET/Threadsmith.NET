namespace Threadsmith.Interaction.Contracts;

using Threadsmith.Core;

/// <summary>Optional local clarification presentation, independent of approval semantics.</summary>
public interface IUserQuestionSurface
{
    /// <summary>Presents a group until explicitly submitted or cancelled; hiding preserves its draft.</summary>
    Task<UserQuestionResult> AskAsync(UserQuestionRequest request, CancellationToken cancellationToken = default);
}
