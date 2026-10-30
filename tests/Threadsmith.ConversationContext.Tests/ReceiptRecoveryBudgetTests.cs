namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Conditional historical recovery participates in ordinary whole-turn admission.</summary>
public static class ReceiptRecoveryBudgetTests
{
    /// <summary>Near capacity, schema cost removes the whole historical turn and its grants without failing admission.</summary>
    [Theory]
    [InlineData(ToolTransportMode.Native)]
    [InlineData(ToolTransportMode.Text)]
    public static async Task ReceiptSchemaCostReducesHistoryBeforeDispatch(ToolTransportMode transport)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var session = SessionId.New();
        var prior = RunId.New();
        var id = ConversationMessageId.New();
        var reference = new ReceiptEvidenceReference(EvidenceId.New(), null, RepositoryIdentity.Create(fixture.DirectoryPath), "hash", 1, 1, "Build", false);
        var provenance = new ConversationReceiptProvenance(1, session, prior, id) { Evidence = [reference] };
        await ArchiveAsync(prior, ConversationMessageId.New(), ConversationRole.User, "Build the project.");
        await ArchiveAsync(prior, id, ConversationRole.Assistant, $"Historical build receipt {reference.EvidenceId.Value:D}", provenance);
        var current = await ArchiveAsync(RunId.New(), ConversationMessageId.New(), ConversationRole.User, "Explain the previous result.");
        var request = new ContextAssemblyRequest
        {
            SessionId = session,
            RunId = current.RunId,
            RepositoryPath = fixture.DirectoryPath,
            Task = new TaskSpecification(current.Content!, []),
            CurrentMessageId = current.Id,
            ToolTransportMode = transport,
        };
        var baseline = await CreateAssembler(32_000).AssembleAsync(request, ct);
        Assert.Single(ReceiptEvidenceAuthorization.Select(baseline.Messages, session, fixture.DirectoryPath));
        var withRecovery = request with { ReceiptRecoveryTool = new ContextToolSchema("read_active_turn_evidence", "Read captured evidence by ID.", "{\"type\":\"object\",\"properties\":{\"evidenceId\":{\"type\":\"string\"}}}") };
        var admitted = await CreateAssembler(32_000).AssembleAsync(withRecovery, ct);
        Assert.True(admitted.WireEstimate!.WireInputTokens > baseline.WireEstimate!.WireInputTokens);
        var reduced = await CreateAssembler(baseline.WireEstimate.WireInputTokens).AssembleAsync(withRecovery, ct);
        Assert.True(reduced.WireEstimate!.WireInputTokens <= baseline.WireEstimate.WireInputTokens);
        Assert.Empty(ReceiptEvidenceAuthorization.Select(reduced.Messages, session, fixture.DirectoryPath));
        Assert.DoesNotContain("read_active_turn_evidence", reduced.ModelInput, StringComparison.Ordinal);
        var withoutHistory = await CreateAssembler(32_000).AssembleAsync(request with { ConversationModeOverride = ConversationContextMode.Stateless }, ct);
        Assert.Equal(withoutHistory.ToolInventoryDigest, reduced.ToolInventoryDigest);

        ContextAssembler CreateAssembler(int budget)
        {
            var sanitizer = new SecretOutputSanitizer();
            return new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, new ContextAssemblerOptions { MaximumTokens = budget }, conversationStore: fixture.Store);
        }

        Task<ConversationMessage> ArchiveAsync(RunId run, ConversationMessageId message, ConversationRole role, string content, ConversationReceiptProvenance? receipt = null)
            => fixture.Store.ArchiveMessageAsync(
                new ConversationMessage
                {
                    Id = message,
                    SessionId = session,
                    RunId = run,
                    Sequence = 0,
                    Role = role,
                    Content = content,
                    ContentHash = "pending",
                    EstimatedTokens = 1,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Receipt = receipt,
                },
                ct);
    }
}
