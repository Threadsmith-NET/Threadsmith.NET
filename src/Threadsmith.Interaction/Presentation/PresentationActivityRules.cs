namespace Threadsmith.Interaction.Presentation;

using Threadsmith.Core;

/// <summary>Classifies event boundaries that end transient interaction activity.</summary>
internal static class PresentationActivityRules
{
    /// <summary>Returns whether an event ends the currently displayed transient activity.</summary>
    internal static bool EndsTransientActivity(IDomainEvent domainEvent, bool emittedModelOutput)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return domainEvent is ToolInvocationCompleted
            or SemanticCheckCompleted
            or PlanProposed
            or MutationSetProposed
            or RunSteeringPaused
            or RunCompleted
            || (domainEvent is ModelOutputObserved && emittedModelOutput);
    }
}
