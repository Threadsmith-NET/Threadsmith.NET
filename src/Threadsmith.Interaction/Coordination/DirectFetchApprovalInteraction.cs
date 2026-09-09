namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Tools;

/// <summary>Frontend-neutral presentation and closed option mapping for direct web approvals.</summary>
internal static class DirectFetchApprovalInteraction
{
    /// <summary>Presents explicit approval durations and maps only known option identities.</summary>
    internal static async Task<DirectFetchApprovalOutcome> RequestAsync(
        InteractionSessionSurface surface,
        DirectFetchApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(request);
        var path = string.IsNullOrEmpty(request.Path) ? "/" : request.Path;
        var query = request.QueryPresent ? "present (values hidden)" : "absent";
        var decision = await surface.SelectAsync(
            "The model proposed a public web destination requiring approval. "
            + $"Origin: {request.Origin}; path: {path}; query: {query}; exact digest: {request.UrlDigest}. "
            + "Approve one attempt covers only this URL and invocation. Approve for this session covers public HTTPS pages on this exact hostname until the session changes. "
            + "Add to user allowed list saves the hostname in your user configuration for future sessions. Hostname approvals exclude subdomains and cross-origin redirects.",
            ["Deny", "Approve one attempt", "Approve for this session", "Add to user allowed list"],
            cancellationToken);
        return decision switch
        {
            1 => DirectFetchApprovalOutcome.Approved,
            2 => DirectFetchApprovalOutcome.ApprovedForSession,
            3 => DirectFetchApprovalOutcome.ApprovedForUser,
            _ => DirectFetchApprovalOutcome.Denied,
        };
    }
}
