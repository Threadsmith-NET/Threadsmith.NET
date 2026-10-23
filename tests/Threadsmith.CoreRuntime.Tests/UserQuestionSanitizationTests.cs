namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Synthetic credentials never enter newly retained clarification state or durable events.</summary>
public static class UserQuestionSanitizationTests
{
    private const string Sentinel = "synthetic-question-credential-90348";

    /// <summary>All terminal outcomes retain only sanitized content.</summary>
    [Theory]
    [InlineData(UserQuestionStatus.Answered)]
    [InlineData(UserQuestionStatus.Cancelled)]
    [InlineData(UserQuestionStatus.Interrupted)]
    public static async Task AdmissionTerminalSelectionAndConsumptionPublishOnlySanitizedContent(UserQuestionStatus status)
    {
        var token = TestContext.Current.CancellationToken;
        var database = Path.Combine(Path.GetTempPath(), "question-redaction-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new SqliteEventStore($"Data Source={database};Pooling=False");
            await store.InitializeAsync(token);
            await using var events = new DomainEventStream();
            var observed = new List<string>();
            await using var subscription = events.Subscribe(async (value, cancellationToken) =>
            {
                observed.Add(DomainEventJson.Serialize(value));
                await store.AppendAsync(value, cancellationToken);
            });
            var steering = new RunSteeringCoordinator();
            using var coordinator = new UserQuestionCoordinator(events, steering, sanitizer: new SecretOutputSanitizer());
            var request = Request();
            steering.RegisterRun(request.SessionId, request.RunId);
            UserQuestionRequest? shown = null;
            coordinator.Attach(async (value, cancellationToken) =>
            {
                shown = value;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Presentation should be cancelled after terminal selection.");
            });
            var admitted = await coordinator.AdmitAsync(request, token);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(admitted), StringComparison.Ordinal);
            Assert.Equal(request.RequestId, admitted.RequestId);
            Assert.Equal("q", admitted.Questions[0].Id);
            Assert.Equal("a", admitted.Questions[0].Options[0].Id);
            var result = new UserQuestionResult(status, status == UserQuestionStatus.Answered ? [new("q", null, "api_key=" + Sentinel)] : []);
            Assert.True(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, result, token));
            Assert.True(await coordinator.CompleteAsync(request.SessionId, request.RequestId, 0, result, token));
            var accepted = await coordinator.WaitAsync(request, token);
            Assert.NotNull(shown);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(shown), StringComparison.Ordinal);
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(accepted), StringComparison.Ordinal);
            if (status == UserQuestionStatus.Answered)
            {
                Assert.Equal(new SecretOutputSanitizer().Sanitize(result.Answers[0].Text!), accepted.Answers[0].Text);
            }

            await coordinator.ConsumeAsync(request, token);
            await coordinator.EndRunAsync(request.SessionId, request.RunId, token);
            Assert.All(observed, payload => Assert.DoesNotContain(Sentinel, payload, StringComparison.Ordinal));
            Assert.All(await store.ReadRowsAsync(request.SessionId, token), row => Assert.DoesNotContain(Sentinel, row.PayloadJson, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(database);
        }
    }

    /// <summary>Exercises the durable redaction boundary.</summary>
    [Fact]
    public static async Task RestoreSanitizesLegacyProjectionAndNewInterruptedRecord()
    {
        var token = TestContext.Current.CancellationToken;
        var database = Path.Combine(Path.GetTempPath(), "question-legacy-redaction-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var store = new SqliteEventStore($"Data Source={database};Pooling=False");
            await store.InitializeAsync(token);
            var request = Request();
            await store.AppendAsync(new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, new(UserQuestionStatus.Answered, [new("q", null, "password=" + Sentinel)])), token);
            var projection = new RecordingProjection();
            var restorer = new SessionRestorer(store, new DomainEventMigrationRegistry([], currentVersion: 1), NullLogger<SessionRestorer>.Instance, sanitizer: new SecretOutputSanitizer());
            var restored = await restorer.RestoreAsync(request.SessionId, projection, token);
            Assert.True(restored.Succeeded);
            Assert.DoesNotContain(Sentinel, restored.Warnings ?? string.Empty, StringComparison.Ordinal);
            Assert.All(projection.Payloads, payload => Assert.DoesNotContain(Sentinel, payload, StringComparison.Ordinal));
            var rows = await store.ReadRowsAsync(request.SessionId, token);
            Assert.Contains(Sentinel, rows[0].PayloadJson, StringComparison.Ordinal); // Existing history is not rewritten silently.
            Assert.DoesNotContain(Sentinel, rows[^1].PayloadJson, StringComparison.Ordinal);
            Assert.Contains("Interrupted", rows[^1].PayloadJson, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(database);
        }
    }

    /// <summary>Exercises the durable redaction boundary.</summary>
    [Fact]
    public static async Task FailedPersistenceStillReceivesOnlySanitizedAdmission()
    {
        await using var events = new DomainEventStream();
        var request = Request();
        var steering = new RunSteeringCoordinator();
        steering.RegisterRun(request.SessionId, request.RunId);
        using var coordinator = new UserQuestionCoordinator(events, steering);
        coordinator.Attach((_, _) => throw new InvalidOperationException("Must not present after failed persistence."));
        string? payload = null;
        await using var subscription = events.Subscribe((value, _) =>
        {
            payload = DomainEventJson.Serialize(value);
            throw new IOException("Synthetic persistence failure");
        });
        await Assert.ThrowsAnyAsync<Exception>(() => coordinator.AdmitAsync(request, TestContext.Current.CancellationToken));
        Assert.NotNull(payload);
        Assert.DoesNotContain(Sentinel, payload, StringComparison.Ordinal);
    }

    private static UserQuestionRequest Request() => new(Guid.NewGuid(), SessionId.New(), RunId.New(), ToolInvocationId.New(), "call", "repo", 0, [new("q", "Authorization: Bearer " + Sentinel, [new("a", "password='" + Sentinel + "'"), new("b", "Ordinary text")])], DateTimeOffset.UtcNow);

    private sealed class RecordingProjection : IProjectionStore
    {
        internal List<string> Payloads { get; } = [];

        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payloads.Add(DomainEventJson.Serialize(domainEvent));
            return Task.CompletedTask;
        }

        public Task<TProjection?> GetAsync<TProjection>(ProjectionKey key, CancellationToken cancellationToken = default)
            where TProjection : class, IProjection => Task.FromResult<TProjection?>(null);
    }
}
