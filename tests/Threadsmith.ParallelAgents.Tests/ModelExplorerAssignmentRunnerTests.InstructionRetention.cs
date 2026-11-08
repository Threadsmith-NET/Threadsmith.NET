namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Large concurrent prepared and fallback observations have a fixed durable body bound.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeConcurrentInstructionsHaveBoundedSerializedBodies(bool prepared)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var body = new string('界', AgentRunInstructionsObserved.MaximumRetainedCharacters * 4);
        var assignments = Enumerable.Range(0, 4).Select(_ => CreateAssignment(CreateProfile().Id, []) with { InitialContext = body }).ToArray();
        var plan = CreatePlan(assignments[0]) with { Assignments = assignments };
        var observed = new ConcurrentQueue<AgentRunInstructionsObserved>();
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is AgentRunInstructionsObserved instructions)
            {
                observed.Enqueue(instructions);
            }

            return Task.CompletedTask;
        });
        using var observation = new ChildAgentInstructionObservation(plan, events, new SecretOutputSanitizer());
        var requests = assignments.Select(assignment => new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = assignment.Objective,
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "child-assignment", Content = [new ModelContentPart { Content = body }] }],
        }).ToArray();

        // Act
        await Task.WhenAll(assignments.Select((assignment, index) => prepared
            ? ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, requests[index], ct)
            : observation.ObserveAssignmentAsync(assignment.AssignmentId, ct)));
        await Task.WhenAll(assignments.Select(assignment => observation.ObserveAssignmentAsync(assignment.AssignmentId, ct)));

        // Assert: worst-case JSON escaping remains bounded independently of original context size.
        Assert.Equal(assignments.Length, observed.Count);
        foreach (var instructions in observed)
        {
            Assert.True(instructions.IsTruncated);
            Assert.InRange(instructions.Sections.Count, 1, AgentRunInstructionsObserved.MaximumRetainedSections);
            Assert.InRange(instructions.Sections.Sum(section => section.Label.Length + section.Text.Length), 1, AgentRunInstructionsObserved.MaximumRetainedCharacters);
            var json = DomainEventJson.Serialize(instructions);
            Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, (6 * AgentRunInstructionsObserved.MaximumRetainedCharacters) + 4096);
            var restored = Assert.IsType<AgentRunInstructionsObserved>(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(instructions), instructions.SchemaVersion, json));
            Assert.True(restored.IsTruncated);
            Assert.Equal(instructions.Sections, restored.Sections);
        }

        Assert.All(assignments, assignment => Assert.Equal(body, assignment.InitialContext));
        Assert.All(requests, request => Assert.Equal(body, request.Messages[0].GetModelVisibleContent()));
    }

    /// <summary>Exact boundaries preserve complete text and report only real omissions.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PreparedInstructionRetentionReportsExactBoundary(int extraCharacters)
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new string('x', AgentRunInstructionsObserved.MaximumRetainedCharacters - "Assignment".Length + extraCharacters);
        var assignment = CreateAssignment(CreateProfile().Id, []);
        var plan = CreatePlan(assignment);
        var observed = new ConcurrentQueue<AgentRunInstructionsObserved>();
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is AgentRunInstructionsObserved instructions)
            {
                observed.Enqueue(instructions);
            }

            return Task.CompletedTask;
        });
        using var observation = new ChildAgentInstructionObservation(plan, events, new SecretOutputSanitizer());
        var request = new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = assignment.Objective,
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "child-assignment", Content = [new ModelContentPart { Content = body }] }],
        };

        await ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, request, ct);

        var instructions = Assert.Single(observed);
        Assert.Equal(extraCharacters > 0, instructions.IsTruncated);
        Assert.Equal(body[..Math.Min(body.Length, AgentRunInstructionsObserved.MaximumRetainedCharacters - "Assignment".Length)], Assert.Single(instructions.Sections).Text);
    }

    /// <summary>Retention neither splits Unicode pairs nor retains a credential prefix at the cut.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedInstructionRetentionPreservesUnicodeAndRedactsBeforeCut(bool credential)
    {
        var ct = TestContext.Current.CancellationToken;
        var available = AgentRunInstructionsObserved.MaximumRetainedCharacters - "Assignment".Length;
        var prefix = new string('x', available - (credential ? 20 : 1));
        var body = credential
            ? prefix + "\npassword=boundary-secret-that-must-not-survive\n" + new string('y', available)
            : prefix + "😀" + new string('y', available);
        var assignment = CreateAssignment(CreateProfile().Id, []);
        var plan = CreatePlan(assignment);
        var observed = new ConcurrentQueue<AgentRunInstructionsObserved>();
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is AgentRunInstructionsObserved instructions)
            {
                observed.Enqueue(instructions);
            }

            return Task.CompletedTask;
        });
        using var observation = new ChildAgentInstructionObservation(plan, events, new SecretOutputSanitizer());
        var request = new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = assignment.Objective,
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "child-assignment", Content = [new ModelContentPart { Content = body }] }],
        };

        await ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, request, ct);

        var instructions = Assert.Single(observed);
        Assert.True(instructions.IsTruncated);
        var retained = Assert.Single(instructions.Sections).Text;
        Assert.False(char.IsHighSurrogate(retained[^1]));
        Assert.DoesNotContain("boundary-secret", retained, StringComparison.Ordinal);
        Assert.Equal(credential ? prefix + "\npassword=[REDACTED]" : prefix, retained);
        Assert.Equal(body, request.Messages[0].GetModelVisibleContent());
    }

    /// <summary>Many tiny recognized sections cannot create an unbounded event envelope.</summary>
    [Fact]
    public async Task PreparedInstructionRetentionBoundsSectionCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var assignment = CreateAssignment(CreateProfile().Id, []);
        var plan = CreatePlan(assignment);
        var observed = new ConcurrentQueue<AgentRunInstructionsObserved>();
        await using var events = new DomainEventStream();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is AgentRunInstructionsObserved instructions)
            {
                observed.Enqueue(instructions);
            }

            return Task.CompletedTask;
        });
        using var observation = new ChildAgentInstructionObservation(plan, events, new SecretOutputSanitizer());
        var request = new ModelStreamRequest
        {
            RunId = assignment.ChildRunId,
            Input = assignment.Objective,
            Messages = Enumerable.Range(0, 20).Select(index => new ModelMessage
            {
                Role = ModelMessageRole.User,
                SectionId = "child-assignment",
                Content = [new ModelContentPart { Content = index.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            }).ToArray(),
        };

        await ChildAgentInstructionObservation.ObservePreparedAsync(plan, assignment, request, ct);

        var instructions = Assert.Single(observed);
        Assert.True(instructions.IsTruncated);
        Assert.Equal(AgentRunInstructionsObserved.MaximumRetainedSections, instructions.Sections.Count);
        Assert.Equal(request.Messages.Take(AgentRunInstructionsObserved.MaximumRetainedSections).Select(message => message.GetModelVisibleContent()), instructions.Sections.Select(section => section.Text));
    }
}
