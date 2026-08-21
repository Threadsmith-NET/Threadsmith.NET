namespace Threadsmith.Tools;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Host-owned progressive tool activation policy.</summary>
public interface IProgressiveToolActivationPolicy
{
    /// <summary>Returns whether a dormant tool schema is eligible for one session and run.</summary>
    bool IsActive(string toolId, SessionId sessionId, RunId runId);
}

/// <summary>Projection explaining current fetch activation without exposing URLs or opaque references.</summary>
public sealed record WebFetchActivationStatus
{
    /// <summary>Whether at least one current route activates the schema.</summary>
    public bool Active { get; init; }

    /// <summary>Bounded host-authored activation reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Generation changed whenever activation authority changes.</summary>
    public long Generation { get; init; }

    /// <summary>Number of current result-derived routes.</summary>
    public int SearchResultRoutes { get; init; }

    /// <summary>Number of current-message URL routes.</summary>
    public int CurrentUserMessageRoutes { get; init; }

    /// <summary>Whether an exact direct route is current.</summary>
    public bool DirectRouteAvailable { get; init; }

    /// <summary>Number of runs with progressive fetch activation.</summary>
    public int ActiveRuns { get; init; }
}

/// <summary>Opaque bounded projection for one exact current-message URL authority record.</summary>
public sealed record UserUrlReference
{
    /// <summary>Opaque model-facing reference.</summary>
    public required string Id { get; init; }

    /// <summary>One-based candidate ordinal in the current raw message.</summary>
    public int Ordinal { get; init; }

    /// <summary>Non-reversible digest of the exact normalized URL.</summary>
    public required string UrlDigest { get; init; }

    /// <summary>Fresh user message that issued the authority.</summary>
    public required ConversationMessageId MessageId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning top-level run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Bounded expiry.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Outcome of a host-owned direct-fetch approval prompt.</summary>
public enum DirectFetchApprovalOutcome
{
    /// <summary>The exact pending invocation was approved once.</summary>
    Approved,

    /// <summary>The user denied or cancelled the prompt.</summary>
    Denied,

    /// <summary>No interactive prompt is available, including headless execution.</summary>
    Unavailable,
}

/// <summary>Sanitized model-proposed destination shown at the host approval boundary.</summary>
public sealed record DirectFetchApprovalRequest
{
    /// <summary>Exact pending invocation identity.</summary>
    public required ToolInvocationId ToolInvocationId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Canonical public HTTPS origin without credentials.</summary>
    public required string Origin { get; init; }

    /// <summary>Escaped path without query values or fragment.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the protected exact URL contains a query.</summary>
    public bool QueryPresent { get; init; }

    /// <summary>Non-reversible digest of the protected exact URL.</summary>
    public required string UrlDigest { get; init; }

    /// <summary>Host-authored provenance label.</summary>
    public string Source { get; init; } = "ModelProposed";
}

/// <summary>Process-local URL-free direct-fetch prompt lifecycle notification.</summary>
public interface IDirectFetchApprovalPromptNotification : IDomainEvent
{
}

/// <summary>URL-free transient notification that an interactive direct-fetch prompt started.</summary>
public sealed record DirectFetchApprovalPromptStarted(
    SessionId SessionId,
    DateTimeOffset OccurredAt) : IDirectFetchApprovalPromptNotification
{
    /// <inheritdoc />
    public int SchemaVersion => 1;
}

/// <summary>URL-free transient notification that an interactive direct-fetch prompt completed.</summary>
public sealed record DirectFetchApprovalPromptCompleted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    DirectFetchApprovalOutcome Outcome) : IDirectFetchApprovalPromptNotification
{
    /// <inheritdoc />
    public int SchemaVersion => 1;
}

/// <summary>TUI-neutral host boundary for one exact model-proposed fetch decision.</summary>
public interface IDirectFetchApprovalPrompt
{
    /// <summary>Requests one explicit decision without exposing the protected exact URL.</summary>
    Task<DirectFetchApprovalOutcome> RequestApprovalAsync(
        DirectFetchApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Serialized prompt router that denies when no interactive adapter is attached.</summary>
public sealed class DirectFetchApprovalPromptRouter : IDirectFetchApprovalPrompt, IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>>? _handler;
    private Func<IDirectFetchApprovalPromptNotification, CancellationToken, Task>? _notificationHandler;
    private bool _disposed;

    /// <summary>Attaches the one current interactive prompt adapter.</summary>
    public IDisposable Attach(
        Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>> handler,
        Func<IDirectFetchApprovalPromptNotification, CancellationToken, Task>? notificationHandler = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handler is not null)
            {
                throw new InvalidOperationException("A direct-fetch approval prompt is already attached.");
            }

            _handler = handler;
            _notificationHandler = notificationHandler;
            return new HandlerLease(this, handler);
        }
    }

    /// <inheritdoc />
    public async Task<DirectFetchApprovalOutcome> RequestApprovalAsync(
        DirectFetchApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _promptGate.WaitAsync(cancellationToken);
        try
        {
            Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>>? handler;
            Func<IDirectFetchApprovalPromptNotification, CancellationToken, Task>? notificationHandler;
            lock (_gate)
            {
                handler = _disposed ? null : _handler;
                notificationHandler = _disposed ? null : _notificationHandler;
            }

            if (handler is null)
            {
                return DirectFetchApprovalOutcome.Unavailable;
            }

            if (notificationHandler is not null)
            {
                await notificationHandler(
                    new DirectFetchApprovalPromptStarted(request.SessionId, DateTimeOffset.UtcNow),
                    cancellationToken);
            }

            var outcome = DirectFetchApprovalOutcome.Denied;
            try
            {
                outcome = await handler(request, cancellationToken);
                return outcome;
            }
            finally
            {
                if (notificationHandler is not null)
                {
                    await notificationHandler(
                        new DirectFetchApprovalPromptCompleted(
                            request.SessionId,
                            DateTimeOffset.UtcNow,
                            outcome),
                        CancellationToken.None);
                }
            }
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handler = null;
            _notificationHandler = null;
        }

        _promptGate.Dispose();
    }

    private void Detach(
        Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>> handler)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_handler, handler))
            {
                _handler = null;
                _notificationHandler = null;
            }
        }
    }

    private sealed class HandlerLease : IDisposable
    {
        private readonly DirectFetchApprovalPromptRouter _owner;
        private Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>>? _handler;

        internal HandlerLease(
            DirectFetchApprovalPromptRouter owner,
            Func<DirectFetchApprovalRequest, CancellationToken, Task<DirectFetchApprovalOutcome>> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var handler =
                Interlocked.Exchange(ref _handler, null);
            if (handler is not null)
            {
                _owner.Detach(handler);
            }
        }
    }
}

/// <summary>Deterministically recognizes bounded absolute HTTPS candidates in one fresh raw user message.</summary>
public static class CurrentUserUrlRecognizer
{
    /// <summary>Maximum raw message characters scanned for URL authority.</summary>
    public const int MaximumScannedCharacters = 32 * 1024;

    /// <summary>Maximum unique candidates accepted from one message.</summary>
    public const int MaximumCandidates = 8;

    /// <summary>Returns whether the bounded raw message contains at least one structurally eligible candidate.</summary>
    public static bool HasEligibleCandidate(string rawMessage, int maximumUrlCharacters = 2048)
    {
        return Recognize(rawMessage, maximumUrlCharacters).Count > 0;
    }

    /// <summary>Returns bounded unique normalized candidates with protected exact URLs.</summary>
    internal static IReadOnlyList<RecognizedUserUrl> Recognize(
        string rawMessage,
        int maximumUrlCharacters)
    {
        ArgumentNullException.ThrowIfNull(rawMessage);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUrlCharacters, 1);
        var length = Math.Min(rawMessage.Length, MaximumScannedCharacters);
        var results = new List<RecognizedUserUrl>(MaximumCandidates);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        var searchIndex = 0;
        while (searchIndex < length && results.Count < MaximumCandidates)
        {
            var start = rawMessage.IndexOf("https://", searchIndex, length - searchIndex, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            if (!HasSupportedStartBoundary(rawMessage, start))
            {
                searchIndex = start + "https://".Length;
                continue;
            }

            var end = start;
            while (end < length && !IsTerminator(rawMessage[end]))
            {
                end++;
            }

            var scanBoundaryCutsCandidate = end == length
                && length < rawMessage.Length;
            searchIndex = Math.Max(end, start + "https://".Length);
            var apostropheCutsCandidate = end < length && rawMessage[end] == '\'';
            if (scanBoundaryCutsCandidate || apostropheCutsCandidate)
            {
                continue;
            }

            var candidate = TrimTerminalPunctuation(rawMessage[start..end]);
            if (candidate.Length == 0)
            {
                continue;
            }

            try
            {
                var normalized = WebFetchUrlPolicy.Normalize(candidate, maximumUrlCharacters);
                var digest = WebFetchUrlPolicy.Digest(normalized);
                if (digests.Add(digest))
                {
                    results.Add(new RecognizedUserUrl(normalized, digest, results.Count + 1));
                }
            }
            catch (WebFetchException)
            {
                // Invalid spans are deliberately ignored and never trigger DNS or network work.
            }
            catch (ArgumentException)
            {
                // Empty or structurally invalid spans create no authority.
            }
        }

        return results;
    }

    private static bool HasSupportedStartBoundary(string rawMessage, int start)
    {
        if (start == 0)
        {
            return true;
        }

        var preceding = rawMessage[start - 1];
        return char.IsWhiteSpace(preceding)
            || char.IsControl(preceding)
            || preceding is '(' or '[' or '{' or '<' or '"' or '\'';
    }

    private static bool IsTerminator(char character)
    {
        return char.IsWhiteSpace(character)
                || char.IsControl(character)
                || character is '<' or '>' or '"' or '\'';
    }

    private static string TrimTerminalPunctuation(string candidate)
    {
        var length = candidate.Length;
        while (length > 0 && candidate[length - 1] is '.' or ',' or ';' or '!')
        {
            length--;
        }

        length = TrimUnbalanced(candidate, length, '(', ')');
        length = TrimUnbalanced(candidate, length, '[', ']');
        length = TrimUnbalanced(candidate, length, '{', '}');
        return candidate[..length];
    }

    private static int TrimUnbalanced(string candidate, int length, char opening, char closing)
    {
        while (length > 0 && candidate[length - 1] == closing)
        {
            var openings = candidate.AsSpan(0, length).Count(opening);
            var closings = candidate.AsSpan(0, length).Count(closing);
            if (closings <= openings)
            {
                break;
            }

            length--;
        }

        return length;
    }

    /// <summary>Protected normalized candidate retained only at the live intake boundary.</summary>
    internal sealed record RecognizedUserUrl(Uri Url, string Digest, int Ordinal);
}

/// <summary>Transient repository-bound references and exact one-shot direct URL grants.</summary>
public sealed class WebFetchAuthorizationAuthority : IProgressiveToolActivationPolicy
{
    private const int MaximumReferences = 100;
    private readonly Lock _gate = new();
    private readonly WebFetchOptionsState _options;
    private readonly Dictionary<(SessionId SessionId, RunId RunId), DateTimeOffset> _activeRuns = [];
    private readonly Dictionary<string, SearchReference> _searchReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UserMessageReference> _userReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectGrant> _directGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<ToolInvocationId, InvocationGrant> _invocationGrants = [];
    private Func<string, bool> _currentMessageConsentEvaluator = static _ => true;
    private long _generation;
    private long _scopeGeneration;

    /// <summary>Initializes a new instance of the <see cref="WebFetchAuthorizationAuthority"/> class with fixed effective limits.</summary>
    public WebFetchAuthorizationAuthority(WebFetchOptions options)
        : this(new WebFetchOptionsState(options))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebFetchAuthorizationAuthority"/> class with rebindable effective limits.</summary>
    public WebFetchAuthorizationAuthority(WebFetchOptionsState options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets the maximum number of URLs accepted in one direct authorization chain.</summary>
    public int MaximumDirectUrlCount => _options.Current.MaximumRedirects + 1;

    /// <summary>Returns whether live schema-3 consent still covers ergonomic routes in one repository.</summary>
    public bool HasCurrentMessageRouteConsent(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Volatile.Read(ref _currentMessageConsentEvaluator)(repositoryRoot);
    }

    /// <summary>Rebinds repository narrowing limits and revokes every authorization issued under prior limits.</summary>
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        await _options.BindRepositoryAsync(repositoryRoot, cancellationToken);
        RevokeAll();
    }

    /// <summary>Issues one opaque repository-bound reference for a normalized search result.</summary>
    public string IssueSearchResult(
        string repositoryRoot,
        Uri exactUrl,
        SessionId sessionId,
        RunId producingRunId,
        ToolInvocationId producingInvocationId,
        string providerId,
        string queryIdentity,
        int ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(exactUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);

        var options = _options.Current;
        var normalizedUrl = WebFetchUrlPolicy.Normalize(exactUrl.AbsoluteUri, options.MaximumUrlCharacters);
        var id = CreateOpaqueId();
        lock (_gate)
        {
            PruneCore();
            MakeReferenceCapacityCore();
            var now = DateTimeOffset.UtcNow;
            _searchReferences[id] = new SearchReference(
                OutboundConsentStore.DeriveRepositoryIdentity(repositoryRoot),
                normalizedUrl,
                sessionId,
                producingRunId,
                producingInvocationId,
                providerId,
                queryIdentity,
                ordinal,
                now,
                now + options.ReferenceLifetime,
                _scopeGeneration);
            _activeRuns[(sessionId, producingRunId)] = now + options.ReferenceLifetime;
            IncrementGenerationCore();
            return id;
        }
    }

    /// <summary>Issues bounded opaque references only from one fresh raw top-level user message.</summary>
    public IReadOnlyList<UserUrlReference> IssueCurrentUserMessageUrls(
        string repositoryRoot,
        SessionId sessionId,
        RunId runId,
        ConversationMessageId messageId,
        string rawMessage,
        ToolInvocationContext policyContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(rawMessage);
        ArgumentNullException.ThrowIfNull(policyContext);
        if (messageId == default)
        {
            throw new ArgumentException("The fresh conversation message id cannot be default.", nameof(messageId));
        }

        var options = _options.Current;
        var candidates =
            CurrentUserUrlRecognizer.Recognize(rawMessage, options.MaximumUrlCharacters);
        lock (_gate)
        {
            PruneCore();
            var removed = _userReferences.RemoveWhere(item => item.Value.SessionId == sessionId);
            removed += _invocationGrants.RemoveWhere(item => item.Value.SessionId == sessionId);
            foreach ((var activeSessionId, var activeRunId) in _activeRuns.Keys
                .Where(item => item.SessionId == sessionId && item.RunId != runId)
                .ToArray())
            {
                _activeRuns.Remove((activeSessionId, activeRunId));
                removed++;
            }

            if (removed > 0)
            {
                IncrementGenerationCore();
            }

            if (candidates.Count == 0)
            {
                return [];
            }

            var repositoryIdentity = OutboundConsentStore.DeriveRepositoryIdentity(repositoryRoot);
            var policyFingerprint = CreatePolicyFingerprint(policyContext);
            var now = DateTimeOffset.UtcNow;
            var expiresAt = now + options.ReferenceLifetime;
            var issued = new List<UserUrlReference>(candidates.Count);
            foreach (var candidate in candidates)
            {
                MakeReferenceCapacityCore();
                var id = CreateOpaqueId();
                _userReferences[id] = new UserMessageReference(
                    repositoryIdentity,
                    candidate.Url,
                    candidate.Digest,
                    sessionId,
                    runId,
                    messageId,
                    candidate.Ordinal,
                    policyFingerprint,
                    now,
                    expiresAt,
                    _scopeGeneration);
                issued.Add(new UserUrlReference
                {
                    Id = id,
                    Ordinal = candidate.Ordinal,
                    UrlDigest = candidate.Digest,
                    MessageId = messageId,
                    SessionId = sessionId,
                    RunId = runId,
                    ExpiresAt = expiresAt,
                });
            }

            _activeRuns[(sessionId, runId)] = expiresAt;
            IncrementGenerationCore();
            return issued;
        }
    }

    /// <summary>Creates an exact one-shot direct authorization from an explicit host-owned user action.</summary>
    public string GrantDirectUrl(string repositoryRoot, SessionId sessionId, string url)
    {
        return GrantDirectUrlChain(repositoryRoot, sessionId, [url]);
    }

    /// <summary>Creates one invocation-bound direct authorization chain from an explicit host-owned user action.</summary>
    public string GrantDirectUrlChain(string repositoryRoot, SessionId sessionId, IReadOnlyList<string> urls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(urls);
        var options = _options.Current;
        if (urls.Count < 1 || urls.Count > options.MaximumRedirects + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(urls), "A direct fetch chain must fit the configured redirect bound.");
        }

        Uri[] normalizedUrls = [.. urls.Select(url => WebFetchUrlPolicy.Normalize(url, options.MaximumUrlCharacters))];
        string[] digests = [.. normalizedUrls.Select(WebFetchUrlPolicy.Digest)];
        if (digests.Distinct(StringComparer.Ordinal).Count() != digests.Length)
        {
            throw new WebFetchException(WebFetchFailureKind.InvalidRequest, "A direct fetch chain cannot contain duplicate URLs.");
        }

        var repositoryIdentity = OutboundConsentStore.DeriveRepositoryIdentity(repositoryRoot);
        var groupId = CreateOpaqueId();
        lock (_gate)
        {
            PruneCore();
            if (digests.Any(_directGrants.ContainsKey))
            {
                throw new WebFetchException(WebFetchFailureKind.InvalidRequest, "An exact direct URL already has current authorization.");
            }

            var expiresAt = DateTimeOffset.UtcNow + options.ReferenceLifetime;
            for (var index = 0; index < normalizedUrls.Length; index++)
            {
                _directGrants[digests[index]] = new DirectGrant(
                    repositoryIdentity,
                    sessionId,
                    normalizedUrls[index],
                    groupId,
                    index == 0,
                    expiresAt,
                    _scopeGeneration);
            }

            IncrementGenerationCore();
            return digests[0];
        }
    }

    /// <summary>Returns the host claim behind an opaque search result without consuming it.</summary>
    public string GetSearchResultHost(string searchResultId)
    {
        return GetReferenceHost(_searchReferences, searchResultId, static reference => reference.Url);
    }

    /// <summary>Returns the host claim behind a current-message URL reference without consuming it.</summary>
    public string GetUserUrlHost(string userUrlId)
    {
        return GetReferenceHost(_userReferences, userUrlId, static reference => reference.Url);
    }

    /// <summary>Resolves and consumes one current route.</summary>
    public WebFetchAuthorization Resolve(ToolExecutionContext context, WebFetchRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var repositoryIdentity = OutboundConsentStore.DeriveRepositoryIdentity(context.Invocation.RepositoryPath);
        lock (_gate)
        {
            PruneCore();
            if (request.SearchResultId is not null)
            {
                if (!_searchReferences.TryGetValue(request.SearchResultId, out var reference)
                    || !string.Equals(reference.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                    || reference.SessionId != context.SessionId
                    || reference.ProducingRunId != context.RunId
                    || reference.ScopeGeneration != _scopeGeneration)
                {
                    throw StaleReference("search result");
                }

                _searchReferences.Remove(request.SearchResultId);
                IncrementGenerationCore();
                return new WebFetchAuthorization(
                    reference.Url,
                    WebFetchSourceKind.SearchResult,
                    new HashSet<string>(StringComparer.Ordinal));
            }

            if (request.UserUrlId is not null)
            {
                if (!_userReferences.TryGetValue(request.UserUrlId, out var reference)
                    || !string.Equals(reference.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                    || reference.SessionId != context.SessionId
                    || reference.RunId != context.RunId
                    || reference.ScopeGeneration != _scopeGeneration
                    || !HasCurrentMessageRouteConsent(context.Invocation.RepositoryPath)
                    || !string.Equals(
                        reference.PolicyFingerprint,
                        CreatePolicyFingerprint(context.Invocation),
                        StringComparison.Ordinal))
                {
                    throw StaleReference("current-user URL");
                }

                _userReferences.Remove(request.UserUrlId);
                IncrementGenerationCore();
                return new WebFetchAuthorization(
                    reference.Url,
                    WebFetchSourceKind.CurrentUserMessage,
                    new HashSet<string>([reference.UrlDigest], StringComparer.Ordinal));
            }

            var uri = WebFetchUrlPolicy.Normalize(request.Url ?? string.Empty, _options.Current.MaximumUrlCharacters);
            var digest = WebFetchUrlPolicy.Digest(uri);
            if (_invocationGrants.TryGetValue(context.ToolInvocationId, out var invocationGrant)
                && invocationGrant.SessionId == context.SessionId
                && invocationGrant.RunId == context.RunId
                && string.Equals(invocationGrant.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                && string.Equals(invocationGrant.UrlDigest, digest, StringComparison.Ordinal)
                && invocationGrant.ScopeGeneration == _scopeGeneration)
            {
                _invocationGrants.Remove(context.ToolInvocationId);
                IncrementGenerationCore();
                return new WebFetchAuthorization(
                    invocationGrant.Url,
                    WebFetchSourceKind.ModelProposedApproved,
                    new HashSet<string>([digest], StringComparer.Ordinal));
            }

            if (!_directGrants.TryGetValue(digest, out var grant)
                || !string.Equals(grant.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                || grant.SessionId != context.SessionId
                || !grant.IsInitialUrl
                || grant.ScopeGeneration != _scopeGeneration)
            {
                throw new WebFetchException(
                    WebFetchFailureKind.DirectAuthorizationRequired,
                    "DirectAuthorizationRequired: the exact model-proposed public URL requires inline approval or explicit pre-authorization.");
            }

            _directGrants.Remove(digest);
            string[] redirectGrantDigests = [.. _directGrants
                .Where(item => string.Equals(item.Value.GroupId, grant.GroupId, StringComparison.Ordinal))
                .Select(item => item.Key)];
            foreach (var redirectGrantDigest in redirectGrantDigests)
            {
                _directGrants.Remove(redirectGrantDigest);
            }

            IncrementGenerationCore();
            return new WebFetchAuthorization(
                grant.Url,
                WebFetchSourceKind.ExplicitDirectGroup,
                new HashSet<string>([digest, .. redirectGrantDigests], StringComparer.Ordinal));
        }
    }

    /// <summary>Returns whether an exact URL already has an executable direct grant.</summary>
    public bool HasDirectGrant(ToolExecutionContext context, string url)
    {
        ArgumentNullException.ThrowIfNull(context);
        var normalized = WebFetchUrlPolicy.Normalize(url, _options.Current.MaximumUrlCharacters);
        var digest = WebFetchUrlPolicy.Digest(normalized);
        var repositoryIdentity = OutboundConsentStore.DeriveRepositoryIdentity(context.Invocation.RepositoryPath);
        lock (_gate)
        {
            PruneCore();
            return (_directGrants.TryGetValue(digest, out var direct)
                    && direct.IsInitialUrl
                    && direct.SessionId == context.SessionId
                    && string.Equals(direct.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal))
                || (_invocationGrants.TryGetValue(context.ToolInvocationId, out var invocation)
                    && invocation.SessionId == context.SessionId
                    && invocation.RunId == context.RunId
                    && string.Equals(invocation.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                    && string.Equals(invocation.UrlDigest, digest, StringComparison.Ordinal));
        }
    }

    /// <summary>Returns whether progressive authority was issued for the exact current run.</summary>
    public bool IsRunProgressivelyActive(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            PruneCore();
            return _activeRuns.ContainsKey((sessionId, runId));
        }
    }

    /// <summary>Creates one exact grant for the same approved model invocation.</summary>
    public void GrantModelProposedInvocation(ToolExecutionContext context, Uri exactUrl)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exactUrl);
        var normalized = WebFetchUrlPolicy.Normalize(exactUrl.AbsoluteUri, _options.Current.MaximumUrlCharacters);
        var digest = WebFetchUrlPolicy.Digest(normalized);
        lock (_gate)
        {
            PruneCore();
            if (!_activeRuns.ContainsKey((context.SessionId, context.RunId))
                || !HasCurrentMessageRouteConsent(context.Invocation.RepositoryPath))
            {
                throw new WebFetchException(
                    WebFetchFailureKind.DirectAuthorizationRequired,
                    "DirectAuthorizationRequired: web_fetch is not progressively active for this run.");
            }

            _invocationGrants[context.ToolInvocationId] = new InvocationGrant(
                OutboundConsentStore.DeriveRepositoryIdentity(context.Invocation.RepositoryPath),
                context.SessionId,
                context.RunId,
                normalized,
                digest,
                DateTimeOffset.UtcNow + _options.Current.ReferenceLifetime,
                _scopeGeneration);
            IncrementGenerationCore();
        }
    }

    /// <summary>Invalidates authority owned by one terminal run.</summary>
    public void RevokeRun(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            var removed = _searchReferences.RemoveWhere(item =>
                    item.Value.SessionId == sessionId && item.Value.ProducingRunId == runId)
                + _userReferences.RemoveWhere(item =>
                    item.Value.SessionId == sessionId && item.Value.RunId == runId)
                + _invocationGrants.RemoveWhere(item =>
                    item.Value.SessionId == sessionId && item.Value.RunId == runId);
            if (_activeRuns.Remove((sessionId, runId)))
            {
                removed++;
            }

            if (removed > 0)
            {
                IncrementGenerationCore();
            }
        }
    }

    /// <summary>Invalidates every route for consent, tool, policy, options, repository, session, or shutdown changes.</summary>
    public void RevokeAll()
    {
        lock (_gate)
        {
            _searchReferences.Clear();
            _userReferences.Clear();
            _directGrants.Clear();
            _invocationGrants.Clear();
            _activeRuns.Clear();
            checked
            {
                _scopeGeneration++;
            }

            IncrementGenerationCore();
        }
    }

    /// <summary>Returns bounded activation state.</summary>
    public WebFetchActivationStatus GetStatus()
    {
        lock (_gate)
        {
            PruneCore();
            var active = _activeRuns.Count > 0 || _directGrants.Count > 0;
            return new WebFetchActivationStatus
            {
                Active = active,
                Reason = _userReferences.Count > 0
                    ? "eligible exact URLs in the current user message"
                    : _searchReferences.Count > 0
                        ? "eligible current web_search results"
                        : _directGrants.Count > 0
                            ? "exact one-shot direct URL authorization"
                            : _activeRuns.Count > 0
                                ? "progressively active for a current run"
                                : "dormant until a host-derived current route exists",
                Generation = _generation,
                SearchResultRoutes = _searchReferences.Count,
                CurrentUserMessageRoutes = _userReferences.Count,
                DirectRouteAvailable = _directGrants.Count > 0,
                ActiveRuns = _activeRuns.Count,
            };
        }
    }

    /// <inheritdoc />
    public bool IsActive(string toolId, SessionId sessionId, RunId runId)
    {
        if (!string.Equals(toolId, "web_fetch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        lock (_gate)
        {
            PruneCore();
            return _activeRuns.ContainsKey((sessionId, runId))
                || _directGrants.Values.Any(grant => grant.SessionId == sessionId);
        }
    }

    /// <summary>Returns every policy-visible host in an exact explicit direct grant's closed redirect scope.</summary>
    public IReadOnlyList<string> GetDirectRouteHosts(string url)
    {
        var normalized = WebFetchUrlPolicy.Normalize(url, _options.Current.MaximumUrlCharacters);
        var digest = WebFetchUrlPolicy.Digest(normalized);
        lock (_gate)
        {
            PruneCore();
            if (!_directGrants.TryGetValue(digest, out var initialGrant) || !initialGrant.IsInitialUrl)
            {
                return [];
            }

            return _directGrants.Values
                .Where(grant => string.Equals(grant.GroupId, initialGrant.GroupId, StringComparison.Ordinal))
                .Select(grant => grant.Url.IdnHost)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Checks whether a policy claim is covered by exact current host-owned authorization.</summary>
    public bool IsHostAuthorized(WebFetchRequest request, string repositoryIdentity, string networkHost)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkHost);
        lock (_gate)
        {
            PruneCore();
            if (request.SearchResultId is not null)
            {
                return _searchReferences.TryGetValue(request.SearchResultId, out var reference)
                    && string.Equals(reference.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                    && string.Equals(reference.Url.IdnHost, networkHost, StringComparison.OrdinalIgnoreCase);
            }

            if (request.UserUrlId is not null)
            {
                return _userReferences.TryGetValue(request.UserUrlId, out var reference)
                    && string.Equals(reference.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                    && string.Equals(reference.Url.IdnHost, networkHost, StringComparison.OrdinalIgnoreCase);
            }

            Uri normalized;
            try
            {
                normalized = WebFetchUrlPolicy.Normalize(request.Url ?? string.Empty, _options.Current.MaximumUrlCharacters);
            }
            catch (WebFetchException)
            {
                return false;
            }

            var digest = WebFetchUrlPolicy.Digest(normalized);
            return _directGrants.TryGetValue(digest, out var initialGrant)
                && initialGrant.IsInitialUrl
                && string.Equals(initialGrant.RepositoryIdentity, repositoryIdentity, StringComparison.Ordinal)
                && _directGrants.Values.Any(grant => string.Equals(grant.GroupId, initialGrant.GroupId, StringComparison.Ordinal)
                    && string.Equals(grant.Url.IdnHost, networkHost, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Builds a secret-free approval projection for one protected exact URL.</summary>
    public DirectFetchApprovalRequest CreateApprovalRequest(ToolExecutionContext context, Uri exactUrl)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exactUrl);
        var normalized = WebFetchUrlPolicy.Normalize(exactUrl.AbsoluteUri, _options.Current.MaximumUrlCharacters);
        return new DirectFetchApprovalRequest
        {
            ToolInvocationId = context.ToolInvocationId,
            SessionId = context.SessionId,
            RunId = context.RunId,
            Origin = BoundProjection(
                normalized.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped),
                320),
            Path = CreateRedactedPathProjection(normalized),
            QueryPresent = !string.IsNullOrEmpty(normalized.Query),
            UrlDigest = WebFetchUrlPolicy.Digest(normalized),
        };
    }

    /// <summary>Configures live schema-3 consent validation for ergonomic routes.</summary>
    internal void SetCurrentMessageConsentEvaluator(Func<string, bool> evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        Volatile.Write(ref _currentMessageConsentEvaluator, evaluator);
    }

    private static string CreateRedactedPathProjection(Uri normalized)
    {
        var escapedPath = normalized.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (string.IsNullOrEmpty(escapedPath))
        {
            return string.Empty;
        }

        var redacted = string.Join(
            '/',
            escapedPath.Split('/', StringSplitOptions.None)
                .Select(segment => segment.Length == 0 ? string.Empty : "[REDACTED]"));
        return BoundProjection(redacted, 512);
    }

    private static string BoundProjection(string value, int maximumCharacters)
    {
        return value.Length <= maximumCharacters
                ? value
                : value[..(maximumCharacters - 3)] + "...";
    }

    private static string CreateOpaqueId()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    }

    private static WebFetchException StaleReference(string source)
    {
        return new(
                WebFetchFailureKind.InvalidRequest,
                $"The {source} reference is unknown, stale, consumed, generation-mismatched, or belongs to another scope.");
    }

    private static string CreatePolicyFingerprint(ToolInvocationContext context)
    {
        var builder = new StringBuilder()
            .Append(context.RepositoryPath).Append('\n')
            .Append(context.TrustLevel).Append('\n')
            .Append(context.DenyAllTools).Append('\n');
        Append(builder, context.AllowedToolIds);
        Append(builder, context.DeniedToolIds);
        Append(builder, context.RequireApprovalToolIds);
        Append(builder, context.AllowedNetworkHosts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();

        static void Append(StringBuilder target, IReadOnlyList<string> values)
        {
            foreach (var value in values.Order(StringComparer.OrdinalIgnoreCase))
            {
                target.Append(value).Append('\n');
            }

            target.Append("--\n");
        }
    }

    private string GetReferenceHost<TReference>(
        Dictionary<string, TReference> references,
        string id,
        Func<TReference, Uri> urlSelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            PruneCore();
            return references.TryGetValue(id, out var reference)
                ? urlSelector(reference).IdnHost
                : "invalid-web-fetch-target";
        }
    }

    private void MakeReferenceCapacityCore()
    {
        while (_searchReferences.Count + _userReferences.Count >= MaximumReferences)
        {
            (string Id, bool IsUser, DateTimeOffset IssuedAt) oldest = _searchReferences
                .Select(item => (item.Key, false, item.Value.IssuedAt))
                .Concat(_userReferences.Select(item => (item.Key, true, item.Value.IssuedAt)))
                .OrderBy(item => item.IssuedAt)
                .First();
            if (oldest.IsUser)
            {
                _userReferences.Remove(oldest.Id);
            }
            else
            {
                _searchReferences.Remove(oldest.Id);
            }

            IncrementGenerationCore();
        }
    }

    private void PruneCore()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = _searchReferences.RemoveWhere(item => item.Value.ExpiresAt <= now)
            + _userReferences.RemoveWhere(item => item.Value.ExpiresAt <= now)
            + _directGrants.RemoveWhere(item => item.Value.ExpiresAt <= now)
            + _invocationGrants.RemoveWhere(item => item.Value.ExpiresAt <= now);
        removed += _activeRuns.RemoveWhere(item => item.Value <= now);

        if (removed > 0)
        {
            IncrementGenerationCore();
        }
    }

    private void IncrementGenerationCore()
    {
        checked
        {
            _generation++;
        }
    }

    private sealed record SearchReference(
        string RepositoryIdentity,
        Uri Url,
        SessionId SessionId,
        RunId ProducingRunId,
        ToolInvocationId ProducingInvocationId,
        string ProviderId,
        string QueryIdentity,
        int Ordinal,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        long ScopeGeneration);

    private sealed record UserMessageReference(
        string RepositoryIdentity,
        Uri Url,
        string UrlDigest,
        SessionId SessionId,
        RunId RunId,
        ConversationMessageId MessageId,
        int Ordinal,
        string PolicyFingerprint,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        long ScopeGeneration);

    private sealed record DirectGrant(
        string RepositoryIdentity,
        SessionId SessionId,
        Uri Url,
        string GroupId,
        bool IsInitialUrl,
        DateTimeOffset ExpiresAt,
        long ScopeGeneration);

    private sealed record InvocationGrant(
        string RepositoryIdentity,
        SessionId SessionId,
        RunId RunId,
        Uri Url,
        string UrlDigest,
        DateTimeOffset ExpiresAt,
        long ScopeGeneration);
}

/// <summary>Resolved transient authorization passed only to transport.</summary>
public sealed record WebFetchAuthorization(
    Uri Url,
    WebFetchSourceKind SourceKind,
    IReadOnlySet<string> AuthorizedDirectUrlDigests);

/// <summary>Progressively disclosed governed readable web-fetch tool.</summary>
public sealed class WebFetchTool : Tool<WebFetchRequest, WebFetchResponse>, IHostAuthorizedNetworkClaims
{
    private readonly WebFetchAuthorizationAuthority _authorization;
    private readonly IWebContentFetcher _fetcher;
    private readonly IDirectFetchApprovalPrompt _approvalPrompt;

    /// <summary>Initializes a new instance of the <see cref="WebFetchTool"/> class.</summary>
    public WebFetchTool(
        IWebContentFetcher fetcher,
        WebFetchAuthorizationAuthority authorization,
        WebFetchOptions options,
        IDirectFetchApprovalPrompt? approvalPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(options);
        _fetcher = fetcher;
        _authorization = authorization;
        _approvalPrompt = approvalPrompt ?? new UnavailableDirectFetchApprovalPrompt();
        Definition = ToolDefinitionFactory.Create<WebFetchRequest, WebFetchResponse>(
            "web_fetch",
            "Retrieves one authorized public HTTPS textual document. Use searchResultId for a search result, userUrlId for a URL in the current user message, or url for an explicitly granted or separately approved direct destination.",
            ToolCategory.ExternalSearch,
            RepositoryTrustLevel.UntrustedInspection,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            options.Timeout,
            CalculateMaximumOutputBytes(options)) with
        {
            DisplayName = "Web Fetch",
            EnabledByDefault = false,
            RequiresOutboundConsent = true,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.SerializedPerRegistration,
                ClaimResolverId = "builtin-web-fetch-authorization-v2",
                MaximumSourceConcurrency = 1,
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<WebFetchResponse>> ExecuteAsync(
        WebFetchRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (input.Url is { } directUrl && !_authorization.HasDirectGrant(context, directUrl))
        {
            if (!_authorization.IsRunProgressivelyActive(context.SessionId, context.RunId)
                || !_authorization.HasCurrentMessageRouteConsent(context.Invocation.RepositoryPath))
            {
                throw new ToolExecutionException(
                    "DirectAuthorizationRequired: web_fetch is not progressively active for this run; use explicit exact-URL pre-authorization.",
                    ToolErrorClassification.DirectAuthorizationRequired);
            }

            var normalized = WebFetchUrlPolicy.Normalize(directUrl, int.MaxValue);
            var approvalRequest = _authorization.CreateApprovalRequest(context, normalized);
            var outcome = await _approvalPrompt.RequestApprovalAsync(
                approvalRequest,
                cancellationToken);
            if (outcome == DirectFetchApprovalOutcome.Unavailable)
            {
                var path = string.IsNullOrEmpty(approvalRequest.Path)
                    ? "/"
                    : approvalRequest.Path;
                var transientError = "DirectAuthorizationRequired: explicit exact-URL authority is required for "
                    + $"origin {approvalRequest.Origin}; path {path}; exact digest {approvalRequest.UrlDigest}. "
                    + "Use /fetch-authorize or the headless authorization input and retry.";
                throw new ToolExecutionException(
                    "DirectAuthorizationRequired: explicit exact-URL authority is required.",
                    ToolErrorClassification.DirectAuthorizationRequired,
                    transientError: transientError);
            }

            if (outcome != DirectFetchApprovalOutcome.Approved)
            {
                throw new ToolExecutionException(
                    "The exact model-proposed web destination was denied or cancelled.",
                    ToolErrorClassification.ApprovalDenied);
            }

            try
            {
                _authorization.GrantModelProposedInvocation(context, normalized);
            }
            catch (WebFetchException)
            {
                throw new ToolExecutionException(
                    "DirectAuthorizationRequired: the progressive fetch authority expired or changed before approval completed.",
                    ToolErrorClassification.DirectAuthorizationRequired);
            }
        }

        var authorization = _authorization.Resolve(context, input);
        var response = await _fetcher.FetchAsync(
            authorization.Url,
            authorization.SourceKind,
            authorization.AuthorizedDirectUrlDigests,
            cancellationToken);
        return new ToolExecution<WebFetchResponse>(
            response,
            [new ToolProvenanceSource(
                "external-web-fetch-untrusted",
                response.Provenance.FinalUrl,
                $"mediaType={response.MediaType};source={response.Provenance.SourceKind};sourceDigest={response.SourceDigest};extractor={response.ExtractionMethod};retrieved={response.Provenance.RetrievedAt:O}")],
            response.Truncation.Stage != WebFetchTruncationStage.None);
    }

    /// <inheritdoc />
    public bool IsNetworkHostAuthorized(object input, ToolInvocationContext context, string networkHost)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkHost);
        if (input is not WebFetchRequest request)
        {
            return false;
        }

        var repositoryIdentity = OutboundConsentStore.DeriveRepositoryIdentity(context.RepositoryPath);
        return _authorization.IsHostAuthorized(request, repositoryIdentity, networkHost);
    }

    /// <inheritdoc />
    protected override void ValidateInput(WebFetchRequest input)
    {
        var routeCount = (string.IsNullOrWhiteSpace(input.SearchResultId) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(input.UserUrlId) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(input.Url) ? 0 : 1);
        if (routeCount != 1)
        {
            throw new ToolArgumentValidationException("Specify exactly one of searchResultId, userUrlId, or url.");
        }

        ValidateOpaqueReference(input.SearchResultId, "search result");
        ValidateOpaqueReference(input.UserUrlId, "current-user URL");
        if (input.Url is not null)
        {
            _ = WebFetchUrlPolicy.Normalize(input.Url, int.MaxValue);
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(WebFetchRequest input)
    {
        if (input.SearchResultId is not null)
        {
            return [_authorization.GetSearchResultHost(input.SearchResultId)];
        }

        if (input.UserUrlId is not null)
        {
            return [_authorization.GetUserUrlHost(input.UserUrlId)];
        }

        try
        {
            return _authorization.GetDirectRouteHosts(input.Url ?? string.Empty);
        }
        catch (WebFetchException)
        {
            return ["invalid-web-fetch-target"];
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(WebFetchRequest input)
    {
        if (input.SearchResultId is not null)
        {
            return "authorized search result";
        }

        return input.UserUrlId is not null
            ? "current user URL"
            : "direct public URL";
    }

    private static void ValidateOpaqueReference(string? value, string source)
    {
        if (value is not null && (value.Length > 128 || value.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ToolArgumentValidationException($"The {source} reference is malformed.");
        }
    }

    private static int CalculateMaximumOutputBytes(WebFetchOptions options)
    {
        return checked(
                (options.MaximumExtractedCharacters * 6)
                + (options.MaximumUrlCharacters * ((options.MaximumRedirects * 2) + 4))
                + (128 * 1024));
    }

    private sealed class UnavailableDirectFetchApprovalPrompt : IDirectFetchApprovalPrompt
    {
        public Task<DirectFetchApprovalOutcome> RequestApprovalAsync(
            DirectFetchApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DirectFetchApprovalOutcome.Unavailable);
        }
    }
}

/// <summary>Provides bounded dictionary pruning without mutation during enumeration.</summary>
internal static class DictionaryPruningExtensions
{
    /// <summary>Removes entries matching a predicate and returns the removed count.</summary>
    internal static int RemoveWhere<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary,
        Func<KeyValuePair<TKey, TValue>, bool> predicate)
        where TKey : notnull
    {
        TKey[] keys = [.. dictionary.Where(predicate).Select(item => item.Key)];
        foreach (var key in keys)
        {
            dictionary.Remove(key);
        }

        return keys.Length;
    }
}
