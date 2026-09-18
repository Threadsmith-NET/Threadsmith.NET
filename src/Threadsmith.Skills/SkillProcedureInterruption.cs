namespace Threadsmith.Skills;

using Threadsmith.Core;

/// <summary>Attaches skill side-effect details to ordinary procedure exceptions.</summary>
internal static class SkillProcedureInterruption
{
    private const string SideEffectsKey = "Threadsmith.Skills.SideEffects";

    /// <summary>Attaches observed side effects to an exception before propagating it.</summary>
    public static void Attach(IReadOnlyList<SkillSideEffectRecord> sideEffects, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(sideEffects);
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[SideEffectsKey] = sideEffects.ToArray();
    }

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
        Attach(sideEffects, exception);
        return exception;
    }

    /// <summary>Returns side effects attached to a procedure exception.</summary>
    public static IReadOnlyList<SkillSideEffectRecord> GetSideEffects(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data[SideEffectsKey] is IReadOnlyList<SkillSideEffectRecord> sideEffects
            ? sideEffects
            : [];
    }
}
