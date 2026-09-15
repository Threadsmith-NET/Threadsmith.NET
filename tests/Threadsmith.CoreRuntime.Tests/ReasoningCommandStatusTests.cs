namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Checks immediate selection status independently of historical request accounting.</summary>
public static class ReasoningCommandStatusTests
{
    /// <summary>Accepted settings reach the next composer status without preparing another request.</summary>
    [Theory]
    [InlineData("high", "high", false)]
    [InlineData("unsupported", "medium", false)]
    [InlineData("high", "high", true)]
    public static async Task CommandUpdatesCurrentReasoningWithoutReplacingRequestUsage(string argument, string expected, bool changedModel)
    {
        var profile = new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "Current model",
            Provider = "openai-compatible",
            ProviderName = "Current provider",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "reasoning-model",
            ContextWindow = 128000,
            MaximumOutputTokens = 4096,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Medium, ReasoningLevel.High],
        };
        var previousProfile = profile with { Id = ModelProfileId.New(), Name = "Previous model", ProviderName = "Previous provider" };
        var preferences = new SessionModelPreferences(profile.Id, ReasoningLevel.Medium);
        var dispatcher = new SessionDispatcher();
        var usage = new SessionUsageProjection();
        var previousRequest = new AgentRequestStatus(changedModel ? previousProfile.Id : profile.Id, ReasoningLevel.Medium, 2500, 10000, 1);
        usage.ObserveRequest(dispatcher.SessionId, RunId.New(), previousRequest);
        await using var events = new DomainEventStream();
        var surface = new RecordingSurface([$"/reasoning {argument}", "/quit"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(dispatcher, new InMemoryProjectionStore()),
            events,
            surface,
            new ConfiguredModelCatalog([profile, previousProfile]),
            profile.Id,
            preferences,
            sessionUsage: usage);

        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, surface.Statuses.Count);
        Assert.Equal(ReasoningLevel.Medium, surface.Statuses[0].Reasoning);
        var current = surface.Statuses[1];
        Assert.Equal(new ReasoningLevel(expected), current.Reasoning);
        Assert.Equal(profile.Name, current.Model);
        Assert.Equal(profile.ProviderName, current.ProviderName);
        Assert.Same(previousRequest, current.AgentRequest);
        Assert.Equal(changedModel ? null : (long?)2500, current.ContextTokens);
        Assert.Equal(changedModel ? profile.ContextWindow : 10000, current.ContextLimit);
        Assert.Single(dispatcher.Commands);
        Assert.IsType<CreateSessionCommand>(dispatcher.Commands[0]);
        Assert.Same(previousRequest, usage.GetRequestStatus(dispatcher.SessionId));
    }

    private sealed class SessionDispatcher : ICommandDispatcher
    {
        public SessionId SessionId { get; } = SessionId.New();

        public List<object> Commands { get; } = [];

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            Assert.IsType<CreateSessionCommand>(command);
            return Task.FromResult((TResponse)(object)SessionId);
        }
    }

    private sealed class RecordingSurface : IInteractionSurface
    {
        private readonly Queue<string> _inputs;

        public RecordingSurface(IEnumerable<string> inputs)
        {
            _inputs = new Queue<string>(inputs);
        }

        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        public List<SessionStatusSnapshot> Statuses { get; } = [];

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new InteractionInput(true, _inputs.Dequeue(), CancellationToken.None));
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No selection is expected.");

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Statuses.Add(status);
            return Task.CompletedTask;
        }

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
            => operation.WaitAsync(cancellationToken);
    }
}
