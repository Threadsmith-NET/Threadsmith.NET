namespace Threadsmith.Policies.Grants;

using Threadsmith.Policies.Contracts;

/// <summary>Owns bounded live-session grants and atomic invocation admission, never checkpoint state.</summary>
public sealed class PolicyGrantAuthority
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _session = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (string Identity, long Epoch)> _once = [];
    private readonly HashSet<Guid> _responded = [];
    private readonly Dictionary<Guid, PersistentContinuation> _persistent = [];
    private long _epoch;

    /// <summary>Gets the live-session revocation identity.</summary>
    public long Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    /// <summary>Revokes all live grants on session or authority transitions.</summary>
    public void Reset(Guid? authorityTransitionId = null)
    {
        lock (_gate)
        {
            _persistent.TryGetValue(authorityTransitionId ?? Guid.Empty, out var pending);
            var retained = pending is { Refreshed: false } && pending.Epoch == _epoch ? pending : null;
            _epoch++;
            _session.Clear();
            _once.Clear();
            _responded.Clear();
            _persistent.Clear();
            if (retained is not null && authorityTransitionId is { } operation)
            {
                _persistent.Add(operation, retained with { Epoch = _epoch, Refreshed = true });
                _responded.Add(retained.Request.RequestId);
            }
        }
    }

    /// <summary>Reserves one exact Always response before IO without granting execution authority.</summary>
    public Guid? BeginPersistent(PolicyApprovalRequest request, PolicyApprovalResponse response, string ruleFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (!ValidResponse(request, response) || response.Choice != PolicyApprovalChoice.Always || _persistent.Count >= 1024)
            {
                return null;
            }

            var operation = Guid.NewGuid();
            _responded.Add(request.RequestId);
            _persistent.Add(operation, new PersistentContinuation(request, ruleFingerprint, _epoch, false));
            return operation;
        }
    }

    /// <summary>Issues one-use authority only after that operation's own refresh, never after independent revocation.</summary>
    public bool CompletePersistent(Guid operation, PolicyApprovalRequest request, string ruleFingerprint, out long epoch)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            epoch = -1;
            if (!_persistent.Remove(operation, out var pending) || !pending.Refreshed || pending.Epoch != _epoch
                || pending.Request != request || pending.RuleFingerprint != ruleFingerprint || _once.Count >= 1024)
            {
                return false;
            }

            epoch = _epoch;
            _once.Add(request.RequestId, (request.ContextFingerprint, epoch));
            return true;
        }
    }

    /// <summary>Releases an unfinished persistent continuation without undoing committed settings.</summary>
    public void CancelPersistent(Guid operation)
    {
        lock (_gate)
        {
            _persistent.Remove(operation);
        }
    }

    /// <summary>Inspects bounded opaque live-grant identities for user-only management.</summary>
    public IReadOnlyList<string> InspectSession()
    {
        lock (_gate)
        {
            return _session.Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Checks eligible remembered authority after the effective outcome is known.</summary>
    public bool HasRemembered(PolicyDecision decision, PolicySnapshot snapshot, string identity) =>
        HasRemembered(decision, snapshot, identity, out _);

    /// <summary>Captures the revocation epoch atomically with a remembered-grant decision.</summary>
    public bool HasRemembered(PolicyDecision decision, PolicySnapshot snapshot, string identity, out long epoch)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(snapshot);
        epoch = -1;
        if (!decision.IsAllowed || decision.Outcome is not (PolicyOutcome.ApprovalRequired or PolicyOutcome.ApprovalRequiredSession))
        {
            return false;
        }

        lock (_gate)
        {
            epoch = _epoch;
            return _session.Contains(Key(identity, decision.RuleFingerprint))
                || (decision.Outcome == PolicyOutcome.ApprovalRequired && snapshot.AlwaysAllow.Any(grant =>
                    grant.ContextFingerprint == identity && grant.RuleFingerprint == decision.RuleFingerprint));
        }
    }

    /// <summary>Validates an exact pending response and records one-use execution authority.</summary>
    public bool Approve(PolicyApprovalRequest request, PolicyApprovalResponse response, string ruleFingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (!ValidResponse(request, response) || response.Choice == PolicyApprovalChoice.Always)
            {
                return false;
            }

            if (response.Choice == PolicyApprovalChoice.Session)
            {
                if (_session.Count >= 1024)
                {
                    return false;
                }

                _session.Add(Key(request.ContextFingerprint, ruleFingerprint));
            }

            _responded.Add(request.RequestId);
            _once.Add(request.RequestId, (request.ContextFingerprint, _epoch));
            return true;
        }
    }

    /// <summary>Consumes one response at final execution admission; duplicates and retries cannot replay it.</summary>
    public bool Consume(Guid requestId, string identity, long epoch)
    {
        lock (_gate)
        {
            return epoch == _epoch && _once.Remove(requestId, out var grant) && grant.Epoch == epoch && grant.Identity == identity;
        }
    }

    /// <summary>Discards an abandoned invocation approval without affecting remembered session authority.</summary>
    public void Abandon(Guid requestId)
    {
        lock (_gate)
        {
            _once.Remove(requestId);
        }
    }

    private bool ValidResponse(PolicyApprovalRequest request, PolicyApprovalResponse response) =>
        request.SessionEpoch == _epoch && request.RequestId == response.RequestId
        && Enum.IsDefined(response.Choice) && response.Choice != PolicyApprovalChoice.Deny
        && request.Choices.Contains(response.Choice) && !_responded.Contains(request.RequestId)
        && _responded.Count < 4096 && _once.Count < 1024;

    private static string Key(string identity, string ruleFingerprint) => identity + ":" + ruleFingerprint;

    private sealed record PersistentContinuation(PolicyApprovalRequest Request, string RuleFingerprint, long Epoch, bool Refreshed);
}
