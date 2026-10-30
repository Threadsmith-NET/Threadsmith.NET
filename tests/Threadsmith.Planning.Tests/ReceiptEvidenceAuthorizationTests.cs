namespace Threadsmith.Planning.Tests;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Historical grants require typed, intact final-request receipt metadata.</summary>
public static class ReceiptEvidenceAuthorizationTests
{
    /// <summary>Copying prose or removing, rewriting, moving or cloning a receipt revokes its authority.</summary>
    [Fact]
    public static void FinalRequestOnlyAuthorizesIntactOwnedReceipts()
    {
        var session = SessionId.New();
        var repository = Environment.CurrentDirectory;
        var reference = new ReceiptEvidenceReference(EvidenceId.New(), ToolInvocationId.New(), RepositoryIdentity.Create(repository), "hash", 10, 1, "Build", false);
        var receipt = new ConversationReceiptProvenance(1, session, RunId.New(), ConversationMessageId.New()) { Evidence = [reference] };
        var body = $"Historical evidence: {reference.EvidenceId.Value:D}";
        var message = new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "recent-assistant",
            Content = [new() { Content = body }],
            Receipt = receipt,
            ReceiptContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
        };
        Assert.Single(ReceiptEvidenceAuthorization.Select([message], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message with { Receipt = null }], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message with { Content = [new() { Content = "summary" }] }], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message with { Role = ModelMessageRole.User }], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message with { SectionId = "memory" }], session, repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message], SessionId.New(), repository));
        Assert.Empty(ReceiptEvidenceAuthorization.Select([message], session, Path.Combine(repository, "other")));
    }

    /// <summary>Invalid grants are denied before consulting durable storage, including child and repository boundaries.</summary>
    [Theory]
    [InlineData("arbitrary")]
    [InlineData("child")]
    [InlineData("repository")]
    [InlineData("session")]
    [InlineData("prohibited")]
    [InlineData("roots")]
    public static async Task ToolRejectsUnauthorizedHistoricalReadsBeforeLookup(string mismatch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var session = SessionId.New();
        var reference = new ReceiptEvidenceReference(EvidenceId.New(), null, RepositoryIdentity.Create(Environment.CurrentDirectory), "hash", 10, 1, "Build", false);
        var receipt = new ConversationReceiptProvenance(1, session, RunId.New(), ConversationMessageId.New()) { Evidence = [reference] };
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = mismatch == "repository" ? Path.Combine(Environment.CurrentDirectory, "other") : Environment.CurrentDirectory,
            RequestedBy = "model",
            IsChildAgent = mismatch == "child",
            ProhibitedPaths = mismatch == "prohibited" ? ["private.cs"] : [],
            ApprovedRoots = mismatch == "roots" ? ["allowed"] : ["."],
            ReceiptEvidenceReferences = [new(receipt, reference)],
        };
        var tool = new ActiveTurnEvidenceTool(new EvidenceStore(events, new SecretOutputSanitizer()), TestPromptLoader.Instance, new UnexpectedStore());
        var context = new ToolExecutionContext(ToolInvocationId.New(), mismatch == "session" ? SessionId.New() : session, RunId.New(), invocation);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new ActiveTurnEvidenceInput(mismatch == "arbitrary" ? Guid.NewGuid() : reference.EvidenceId.Value), context, ct));
    }

    private sealed class UnexpectedStore : IReceiptEvidenceStore
    {
        public Task<IReadOnlyList<ReceiptEvidenceReference>> CaptureAsync(ConversationReceiptProvenance receipt, IReadOnlyList<ReceiptEvidenceCapture> captures, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unauthorized capture.");

        public Task<TextEvidenceReadResult?> ReadReceiptEvidenceAsync(SessionId sessionId, ReceiptEvidenceGrant grant, int startLine, int? endLine, int startColumn, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unauthorized storage lookup.");
    }
}
