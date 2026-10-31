namespace Threadsmith.Models;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Derives grants only from unchanged host-associated messages surviving final request admission.</summary>
public static class ReceiptEvidenceAuthorization
{
    /// <summary>Selects only typed, intact receipt entries visible to this session and repository.</summary>
    public static IReadOnlyList<ReceiptEvidenceGrant> Select(IReadOnlyList<ModelMessage>? messages, SessionId sessionId, string? repositoryPath)
    {
        if (messages is null || repositoryPath is null)
        {
            return [];
        }

        var repository = RepositoryIdentity.Create(repositoryPath);
        var grants = new List<ReceiptEvidenceGrant>();
        foreach (var message in messages)
        {
            if (message.Role != ModelMessageRole.Assistant || message.SectionId != "recent-assistant"
                || message.Receipt is not { Version: 1 } receipt || receipt.OriginSessionId != sessionId
                || receipt.Evidence.Count > 8 || message.ReceiptContentHash is null
                || message.GetModelVisibleContentLength() > 2048
                || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(message.GetModelVisibleContent()))) != message.ReceiptContentHash)
            {
                continue;
            }

            grants.AddRange(receipt.Evidence.Where(reference => reference.RepositoryIdentity == repository)
                .Select(reference => new ReceiptEvidenceGrant(receipt, reference)));
        }

        return grants.DistinctBy(grant => grant.Reference.EvidenceId).ToArray();
    }
}
