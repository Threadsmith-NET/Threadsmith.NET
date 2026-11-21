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

    /// <summary>The combined replay boundary preserves input validation and validates expanded sanitized text.</summary>
    [Theory]
    [InlineData("original-prompt")]
    [InlineData("original-answer")]
    [InlineData("expanded-prompt")]
    [InlineData("expanded-answer")]
    [InlineData("identity")]
    [InlineData("outcome")]
    public static void ReplayRejectsInvalidOriginalAndSanitizedPayloads(string invalid)
    {
        var request = Request() with { Questions = [new("q", "Ordinary prompt", [])] };
        var result = new UserQuestionResult(UserQuestionStatus.Answered, [new("q", null, "Ordinary answer")]);
        IOutputSanitizer sanitizer = new SecretOutputSanitizer();
        if (invalid == "original-prompt")
        {
            request = request with { Questions = [new("q", new string('x', 2001), [])] };
        }
        else if (invalid == "original-answer")
        {
            result = result with { Answers = [new("q", null, new string('x', 8001))] };
        }
        else if (invalid is "expanded-prompt" or "expanded-answer")
        {
            sanitizer = new ExpandingSanitizer(invalid == "expanded-prompt" ? "Ordinary prompt" : "Ordinary answer");
        }
        else if (invalid == "identity")
        {
            request = request with { Questions = [new("password=" + Sentinel, "Ordinary prompt", [])] };
            result = new(UserQuestionStatus.Pending, []);
        }
        else
        {
            result = result with { Status = UserQuestionStatus.Cancelled };
        }

        var state = new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, result);
        Assert.Throws<ArgumentException>(() => UserQuestionSanitizer.Sanitize(state, sanitizer));
    }

    /// <summary>Unchanged sanitized text still receives detached lists at the replay trust boundary.</summary>
    [Fact]
    public static void ReplayDetachesValidatedCollections()
    {
        var request = Request() with { Questions = [new("q", "Ordinary prompt", [new("a", "First"), new("b", "Second")])] };
        var result = new UserQuestionResult(UserQuestionStatus.Answered, [new("q", "a", null)]);
        var state = new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, result);
        var safe = UserQuestionSanitizer.Sanitize(state, new SecretOutputSanitizer());
        Assert.NotSame(request.Questions, safe.Request.Questions);
        Assert.NotSame(request.Questions[0].Options, safe.Request.Questions[0].Options);
        Assert.NotSame(result.Answers, safe.Result.Answers);
        Assert.Equal(JsonSerializer.Serialize(state), JsonSerializer.Serialize(safe));
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

    private sealed class ExpandingSanitizer : IOutputSanitizer
    {
        private readonly string _target;

        internal ExpandingSanitizer(string target) => _target = target;

        public string Sanitize(string value) => value == _target ? new string('x', 8001) : value;
    }
}
