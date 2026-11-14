namespace Threadsmith.Core;

/// <summary>Releases live execution resources while a nested operation waits for a person.</summary>
/// <remarks>Scopes follow one async execution branch. They are never persisted or restored.</remarks>
public sealed class UserInteractionScope : IDisposable
{
    private static readonly AsyncLocal<UserInteractionScope?> Current = new();
    private readonly UserInteractionScope? _parent;
    private readonly Action _pause;
    private readonly Func<CancellationToken, Task> _resume;

    /// <summary>Initializes a new instance of the <see cref="UserInteractionScope"/> class.</summary>
    public UserInteractionScope(Action pause, Func<CancellationToken, Task> resume)
    {
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(resume);
        _parent = Current.Value;
        _pause = pause;
        _resume = resume;
        Current.Value = this;
    }

    /// <summary>Waits with ancestor resources released, then re-enters from the outermost owner.</summary>
    public static async Task<T> WaitAsync<T>(Func<CancellationToken, Task<T>> wait, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wait);
        var paused = new Stack<UserInteractionScope>();
        var waitFailed = false;
        try
        {
            for (var scope = Current.Value; scope is not null; scope = scope._parent)
            {
                scope._pause();
                paused.Push(scope);
            }

            return await wait(cancellationToken);
        }
        catch
        {
            waitFailed = true;
            throw;
        }
        finally
        {
            // Cancellation never reacquires execution admission. Remaining scopes still need
            // their bookkeeping restored even when an earlier re-entry fails.
            Exception? failure = null;
            while (paused.TryPop(out var scope))
            {
                try
                {
                    await scope._resume(cancellationToken);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            if (failure is not null && !waitFailed)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Current.Value = _parent;
}
