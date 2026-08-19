namespace Threadsmith.Workspaces;

/// <summary>Identifies deterministic filesystem-effect boundaries used by transaction fault verification.</summary>
internal enum MutationTransactionPoint
{
    /// <summary>Immediately before writing a private temporary file.</summary>
    BeforeTemporaryWrite,

    /// <summary>Immediately after writing a private temporary file.</summary>
    AfterTemporaryWrite,

    /// <summary>Immediately before removing a baseline identity.</summary>
    BeforeBaselineRemoval,

    /// <summary>Immediately after removing a baseline identity.</summary>
    AfterBaselineRemoval,

    /// <summary>Immediately before publishing a final identity.</summary>
    BeforeFinalPublication,

    /// <summary>Immediately after publishing a final identity.</summary>
    AfterFinalPublication,

    /// <summary>Immediately before verifying a final identity.</summary>
    BeforeFinalVerification,

    /// <summary>Immediately after verifying a final identity.</summary>
    AfterFinalVerification,

    /// <summary>Immediately before compensation removes a created identity.</summary>
    BeforeCompensationRemoval,

    /// <summary>Immediately after compensation removes a created identity.</summary>
    AfterCompensationRemoval,

    /// <summary>Immediately before compensation restores a baseline identity.</summary>
    BeforeCompensationRestore,

    /// <summary>Immediately after compensation restores a baseline identity.</summary>
    AfterCompensationRestore,
}

/// <summary>Observes deterministic filesystem-effect boundaries for internal fault verification.</summary>
internal interface IMutationTransactionObserver
{
    /// <summary>Observes one transaction boundary.</summary>
    Task ObserveAsync(
        MutationTransactionPoint point,
        string relativePath,
        CancellationToken cancellationToken);
}

/// <summary>Provides the production no-op transaction observer.</summary>
internal sealed class NullMutationTransactionObserver : IMutationTransactionObserver
{
    /// <summary>Gets the shared no-op observer.</summary>
    public static NullMutationTransactionObserver Instance { get; } = new();

    /// <inheritdoc />
    public Task ObserveAsync(
        MutationTransactionPoint point,
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
