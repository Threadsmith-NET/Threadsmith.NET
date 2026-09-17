namespace Threadsmith.Skills;

using Threadsmith.Core;

/// <summary>Attaches skill side-effect details to ordinary cancellation exceptions.</summary>
internal static class SkillProcedureInterruption
{
    private const string SideEffectsKey = "Threadsmith.Skills.SideEffects";

    /// <summary>Creates a cancellation exception that carries observed side effects.</summary>
    public static OperationCanceledException Create(
        IReadOnlyList<SkillSideEffectRecord> sideEffects,
        OperationCanceledException innerException)
    {
        ArgumentNullException.ThrowIfNull(sideEffects);
        ArgumentNullException.ThrowIfNull(innerException);
        var exception = new OperationCanceledException(
            "Skill procedure was interrupted after producing side effects.",
            innerException,
            innerException.CancellationToken);
        exception.Data[SideEffectsKey] = sideEffects.ToArray();
        return exception;
    }

    /// <summary>Returns side effects attached to an interruption exception.</summary>
    public static IReadOnlyList<SkillSideEffectRecord> GetSideEffects(OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[SideEffectsKey] is IReadOnlyList<SkillSideEffectRecord> sideEffects
            ? sideEffects
            : [];
    }
}
