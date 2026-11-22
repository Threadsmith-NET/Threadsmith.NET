namespace Threadsmith.Extensions.Neo4j;

using Threadsmith.Extensions.Abstractions;

/// <summary>The single invocation lifecycle shared by both Neo4j tools.</summary>
/// <remarks>
/// The credential is resolved only after the host admits the invocation, the session is created and
/// disposed inside that invocation, and every database failure is translated before it leaves the
/// extension. Cancellation propagates so the host owns the interruption receipt.
/// </remarks>
internal static class Neo4jExecution
{
    /// <summary>Resolves the configured credential and runs one bounded operation.</summary>
    /// <typeparam name="TResult">The caller's result shape.</typeparam>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="sessions">The invocation-scoped session factory.</param>
    /// <param name="context">The host invocation context.</param>
    /// <param name="operation">The bounded operation to run against the session.</param>
    /// <param name="onFailure">Builds the result when no session could be opened.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The host-owned tool result.</returns>
    internal static async Task<TResult> RunAsync<TResult>(
        Neo4jSettings settings,
        IGraphSessionFactory sessions,
        ExtensionToolInvocationContext context,
        Func<IGraphSession, Task<TResult>> operation,
        Func<string, string, TResult> onFailure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onFailure);
        if (context.Secrets is not { } secrets)
        {
            return onFailure(
                GraphFailureKind.CredentialUnavailable,
                "Secret access was not admitted for this invocation.");
        }

        ExtensionSecretValue credential;
        try
        {
            credential = await secrets.ResolveAsync(settings.PasswordReference, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // The resolver failure text is host-owned and is not echoed to the model.
        catch (Exception)
#pragma warning restore CA1031
        {
            return onFailure(
                GraphFailureKind.CredentialUnavailable,
                "The configured password reference could not be resolved.");
        }

        var session = sessions.Create(settings, credential.Reveal());
        await using (session.ConfigureAwait(false))
        {
            return await operation(session);
        }
    }
}
