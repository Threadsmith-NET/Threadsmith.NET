namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Xunit;

public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Verifies structured execution descriptions are redacted before they enter durable and model-visible history.</summary>
    [Fact]
    public async Task ExecutionOutcome_RedactsSecretsAndRetainsValidJsonInHistory()
    {
        // Arrange
        const string secret = "execution-history-secret";
        await using var scenario = await ConversationScenario.CreateAsync(
            new ExecutionOutcomeTemplate(
                ExecutionCheckpointPhase.Completed,
                ["src/ShellRunner.cs"],
                [$"Checked Server=db;User Id=me;Password={secret};"],
                RollbackAvailable: true));

        // Act
        Assert.True(await scenario.CompletePlannedExecutionAsync());
        var snapshot = await scenario.GetReopenedSnapshotAsync();
        var runId = await scenario.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(scenario.SessionId, "undo that change"));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        // Assert
        var receipt = Assert.Single(snapshot.Messages, message => message.Role == ConversationRole.Assistant);
        var content = Assert.IsType<string>(receipt.Content);
        Assert.Equal(ConversationSensitivity.Sensitive, receipt.Sensitivity);
        Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
        var jsonStart = content.IndexOf('{');
        var jsonEnd = content.LastIndexOf('}');
        Assert.True(jsonStart >= 0 && jsonEnd > jsonStart);
        using var document = JsonDocument.Parse(content[jsonStart..(jsonEnd + 1)]);
        Assert.Equal("Completed", document.RootElement.GetProperty("Status").GetString());
        Assert.True(document.RootElement.GetProperty("RollbackAvailable").GetBoolean());
        var request = scenario.Model.SecondRequest
            ?? throw new InvalidOperationException("The next request did not reach the model.");
        Assert.DoesNotContain(secret, request.Input, StringComparison.Ordinal);
    }
}
