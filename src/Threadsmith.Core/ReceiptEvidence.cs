namespace Threadsmith.Core;

/// <summary>Immutable historical observation associated with one original conversation receipt.</summary>
public sealed record ReceiptEvidenceReference(
    EvidenceId EvidenceId,
    ToolInvocationId? InvocationId,
    string RepositoryIdentity,
    string ContentHash,
    int ByteLength,
    int TotalLines,
    string Label,
    bool WasTruncated);

/// <summary>Original receipt ownership, carried only by the host through final request admission.</summary>
public sealed record ReceiptEvidenceGrant(ConversationReceiptProvenance Receipt, ReceiptEvidenceReference Reference);

/// <summary>A bounded sanitized observation eligible for durable receipt recovery.</summary>
public sealed record ReceiptEvidenceCapture(EvidenceId EvidenceId, ToolInvocationId? InvocationId, string RepositoryIdentity, string Label, string Content, bool WasTruncated);

/// <summary>Historical text paging through the existing artifact owner; identities never confer authorization by themselves.</summary>
public interface IReceiptEvidenceStore
{
    /// <summary>Persists eligible bounded observations before their receipt is archived.</summary>
    Task<IReadOnlyList<ReceiptEvidenceReference>> CaptureAsync(ConversationReceiptProvenance receipt, IReadOnlyList<ReceiptEvidenceCapture> captures, CancellationToken cancellationToken = default);

    /// <summary>Reads a page after checking original session, repository, receipt and invocation ownership.</summary>
    Task<TextEvidenceReadResult?> ReadReceiptEvidenceAsync(SessionId sessionId, ReceiptEvidenceGrant grant, int startLine, int? endLine, int startColumn, CancellationToken cancellationToken = default);
}
