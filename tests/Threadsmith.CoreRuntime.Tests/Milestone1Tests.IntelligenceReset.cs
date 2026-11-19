namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit.Terminal;
using Xunit;

/// <summary>Reset must cross the existing interactive selector before host mutation.</summary>
public static partial class Milestone1Tests
{
    /// <summary>The actual TUIKit selector defaults to No and maps keyboard Yes and Escape correctly.</summary>
    [Theory]
    [InlineData("\r", 0)]
    [InlineData("\u001b[B\r", 1)]
    [InlineData("\u001b[27u", -1)]
    public static async Task IntelligenceReset_TuiKitSelectorHandlesYesNoAndEscape(string keys, int expected)
    {
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async token =>
            {
                var selection = new InteractionSessionSurface(surface).SelectAsync("Are you sure?", ["No", "Yes"], token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                backend.FeedInput(keys);
                Assert.Equal(expected, await selection);
            },
            TestContext.Current.CancellationToken);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Headless callers cannot bypass the required selector with a destructive slash command.</summary>
    [Fact]
    public static async Task IntelligenceReset_HeadlessRequiresInteractiveConfirmation()
    {
        var dispatcher = new IntelligenceResetDispatcher();
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(dispatcher, new InMemoryProjectionStore(), writer);
        var result = await shell.RunRepositoryRequestAsync("test", "C:\\repo", RepositoryTrustLevel.TrustedRead, null, "/intelligence reset", TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("interactive terminal (--tui)", writer.ToString(), StringComparison.Ordinal);
        Assert.Empty(dispatcher.Resets);
        Assert.False(dispatcher.Inner.SubmitRequestObserved);
    }

    private sealed class IntelligenceResetDispatcher : ICommandDispatcher
    {
        private readonly IDomainEventStream? _events;

        public IntelligenceResetDispatcher(IDomainEventStream? events = null)
        {
            _events = events;
        }

        public SessionId SessionId { get; } = SessionId.New();

        public SemanticUnavailableRepositoryDispatcher Inner { get; } = new(SessionId.New(), WorkspaceId.New());

        public List<SubmitRequestCommand> Submissions { get; } = [];

        public List<ManageRepositoryIntelligenceCommand> Resets { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command is WaitForRunCommand wait && _events is not null)
            {
                return CompleteAsync<TResponse>(wait.RunId, cancellationToken);
            }

            if (command is CreateSessionCommand)
            {
                return Task.FromResult((TResponse)(object)SessionId);
            }

            if (command is SubmitRequestCommand submit)
            {
                Submissions.Add(submit);
                return Task.FromResult((TResponse)(object)RunId.New());
            }

            if (command is GetRepositoryInitializationStatusCommand initialization)
            {
                return Task.FromResult((TResponse)(object)new RepositoryInitializationStatus(initialization.RepositoryPath, true, false));
            }

            if (command is GetRepositoryTrustCommand trust)
            {
                return Task.FromResult((TResponse)(object)new RepositoryTrustState(trust.RepositoryPath, RepositoryTrustLevel.UntrustedInspection, DateTimeOffset.UtcNow));
            }

            if (command is ManageRepositoryIntelligenceCommand reset)
            {
                Resets.Add(reset);
                return Task.FromResult((TResponse)(object)new RepositoryIntelligenceStatus { RepositoryIdentity = RepositoryIdentity.Create(reset.RepositoryPath) });
            }

            return Inner.DispatchAsync(command, cancellationToken);
        }

        private async Task<TResponse> CompleteAsync<TResponse>(RunId runId, CancellationToken cancellationToken)
        {
            await _events!.PublishAsync(new RunCompleted(SessionId, DateTimeOffset.UtcNow, runId, true), cancellationToken);
            return (TResponse)(object)true;
        }
    }
}
