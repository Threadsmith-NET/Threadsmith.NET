namespace Threadsmith.CoreRuntime.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Commands;
using Threadsmith.Models;
using Xunit;

/// <summary>Checks the goal grammar and the live-session command boundary.</summary>
public static class SessionGoalCommandTests
{
    /// <summary>Exercises local session controls without allowing any model calls.</summary>
    [Fact]
    public static async Task CommandsReplaceAndCancelOnlyTheSelectedLiveGoal()
    {
        var token = TestContext.Current.CancellationToken;
        var prompts = new UnusedPrompts();
        await using var events = new DomainEventStream();
        var application = new SessionApplication(
            events,
            new UnusedModel(),
            new ExecutionBudget(new BudgetDimensions(100, 1, TimeSpan.MaxValue)),
            new DisplaySanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts);
        var dispatcher = new CommandDispatcher([application]);
        var first = await dispatcher.DispatchAsync(new CreateSessionCommand("first"), token);
        var other = await dispatcher.DispatchAsync(new CreateSessionCommand("other"), token);

        Task<SessionGoalResult> DispatchAsync(SessionId sessionId, string input)
        {
            Assert.True(InteractiveCommandRouter.TryParse(input, out var invocation));
            var command = GoalCommandParser.Parse(sessionId, invocation!.Argument);
            Assert.NotNull(command);
            return dispatcher.DispatchAsync(command, token);
        }

        Assert.Null((await DispatchAsync(first, "/goal")).DisplayText);
        await DispatchAsync(first, "/goal set \"Review the parser\"");
        await DispatchAsync(first, "/goal set \"Check C:\\src and \\\"quoted\\\" private text\"");
        application.RegisterRestoredSession(first, isWritable: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DispatchAsync(first, "/goal set \"overwrite\""));
        Assert.Equal("Check C:\\src and \"quoted\" [hidden] text", (await DispatchAsync(first, "/goal")).DisplayText);
        Assert.Null((await DispatchAsync(other, "/goal")).DisplayText);
        Assert.True((await DispatchAsync(first, "/goal cancel")).WasCancelled);
        Assert.False((await DispatchAsync(first, "/goal cancel")).WasCancelled);
        Assert.Null((await DispatchAsync(first, "/goal")).DisplayText);
    }

    /// <summary>Malformed controls cannot reach session state.</summary>
    [Theory]
    [InlineData("set unquoted")]
    [InlineData("set \" \"")]
    [InlineData("set \"not closed\\\"")]
    [InlineData("set \"one\" \"two\"")]
    [InlineData("cancel extra")]
    [InlineData("resume")]
    public static void InvalidArgumentsDoNotProduceACommand(string argument)
    {
        Assert.Null(GoalCommandParser.Parse(SessionId.New(), argument));
    }

    /// <summary>Escaped quotes count once against the decoded input limit.</summary>
    [Fact]
    public static void LengthLimitAppliesToDecodedTextWithoutTruncation()
    {
        var session = SessionId.New();
        var goal = new string('a', SessionGoalCommand.MaximumGoalLength - 1);
        Assert.Equal(goal + "\"", GoalCommandParser.Parse(session, $"set \"{goal}\\\"\"")?.Text);
        Assert.Null(GoalCommandParser.Parse(session, $"set \"{goal}\\\"x\""));
    }

    private sealed class UnusedModel : IModelProvider
    {
        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedPrompts : IPromptLoader
    {
        public string Get(string promptFileName) => throw new NotSupportedException();

        public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens) => throw new NotSupportedException();
    }

    private sealed class DisplaySanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value.Replace("private", "[hidden]", StringComparison.Ordinal);
    }
}
