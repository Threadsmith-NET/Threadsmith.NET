namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>A bounded group of related blocking clarifications.</summary>
public sealed record AskUserInput(IReadOnlyList<UserQuestion> Questions);

/// <summary>Registers clarification and returns promptly to release tool resources.</summary>
public sealed class AskUserTool : Tool<AskUserInput, UserQuestionResult>
{
    /// <summary>Initializes a new instance of the <see cref="AskUserTool"/> class.</summary>
    public AskUserTool(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        Definition = ToolDefinitionFactory.Create<AskUserInput, UserQuestionResult>("ask_user", () => prompts.Get(PromptFileNames.ToolAskUserDescription), ToolCategory.Workflow, RepositoryTrustLevel.UntrustedInspection, ApprovalLevel.None, ToolSideEffect.ReadOnly, TimeSpan.FromSeconds(10), 32 * 1024) with
        {
            DisplayName = "Ask user",
            SubagentAvailable = false,
            EnabledByDefault = false,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<UserQuestionResult>> ExecuteAsync(AskUserInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (context.Invocation.IsChildAgent || context.Invocation.UserQuestions is not { IsAvailable: true } questions)
        {
            throw new ToolExecutionException("Interactive clarification is unavailable for this caller.", ToolErrorClassification.PolicyDenied);
        }

        var request = await questions.AdmitAsync(new UserQuestionRequest(Guid.NewGuid(), context.SessionId, context.RunId, context.ToolInvocationId, context.InvocationKey ?? context.ToolInvocationId.Value.ToString("D"), RepositoryIdentity.Create(context.Invocation.RepositoryPath), context.Invocation.SecurityPolicy?.Generation ?? 0, input.Questions, DateTimeOffset.UtcNow), cancellationToken);
        return new ToolExecution<UserQuestionResult>(new(UserQuestionStatus.Pending, []), [], Suspension: request);
    }

    /// <inheritdoc />
    protected override void ValidateInput(AskUserInput input) => UserQuestionValidation.ValidateQuestions(input.Questions);
}
