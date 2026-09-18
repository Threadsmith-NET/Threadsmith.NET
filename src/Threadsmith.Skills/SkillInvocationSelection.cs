namespace Threadsmith.Skills;

using Threadsmith.Core;

/// <summary>Resolves invocation selectors and existing pins identically for inspection and execution.</summary>
internal static class SkillInvocationSelection
{
    /// <summary>Resolves an exact candidate without executing or authorizing the skill.</summary>
    internal static async Task<SkillCatalogCandidate> ResolveAsync(
        ISkillCatalog catalog,
        ISkillStateStore state,
        string selector,
        CancellationToken cancellationToken)
    {
        if (selector.IndexOfAny([':', '@', '+']) < 0)
        {
            var pin = await state.GetPinAsync(new SkillId(selector), cancellationToken);
            if (pin is not null)
            {
                var matches = catalog.Snapshot.Candidates.Where(item => item.Identity == pin).ToArray();
                return matches.Length switch
                {
                    0 => throw new KeyNotFoundException("The pinned skill package is no longer installed."),
                    1 => matches[0],
                    _ => throw new InvalidOperationException(
                        "The pinned skill package exists in multiple scopes; invoke a scope-qualified selector."),
                };
            }
        }

        return catalog is IAsyncSkillCatalog asynchronous
            ? await asynchronous.ResolveAsync(selector, cancellationToken)
            : catalog.Resolve(selector);
    }
}
