namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Core;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Restores archived visible messages through the ordinary semantic presentation path.</summary>
public sealed partial class InteractionCoordinator
{
    private async Task PresentResumedConversationAsync(
        ConversationStateSnapshot history,
        SessionTransitionResult transition,
        CancellationToken cancellationToken)
    {
        var collector = new ModelAnswerCollector(_displayOptions.RenderMarkdown, limits: _displayOptions.Limits.Markdown);
        var restorationWarnings = transition.Warnings.Concat(history.Warnings).Distinct().ToArray();
        var warnings = restorationWarnings.Length == 0
            ? string.Empty
            : "\nWarning: " + string.Join(" ", restorationWarnings);
        var role = restorationWarnings.Length == 0 ? PresentationTextRole.Status : PresentationTextRole.Warning;
        var output = new List<PresentationItem>
        {
            new PresentationTextItem([new($"Threadsmith: Resumed session {history.SessionId.Value:D}.{warnings}\n", role)]),
        };
        foreach (var message in history.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = message.Content ?? "[Saved message content is no longer available.]";
            switch (message.Role)
            {
                case ConversationRole.User:
                    output.Add(new PresentationUserInputItem("You > ", content));
                    break;
                case ConversationRole.Assistant:
                    if (collector.Append(content) is { } source)
                    {
                        output.Add(source);
                    }

                    if (collector.Flush(cancellationToken) is { } answer)
                    {
                        output.Add(answer);
                    }

                    break;
            }
        }

        await _surface.Surface.PresentAsync(new PresentationBatch(output) { ReplaceOutput = true }, cancellationToken);
    }
}
