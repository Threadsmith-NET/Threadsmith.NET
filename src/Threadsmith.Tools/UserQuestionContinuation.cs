namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Shares the wait, authority revalidation and original tool completion handoff.</summary>
public static class UserQuestionContinuation
{
    /// <summary>Completes a suspended question; the history owner must append, then consume it.</summary>
    public static async Task<ToolInvocationResult> CompleteAsync(
        IToolInvocationPipeline pipeline,
        IUserQuestionCoordinator questions,
        ToolInvocationRequest request,
        ToolInvocationResult suspended,
        Func<CancellationToken, Task<ToolInvocationContext?>> currentContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(suspended);
        ArgumentNullException.ThrowIfNull(currentContext);
        var question = suspended.Suspension ?? throw new InvalidOperationException("The tool did not suspend for clarification.");
        try
        {
            var answer = await UserInteractionScope.WaitAsync(token => questions.WaitAsync(question, token), cancellationToken);
            if (request.Context.SecurityPolicy is { } captured)
            {
                request.Context.PolicyBinding?.Validate(captured);
            }

            var current = await currentContext(cancellationToken);
            if (current is null || RepositoryIdentity.Create(current.RepositoryPath) != question.RepositoryIdentity
                || (current.SecurityPolicy?.Generation ?? 0) != question.PolicyGeneration)
            {
                throw new InvalidOperationException("The repository or policy changed while awaiting clarification.");
            }

            return await pipeline.CompleteSuspensionAsync(request, suspended, answer, cancellationToken);
        }
        catch
        {
            // Preserve the original invocation's one terminal publication attempt.
            try
            {
                await pipeline.CompleteSuspensionAsync(request, suspended, new(UserQuestionStatus.Interrupted, []), CancellationToken.None);
            }
            finally
            {
                await questions.InterruptAsync(question, CancellationToken.None);
            }

            throw;
        }
    }
}
