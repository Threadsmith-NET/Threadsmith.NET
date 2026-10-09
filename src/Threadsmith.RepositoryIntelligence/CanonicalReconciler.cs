namespace Threadsmith.RepositoryIntelligence;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>The single deterministic retention path over validated host-collected interpretation results.</summary>
internal sealed class CanonicalReconciler
{
    private readonly CanonicalIntelligenceStore _store;

    /// <summary>Initializes a new instance of the <see cref="CanonicalReconciler"/> class.</summary>
    internal CanonicalReconciler(CanonicalIntelligenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Reconciles one bounded unit without opening inference, collecting sources or rewriting memory.</summary>
    internal async Task<IntelligenceReconciliationReceipt> ReconcileAsync(
        RepositoryIntelligenceAdmission admission,
        RepositoryInterpretationResult interpretation,
        IntelligenceReconciliationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interpretation);
        ArgumentNullException.ThrowIfNull(request);
        CanonicalRecordValidation.Text(request.UnitId, 128);
        CanonicalRecordValidation.Provenance(request.Provenance, interpretation.LastPacket.Target);
        if (request.Provenance.OperationId != interpretation.OperationId || request.Provenance.Target != interpretation.LastPacket.Target
            || request.Provenance.Model != interpretation.Model || request.Selections.Count > 16
            || request.Selections.Select(selection => selection.CandidateKey).Distinct(StringComparer.Ordinal).Count() != request.Selections.Count)
        {
            throw new InvalidDataException("Retention selection does not belong to this interpretation unit.");
        }

        foreach (var selection in request.Selections)
        {
            CanonicalRecordValidation.Text(selection.CandidateKey, 64);
            CanonicalRecordValidation.Optional(selection.Reason, 1024);
            if (!Enum.IsDefined(selection.Action))
            {
                throw new InvalidDataException("Unsupported host retention action.");
            }

            if (selection.ConfirmDistinctFrom?.Count > 32)
            {
                throw new InvalidDataException("Distinct-claim authority exceeds its bounded collision frontier.");
            }
        }

        if (interpretation.Evidence.Count > 512 || interpretation.Evidence.Any(item => item.Text?.Length > 32768)
            || interpretation.Evidence.Sum(item => (long)(item.Text?.Length ?? 0)) > 1024 * 1024)
        {
            throw new InvalidDataException("Interpretation exceeds retention input bounds; select a smaller evidence unit.");
        }

        var resultJson = JsonSerializer.Serialize(interpretation, CanonicalRecordValidation.JsonOptions);
        if (Encoding.UTF8.GetByteCount(resultJson) > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Interpretation exceeds its serialized retention input bound.");
        }

        interpretation = JsonSerializer.Deserialize<RepositoryInterpretationResult>(resultJson, CanonicalRecordValidation.JsonOptions)
            ?? throw new InvalidDataException("Interpretation is empty.");
        request = JsonSerializer.Deserialize<IntelligenceReconciliationRequest>(CanonicalRecordValidation.Serialize(request), CanonicalRecordValidation.JsonOptions)
            ?? throw new InvalidDataException("Retention request is empty.");
        var receiptId = RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { interpretation.OperationId, request.UnitId }));

        // Bookkeeping timestamps do not change the identity of a replayed host operation/evidence unit.
        var fingerprint = RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new
        {
            interpretation.Response,
            interpretation.Evidence,
            interpretation.LastPacket.Target,
            interpretation.LastPacket.Selection,
            interpretation.Episodes,
            request.Selections,
            request.Provenance.SessionId,
            request.Provenance.RunId,
            request.Provenance.InvocationId,
            request.Provenance.UserAttribution,
        }));
        string? invalidReason = null;
        try
        {
            Validate(interpretation, request);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            invalidReason = "Candidate structure, evidence, locator, scope or host transition failed validation; no knowledge was changed.";
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = await _store.ReadReceiptAsync(admission, receiptId, cancellationToken);
            if (previous is not null)
            {
                if (previous.Fingerprint != fingerprint)
                {
                    throw new InvalidDataException("A completed retention unit cannot be rebound to different candidates or authority.");
                }

                return previous;
            }

            try
            {
                var keys = invalidReason is null
                    ? request.Selections.Select(selection => ClaimKey(interpretation.Response.Candidates.Single(candidate => candidate.Key == selection.CandidateKey), Scope(interpretation))).Distinct(StringComparer.Ordinal).ToArray()
                    : [];
                var ids = invalidReason is null
                    ? request.Selections.SelectMany(selection => new[] { selection.Target, selection.MergeFrom }).OfType<IntelligenceRevisionReference>().Select(reference => reference.ItemId).Distinct().ToArray()
                    : [];
                IntelligenceReadSnapshot snapshot;
                var readLimited = false;
                try
                {
                    snapshot = await _store.ReadAsync(admission, ids, keys, cancellationToken);
                }
                catch (IntelligenceStorageException exception) when (exception.Failure == IntelligenceStorageFailure.ReadLimit)
                {
                    snapshot = await _store.ReadAsync(admission, [], [], cancellationToken);
                    readLimited = true;
                }

                IntelligencePublication publication;
                if (readLimited)
                {
                    publication = DeferredUnit(snapshot.Generation, receiptId, fingerprint, request, "Canonical target lookup exceeded its byte capacity; select fewer targets in a new UnitId. No knowledge was changed.");
                }
                else if (invalidReason is not null)
                {
                    var rejected = request.Selections.Select(selection => new IntelligenceCandidateOutcome(selection.CandidateKey, IntelligenceReconciliationOutcome.Rejected, invalidReason, null, null, [])).ToArray();
                    publication = new IntelligencePublication(snapshot.Generation, [], [], [], [], [], Receipt: new(receiptId, fingerprint, request.Provenance, rejected, []));
                }
                else
                {
                    publication = await PrepareAsync(admission, interpretation, request, snapshot, receiptId, fingerprint, cancellationToken);
                }

                await _store.PublishAsync(admission, publication, cancellationToken);
                return publication.Receipt ?? throw new InvalidOperationException("Reconciliation did not produce its atomic receipt.");
            }
            catch (IntelligenceStorageException exception) when (exception.Failure == IntelligenceStorageFailure.RevisionConflict && attempt == 0)
            {
                // Reload and revalidate the entire unit; never blindly replay a previously prepared mutation.
                if (invalidReason is null)
                {
                    Validate(interpretation, request);
                }
            }
        }

        throw new InvalidOperationException("Reconciliation retry did not produce an outcome.");
    }

    private async Task<IntelligencePublication> PrepareAsync(
        RepositoryIntelligenceAdmission admission,
        RepositoryInterpretationResult interpretation,
        IntelligenceReconciliationRequest request,
        IntelligenceReadSnapshot snapshot,
        string receiptId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var scope = Scope(interpretation);
        var concepts = MemoryConcepts.Normalize(interpretation.LastPacket.Selection.Concepts).Where(concept => concept is not ("code" or "change")).ToArray();
        var evidence = interpretation.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var existingItems = snapshot.Items.ToDictionary(item => item.Reference.ItemId);
        var writes = new Dictionary<Guid, IntelligenceItemWrite>();
        var claims = new Dictionary<string, IntelligenceClaimIdentity>(StringComparer.Ordinal);
        var outcomes = new List<IntelligenceCandidateOutcome>();
        var uncertainties = new List<IntelligenceUncertainty>();
        var relationships = new List<IntelligenceRelationship>();
        var diagnostics = new List<string>();
        var retainedEvidence = new HashSet<string>(StringComparer.Ordinal);
        var candidates = interpretation.Response.Candidates.ToDictionary(candidate => candidate.Key, StringComparer.Ordinal);

        foreach (var selection in request.Selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[selection.CandidateKey];
            var key = ClaimKey(candidate, scope);
            var known = snapshot.Items.FirstOrDefault(item => item.IdentityKey == key);
            if (known is null)
            {
                // Claim aliases can retain the original immutable IdentityKey after explicit revisions.
                var match = await _store.ReadAsync(admission, [], [key], cancellationToken);
                if (match.Generation != snapshot.Generation)
                {
                    throw new IntelligenceStorageException(IntelligenceStorageFailure.RevisionConflict, "Canonical claim changed during reconciliation; reload the unit.");
                }

                known = match.Items.SingleOrDefault();
            }

            if (known is not null && snapshot.Aliases.ContainsKey(known.Reference.ItemId))
            {
                known = await ResolveCurrentAsync(admission, known, snapshot.Generation, cancellationToken);
            }

            if (known is null && claims.TryGetValue(key, out var pending))
            {
                known = writes[pending.ItemId].Item;
            }

            var target = selection.Target is { } reference ? existingItems.GetValueOrDefault(reference.ItemId) : known;
            var candidateEvidence = candidate.EvidenceIds.Concat(candidate.ConflictingEvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
            if (retainedEvidence.Union(candidateEvidence).Count() > 64)
            {
                outcomes.Add(new(candidate.Key, IntelligenceReconciliationOutcome.Deferred, "Canonical evidence-unit capacity was reached; select this candidate in another bounded unit.", null, null, [], candidate));
                continue;
            }

            if (selection.Action is IntelligenceRetentionAction.Reject or IntelligenceRetentionAction.Defer)
            {
                retainedEvidence.UnionWith(candidateEvidence);
                outcomes.Add(new(candidate.Key, selection.Action == IntelligenceRetentionAction.Reject ? IntelligenceReconciliationOutcome.Rejected : IntelligenceReconciliationOutcome.Deferred, selection.Reason ?? "Host selection withheld this candidate from canonical knowledge.", null, null, candidateEvidence, selection.Action == IntelligenceRetentionAction.Defer ? candidate : null));
                continue;
            }

            if (selection.Target is { } expected && (target?.Reference != expected || snapshot.Aliases.ContainsKey(expected.ItemId)))
            {
                retainedEvidence.UnionWith(candidateEvidence);
                outcomes.Add(new(candidate.Key, IntelligenceReconciliationOutcome.Deferred, "Expected target revision is absent, changed or merged; resolve the current identity and select it explicitly.", null, null, candidateEvidence, candidate));
                continue;
            }

            var targetIsAttributed = target is not null && (HasUserCorrection(target)
                || await _store.HasAttributedSupportAsync(admission, target.Reference.ItemId, cancellationToken));
            if (targetIsAttributed && target is not null && !Equivalent(target, candidate, scope))
            {
                await ConflictAsync(target, "Proposed inference contradicts user-attributed knowledge; the correction was preserved.");
                continue;
            }

            if (known is null && selection.Action is IntelligenceRetentionAction.Consolidate or IntelligenceRetentionAction.Supersede)
            {
                var sourceIds = candidate.EvidenceIds.Select(id => evidence[id].Source.Id).ToHashSet(StringComparer.Ordinal);
                var pendingCollisions = writes.Values.Select(write => write.Item).Where(item => item.Knowledge.GetType() == Knowledge(candidate).GetType()
                    && CanonicalRecordValidation.Serialize(item.Scope) == CanonicalRecordValidation.Serialize(scope)
                    && item.SupportingEvidenceIds.Any(id => evidence.TryGetValue(id, out var excerpt) && sourceIds.Contains(excerpt.Source.Id))).ToArray();
                if (pendingCollisions.Any(item => item.Reference != selection.Target && !Equivalent(item, candidate, scope)))
                {
                    Deferred("A competing claim on the same kind/scope/sources is pending in this unit; publish it first, then make an explicit distinct-claim decision.");
                    continue;
                }

                var neighbors = await _store.ReadBySourcesAsync(admission, candidate.EvidenceIds.Select(id => evidence[id].Source.Id).Distinct(StringComparer.Ordinal).ToArray(), candidate.Kind, scope, cancellationToken);
                if (neighbors.Generation != snapshot.Generation)
                {
                    throw new IntelligenceStorageException(IntelligenceStorageFailure.RevisionConflict, "Source collision frontier changed; reload the reconciliation unit.");
                }

                var currentNeighbors = new List<IntelligenceItemRevision>();
                foreach (var neighbor in neighbors.Items)
                {
                    var current = neighbors.Aliases.ContainsKey(neighbor.Reference.ItemId)
                        ? await ResolveCurrentAsync(admission, neighbor, snapshot.Generation, cancellationToken) : neighbor;
                    if (current is not null)
                    {
                        currentNeighbors.Add(current);
                    }
                }

                var collisions = currentNeighbors.DistinctBy(item => item.Reference.ItemId).Where(item => item.Knowledge.GetType() == Knowledge(candidate).GetType()
                    && CanonicalRecordValidation.Serialize(item.Scope) == CanonicalRecordValidation.Serialize(scope)).ToArray();
                var equivalent = collisions.FirstOrDefault(item => Equivalent(item, candidate, scope));
                if (equivalent is not null && selection.Target is null)
                {
                    known = equivalent;
                    target = equivalent;
                    claims[key] = new(key, equivalent.Reference.ItemId);
                }
                else if (neighbors.HasMore || collisions.Any(item => item.Reference != selection.Target
                    && selection.ConfirmDistinctFrom?.Contains(item.Reference) != true))
                {
                    Deferred("Possible paraphrase or competing claim on the same kind/scope/sources requires a complete, explicit distinct-claim decision; no duplicate identity was added.");
                    continue;
                }
            }

            switch (selection.Action)
            {
                case IntelligenceRetentionAction.Consolidate:
                    if (target is null)
                    {
                        if (writes.Count == 16)
                        {
                            Deferred("Canonical item-unit capacity reached; select this candidate in another bounded unit.");
                            break;
                        }

                        var item = Create(candidate, scope, concepts, key, request.Provenance, interpretation.LastPacket.HistoryCoverage);
                        writes.Add(item.Reference.ItemId, new(item, 0));
                        claims[key] = new(key, item.Reference.ItemId);
                        await AcceptedAsync(IntelligenceReconciliationOutcome.Added, item, "New validated claim was added.");
                    }
                    else if (Equivalent(target, candidate, scope))
                    {
                        await AcceptedAsync(IntelligenceReconciliationOutcome.Corroborating, target, "Equivalent finding corroborated the stable revision; evaluation anchors and old citations were preserved.");
                    }
                    else
                    {
                        retainedEvidence.UnionWith(candidateEvidence);
                        outcomes.Add(new(candidate.Key, IntelligenceReconciliationOutcome.Deferred, "Claim identity overlaps existing knowledge but meaning or assessment differs; select an explicit revision or conflict.", target.Reference, null, candidateEvidence, candidate));
                    }

                    break;
                case IntelligenceRetentionAction.Revise:
                    if (target is null || writes.Count == 16 || target.Knowledge.GetType() != Knowledge(candidate).GetType() || writes.ContainsKey(target.Reference.ItemId)
                        || (known is not null && known.Reference.ItemId != target.Reference.ItemId))
                    {
                        Deferred("Revision requires one existing unmerged identity of the same kind without a competing claim identity.");
                        break;
                    }

                    if (Equivalent(target, candidate, scope))
                    {
                        await AcceptedAsync(IntelligenceReconciliationOutcome.Corroborating, target, "No substantive revision was required.");
                        break;
                    }

                    var revised = Create(candidate, scope, concepts, target.IdentityKey, request.Provenance, interpretation.LastPacket.HistoryCoverage) with
                    {
                        Reference = target.Reference with { Revision = target.Reference.Revision + 1 },
                        Created = target.Created,
                    };
                    writes.Add(target.Reference.ItemId, new(revised, target.Reference.Revision));
                    claims[key] = new(key, target.Reference.ItemId);
                    await AcceptedAsync(IntelligenceReconciliationOutcome.Revised, revised, "Explicit validated revision preserved the previous statement and provenance at its original citation.");
                    break;
                case IntelligenceRetentionAction.Merge:
                    var other = selection.MergeFrom is { } mergeReference ? existingItems.GetValueOrDefault(mergeReference.ItemId) : null;
                    var otherIsAttributed = other is not null && (HasUserCorrection(other)
                        || await _store.HasAttributedSupportAsync(admission, other.Reference.ItemId, cancellationToken));
                    if (writes.Count == 16 || target is null || other is null || other.Reference != selection.MergeFrom || target.Reference.ItemId == other.Reference.ItemId
                        || snapshot.Aliases.ContainsKey(other.Reference.ItemId) || writes.ContainsKey(other.Reference.ItemId) || writes.ContainsKey(target.Reference.ItemId)
                        || !Equivalent(target, candidate, scope) || other.Knowledge.GetType() != target.Knowledge.GetType()
                        || CanonicalRecordValidation.Serialize(other.Scope) != CanonicalRecordValidation.Serialize(target.Scope)
                        || (!Equivalent(other, candidate, scope) && string.IsNullOrWhiteSpace(selection.Reason))
                        || (otherIsAttributed && (!targetIsAttributed || other.Created.UserAttribution != target.Created.UserAttribution
                            || other.Evaluated.UserAttribution != target.Evaluated.UserAttribution)))
                    {
                        Deferred("Merge requires explicitly revision-checked, semantically equivalent scopes and claims; attributed corrections cannot be discarded.");
                        break;
                    }

                    var merged = Historical(other, IntelligenceApplicability.Superseded, request.Provenance);
                    writes.Add(other.Reference.ItemId, new(merged, other.Reference.Revision, target.Reference.ItemId));
                    await AcceptedAsync(IntelligenceReconciliationOutcome.Merged, target, selection.Reason ?? "Equivalent identities were merged; the older statement, evidence and exact revisions remain resolvable.", merged.Reference);
                    break;
                case IntelligenceRetentionAction.Supersede:
                    if (target is null || writes.Count > (known is null ? 14 : 15) || writes.ContainsKey(target.Reference.ItemId) || known?.Reference.ItemId == target.Reference.ItemId
                        || (known is not null && !Equivalent(known, candidate, scope)))
                    {
                        Deferred("Supersession requires an explicit existing target and a distinct validated replacement claim.");
                        break;
                    }

                    var replacement = known ?? Create(candidate, scope, concepts, key, request.Provenance, interpretation.LastPacket.HistoryCoverage);
                    if (known is null)
                    {
                        writes.Add(replacement.Reference.ItemId, new(replacement, 0));
                        claims[key] = new(key, replacement.Reference.ItemId);
                    }

                    var historicalApplicability = selection.PartialSupersession ? IntelligenceApplicability.PartiallySuperseded : IntelligenceApplicability.Superseded;
                    var superseded = target.Applicability == historicalApplicability ? target : Historical(target, historicalApplicability, request.Provenance);
                    if (superseded.Reference != target.Reference)
                    {
                        writes.Add(target.Reference.ItemId, new(superseded, target.Reference.Revision));
                    }

                    relationships.Add(await PreserveRelationshipAsync(admission, new(Guid.Empty, replacement.Reference, superseded.Reference, IntelligenceRelationshipKind.Supersession, selection.Reason ?? "Explicit replacement preserves the earlier claim and surviving lessons as qualified history.", Parse<IntelligenceConfidence>(candidate.Confidence), candidate.EvidenceIds, request.Provenance), cancellationToken));
                    await AcceptedAsync(IntelligenceReconciliationOutcome.Superseding, replacement, "Replacement and historical applicability were published atomically without erasing the earlier mechanism or invariant.", superseded.Reference);
                    break;
                case IntelligenceRetentionAction.Conflict:
                    await ConflictAsync(target, selection.Reason ?? "Competing explanation was retained without overwriting established knowledge.");
                    break;
                default:
                    throw new InvalidDataException("Unsupported retention transition.");
            }

            void Deferred(string reason)
            {
                retainedEvidence.UnionWith(candidateEvidence);
                outcomes.Add(new(candidate.Key, IntelligenceReconciliationOutcome.Deferred, reason, target?.Reference, null, candidateEvidence, candidate));
            }

            async Task ConflictAsync(IntelligenceItemRevision? existing, string reason)
            {
                retainedEvidence.UnionWith(candidateEvidence);
                outcomes.Add(new(candidate.Key, IntelligenceReconciliationOutcome.Conflicting, reason, existing?.Reference, null, candidateEvidence, candidate));
                if (existing is not null)
                {
                    var proposalIdentity = CanonicalRecordValidation.Serialize(candidate with
                    {
                        Key = string.Empty,
                        Title = string.Empty,
                        Statement = Normalize(candidate.Statement),
                        EvidenceIds = candidate.EvidenceIds.Order(StringComparer.Ordinal).ToArray(),
                        ConflictingEvidenceIds = candidate.ConflictingEvidenceIds.Order(StringComparer.Ordinal).ToArray(),
                    });
                    var id = StableGuid(CanonicalRecordValidation.Serialize(existing.Reference), "conflict", proposalIdentity);
                    uncertainties.Add(await _store.ReadUncertaintyAsync(admission, id, cancellationToken)
                        ?? new(id, existing.Reference, $"Compare retained proposal {candidate.Key} in reconciliation receipt {receiptId} with this knowledge revision; resolve the competing evidence or clarify intent.", candidateEvidence, request.Provenance));
                }
            }

            async Task AcceptedAsync(IntelligenceReconciliationOutcome outcome, IntelligenceItemRevision item, string reason, IntelligenceRevisionReference? related = null)
            {
                retainedEvidence.UnionWith(candidateEvidence);
                outcomes.Add(new(candidate.Key, outcome, reason, item.Reference, related, candidateEvidence, SupportingEvidenceIds: candidate.EvidenceIds));
                if (candidate.Uncertainty is { } uncertainty)
                {
                    var id = StableGuid(CanonicalRecordValidation.Serialize(item.Reference), uncertainty, CanonicalRecordValidation.Serialize(candidateEvidence.Order(StringComparer.Ordinal).ToArray()));
                    uncertainties.Add(await _store.ReadUncertaintyAsync(admission, id, cancellationToken)
                        ?? new(id, item.Reference, uncertainty, candidateEvidence, request.Provenance));
                }
            }
        }

        var accepted = outcomes.Where(outcome => outcome.Outcome is IntelligenceReconciliationOutcome.Added or IntelligenceReconciliationOutcome.Corroborating
            or IntelligenceReconciliationOutcome.Revised or IntelligenceReconciliationOutcome.Merged or IntelligenceReconciliationOutcome.Superseding).ToDictionary(outcome => outcome.CandidateKey, StringComparer.Ordinal);
        foreach (var proposal in interpretation.Response.Relationships)
        {
            if (!accepted.TryGetValue(proposal.From, out var from) || !accepted.TryGetValue(proposal.To, out var to)
                || from.Item is not { } fromReference || to.Item is not { } toReference || fromReference.ItemId == toReference.ItemId
                || relationships.Count == 32 || retainedEvidence.Union(proposal.EvidenceIds).Count() > 64)
            {
                if (diagnostics.Count < 30)
                {
                    diagnostics.Add($"Relationship {proposal.From}->{proposal.To} was deferred because endpoints or unit capacity were not accepted.");
                }

                continue;
            }

            retainedEvidence.UnionWith(proposal.EvidenceIds);
            relationships.Add(await PreserveRelationshipAsync(admission, new(Guid.Empty, fromReference, toReference, Parse<IntelligenceRelationshipKind>(proposal.Kind), proposal.Explanation, Parse<IntelligenceConfidence>(proposal.Confidence), proposal.EvidenceIds, request.Provenance, proposal.RationaleQuote), cancellationToken));
        }

        var episodes = new List<IntelligenceEpisode>();
        foreach (var proposal in interpretation.Episodes ?? interpretation.LastPacket.Episodes)
        {
            var derived = accepted.Values.Where(outcome => outcome.EvidenceIds.Intersect(proposal.EvidenceIds, StringComparer.Ordinal).Any()).Select(outcome => outcome.Item).OfType<IntelligenceRevisionReference>().Distinct().ToArray();
            if (derived.Length == 0)
            {
                continue;
            }

            if (episodes.Count == 16 || retainedEvidence.Union(proposal.EvidenceIds).Count() > 64)
            {
                if (diagnostics.Count < 30)
                {
                    diagnostics.Add("Episode association was deferred because this publication unit reached its episode or evidence capacity.");
                }

                continue;
            }

            var prior = await _store.ReadEpisodeAsync(admission, proposal.Id, cancellationToken);
            var itemReferences = (prior?.Items ?? []).Concat(derived).Distinct().ToArray();
            var evidenceIds = (prior?.EvidenceIds ?? []).Concat(proposal.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray();
            if (itemReferences.Length > 32 || evidenceIds.Length > 64)
            {
                if (diagnostics.Count < 30)
                {
                    diagnostics.Add("Episode association capacity reached; prior episode was preserved.");
                }

                continue;
            }

            retainedEvidence.UnionWith(proposal.EvidenceIds);
            episodes.Add(new(proposal.Id, proposal.Scope, proposal.Commits, evidenceIds, itemReferences, prior?.Provenance ?? request.Provenance));
        }

        var receipt = new IntelligenceReconciliationReceipt(receiptId, fingerprint, request.Provenance, outcomes, diagnostics.Distinct(StringComparer.Ordinal).Take(32).ToArray());
        var publication = new IntelligencePublication(snapshot.Generation, writes.Values.ToArray(), retainedEvidence.Select(id => evidence[id]).ToArray(), episodes, relationships, uncertainties, Receipt: receipt, Claims: claims.Values.ToArray());
        try
        {
            return CanonicalRecordValidation.Freeze(publication, interpretation.LastPacket.Target);
        }
        catch (InvalidDataException)
        {
            return DeferredUnit(snapshot.Generation, receiptId, fingerprint, request, "The complete canonical publication exceeded its record or serialized byte capacity; select a smaller unit with a new UnitId. No knowledge was changed.");
        }
    }

    private static IntelligencePublication DeferredUnit(long generation, string receiptId, string fingerprint, IntelligenceReconciliationRequest request, string reason)
    {
        var deferred = request.Selections.Select(selection => new IntelligenceCandidateOutcome(selection.CandidateKey, IntelligenceReconciliationOutcome.Deferred, reason, null, null, [])).ToArray();
        return new(generation, [], [], [], [], [], Receipt: new(receiptId, fingerprint, request.Provenance, deferred, []));
    }

    private async Task<IntelligenceRelationship> PreserveRelationshipAsync(RepositoryIntelligenceAdmission admission, IntelligenceRelationship proposed, CancellationToken token)
    {
        var identity = CanonicalRecordValidation.Serialize(new
        {
            proposed.From,
            proposed.To,
            proposed.Kind,
            proposed.Explanation,
            proposed.Confidence,
            EvidenceIds = proposed.EvidenceIds.Order(StringComparer.Ordinal).ToArray(),
            proposed.RationaleQuote,
        });
        var id = StableGuid(identity);
        return await _store.ReadRelationshipAsync(admission, id, token) ?? proposed with { Id = id };
    }

    private static void Validate(RepositoryInterpretationResult interpretation, IntelligenceReconciliationRequest request)
    {
        _ = Scope(interpretation);
        var evidence = interpretation.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);
        RepositoryInterpreter.Validate(interpretation.Response, interpretation.OperationId, evidence, interpretation.LastPacket.Target.Commit);
        var target = interpretation.LastPacket.Target;
        var scope = Scope(interpretation);
        CanonicalRecordValidation.Scope(scope);
        if (interpretation.Response.Relationships.Select(item => (item.From, item.To, item.Kind)).Distinct().Count() != interpretation.Response.Relationships.Count)
        {
            throw new InvalidDataException("A retention unit cannot publish repeated relationship endpoint/kind identities.");
        }

        foreach (var proposal in interpretation.Response.Relationships)
        {
            var relationship = new IntelligenceRelationship(Guid.NewGuid(), new(Guid.NewGuid(), 1), new(Guid.NewGuid(), 1), Parse<IntelligenceRelationshipKind>(proposal.Kind), proposal.Explanation, Parse<IntelligenceConfidence>(proposal.Confidence), proposal.EvidenceIds, request.Provenance, proposal.RationaleQuote);
            _ = CanonicalRecordValidation.Freeze(new(0, [], [], [], [relationship], []), target);
        }

        foreach (var item in evidence.Values)
        {
            _ = CanonicalRecordValidation.Freeze(new(0, [], [item], [], [], []), target);
            var source = item.Source;
            CanonicalRecordValidation.Path(source.Path, allowRoot: true);
            if (source.RepositoryIdentity != target.RepositoryIdentity || source.CheckoutIdentity != target.CheckoutIdentity
                || source.Id != RepositoryEvidenceIdentity.SourceId(source)
                || item.Id != RepositoryEvidenceIdentity.ExcerptId(source, item.State, item.Text, item.StartLine, item.EndLine)
                || source.Kind is not ("Document" or "File" or "Configuration" or "TestSource" or "CommitMetadata" or "DiffMetadata" or "WorkingTreeOverlay")
                || (source.Kind == "WorkingTreeOverlay" ? source.Revision is not null || !Hash(source.SourceIdentity, 64) : !Commit(source.Revision))
                || (source.Kind == "CommitMetadata" && (source.Path != "." || source.SourceIdentity != source.Revision))
                || (source.Kind == "DiffMetadata" && source.SourceIdentity != source.Revision + ":" + source.PreviousRevision)
                || (source.Kind is "Document" or "File" or "Configuration" or "TestSource" && !Commit(source.SourceIdentity))
                || (source.PreviousRevision is not null && !Commit(source.PreviousRevision))
                || (source.Path != "." && !Within(source.Path, interpretation.LastPacket.Selection.Paths)
                    && !(source.Kind == "DiffMetadata" && source.PreviousPath is { } previous && Within(previous, interpretation.LastPacket.Selection.Paths)))
                || ((item.StartLine is null) != (item.EndLine is null)) || item.StartLine is < 1 || item.EndLine < item.StartLine)
            {
                throw new InvalidDataException("Evidence identity, locator or repository context is unsupported.");
            }

            if (source.PreviousPath is { } previousPath)
            {
                CanonicalRecordValidation.Path(previousPath, allowRoot: true);
            }
        }

        var touched = new HashSet<Guid>();
        foreach (var selection in request.Selections)
        {
            var candidate = interpretation.Response.Candidates.SingleOrDefault(item => item.Key == selection.CandidateKey)
                ?? throw new InvalidDataException("Retention references an unknown candidate.");
            var canonical = Create(candidate, scope, [], ClaimKey(candidate, scope), request.Provenance, interpretation.LastPacket.HistoryCoverage);
            _ = CanonicalRecordValidation.Freeze(new(0, [new(canonical, 0)], [], [], [], []), target);
            if ((selection.Action is IntelligenceRetentionAction.Revise or IntelligenceRetentionAction.Merge or IntelligenceRetentionAction.Supersede && selection.Target is null)
                || (selection.Action == IntelligenceRetentionAction.Merge && selection.MergeFrom is null)
                || (selection.Action != IntelligenceRetentionAction.Merge && selection.MergeFrom is not null)
                || (selection.PartialSupersession && selection.Action != IntelligenceRetentionAction.Supersede))
            {
                throw new InvalidDataException("Retention action has missing or inapplicable host targets.");
            }

            foreach (var reference in new[] { selection.Target, selection.MergeFrom }.OfType<IntelligenceRevisionReference>())
            {
                if (reference.ItemId == Guid.Empty || reference.Revision < 1
                    || (selection.Action is IntelligenceRetentionAction.Revise or IntelligenceRetentionAction.Merge or IntelligenceRetentionAction.Supersede && !touched.Add(reference.ItemId)))
                {
                    throw new InvalidDataException("Retention unit overlaps mutation targets or uses an invalid revision.");
                }
            }

            if (selection.ConfirmDistinctFrom?.Any(reference => reference.ItemId == Guid.Empty || reference.Revision < 1) == true)
            {
                throw new InvalidDataException("Distinct-claim decisions require exact existing revisions.");
            }

            if (candidate.RationaleQuote is { } quote && !candidate.EvidenceIds.Select(id => evidence[id]).Any(item => item.Source.Kind is "Document" or "CommitMetadata" && item.Text?.Contains(quote, StringComparison.Ordinal) == true))
            {
                throw new InvalidDataException("Unsupported quoted rationale.");
            }
        }

        if (interpretation.Episodes?.Count > 512)
        {
            throw new InvalidDataException("Collected episode retention exceeds its bounded interpretation allowance.");
        }

        foreach (var episode in interpretation.Episodes ?? interpretation.LastPacket.Episodes)
        {
            var expected = RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { scope = episode.Scope, commits = episode.Commits }));
            _ = CanonicalRecordValidation.Freeze(new(0, [], [], [new(episode.Id, episode.Scope, episode.Commits, episode.EvidenceIds, [], request.Provenance)], [], []), target);
            if (episode.Id != expected || episode.Commits.Any(commit => !Commit(commit)) || episode.EvidenceIds.Any(id => !evidence.ContainsKey(id)))
            {
                throw new InvalidDataException("Unsupported episode constituents or evidence.");
            }
        }
    }

    private async Task<IntelligenceItemRevision?> ResolveCurrentAsync(RepositoryIntelligenceAdmission admission, IntelligenceItemRevision item, long generation, CancellationToken token)
    {
        var currentId = await _store.ResolveAliasAsync(admission, item.Reference.ItemId, token);
        var current = await _store.ReadAsync(admission, [currentId], [], token);
        if (current.Generation != generation)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.RevisionConflict, "Canonical alias changed during reconciliation; reload the unit.");
        }

        return current.Items.SingleOrDefault();
    }

    private static IntelligenceScope Scope(RepositoryInterpretationResult interpretation)
    {
        var paths = interpretation.LastPacket.Selection.Paths.Contains(".", StringComparer.Ordinal)
            ? [] : interpretation.LastPacket.Selection.Paths.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            CanonicalRecordValidation.Path(path, allowRoot: true);
        }

        return new IntelligenceScope(paths.Length == 0 || paths.Contains(".", StringComparer.Ordinal), paths, [], []);
    }

    private static bool Within(string path, IReadOnlyList<string> scopes) => scopes.Count == 0 || scopes.Any(scope => scope == "." || path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal));

    private static bool Hash(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    private static bool Commit(string? value) => Hash(value, 40) || Hash(value, 64);

    private static T Parse<T>(string value)
        where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new InvalidDataException("Unsupported canonical assessment value.");

    private static string Normalize(string value) => value.Trim();

    private static string ClaimKey(RepositoryInterpretationCandidate candidate, IntelligenceScope scope)
        => RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { candidate.Kind, Statement = Normalize(candidate.Statement), Scope = scope }));

    private static bool Equivalent(IntelligenceItemRevision item, RepositoryInterpretationCandidate candidate, IntelligenceScope scope)
        => Normalize(item.Statement) == Normalize(candidate.Statement)
            && CanonicalRecordValidation.Serialize(item.Knowledge) == CanonicalRecordValidation.Serialize(Knowledge(candidate))
            && CanonicalRecordValidation.Serialize(item.Scope) == CanonicalRecordValidation.Serialize(scope)
            && item.Applicability == Parse<IntelligenceApplicability>(candidate.Applicability)
            && item.Confidence == Parse<IntelligenceConfidence>(candidate.Confidence)
            && item.EvidenceClass == Parse<IntelligenceEvidenceClass>(candidate.EvidenceClass)
            && item.Uncertainty == candidate.Uncertainty
            && item.AtTarget == candidate.AtTarget
            && item.CausalSupport == new IntelligenceCausalSupport(candidate.CausalClaim, candidate.RationaleQuote);

    private static bool HasUserCorrection(IntelligenceItemRevision item) => item.Created.UserAttribution is not null || item.Evaluated.UserAttribution is not null;

    private static IntelligenceKnowledge Knowledge(RepositoryInterpretationCandidate candidate) => candidate.Kind switch
    {
        "Decision" => new DecisionKnowledge(candidate.RationaleQuote, [], candidate.Then, candidate.Replacement),
        "Constraint" => new ConstraintKnowledge(candidate.RationaleQuote, candidate.Then, candidate.Replacement),
        "Convention" => new ConventionKnowledge(candidate.RationaleQuote, candidate.Then, candidate.Replacement),
        "Migration" => new MigrationKnowledge(candidate.Then, candidate.Replacement, null),
        "Reversal" => new ReversalKnowledge(candidate.Then, candidate.Replacement, null),
        "HistoricalFailure" => new HistoricalFailureKnowledge(null, candidate.Replacement, null, candidate.Then),
        "PersistentPattern" => new PersistentPatternKnowledge(null, null, candidate.Then, candidate.Replacement),
        "OpenTension" => new OpenTensionKnowledge(null, [], candidate.Then, candidate.Replacement),
        _ => throw new InvalidDataException("Unsupported canonical item kind."),
    };

    private static IntelligenceItemRevision Create(RepositoryInterpretationCandidate candidate, IntelligenceScope scope, IReadOnlyList<string> concepts, string key, IntelligenceProvenance provenance, string coverage)
    {
        var applicability = Parse<IntelligenceApplicability>(candidate.Applicability);
        return new(new(Guid.NewGuid(), 1), key, candidate.Title, candidate.Statement, Knowledge(candidate), applicability, Parse<IntelligenceConfidence>(candidate.Confidence), Parse<IntelligenceEvidenceClass>(candidate.EvidenceClass), provenance.Target.Commit is null ? IntelligenceFreshness.Unknown : IntelligenceFreshness.Evaluated, coverage, provenance.Target.Commit, scope, concepts, candidate.EvidenceIds, candidate.ConflictingEvidenceIds, candidate.Uncertainty, provenance, provenance, Capsule(candidate.Statement, applicability, candidate.Confidence, candidate.EvidenceClass, candidate.Uncertainty), candidate.AtTarget, new(candidate.CausalClaim, candidate.RationaleQuote));
    }

    private static IntelligenceItemRevision Historical(IntelligenceItemRevision item, IntelligenceApplicability applicability, IntelligenceProvenance provenance)
        => item with
        {
            Reference = item.Reference with { Revision = item.Reference.Revision + 1 },
            Applicability = applicability,
            Evaluated = provenance with { UserAttribution = provenance.UserAttribution ?? item.Evaluated.UserAttribution ?? item.Created.UserAttribution },
            LastEvaluatedCommit = provenance.Target.Commit,
            Freshness = provenance.Target.Commit is null ? IntelligenceFreshness.Unknown : IntelligenceFreshness.Evaluated,
            Capsule = Capsule(item.Statement, applicability, item.Confidence.ToString(), item.EvidenceClass.ToString(), item.Uncertainty),
        };

    private static string? Capsule(string statement, IntelligenceApplicability applicability, string confidence, string evidenceClass, string? uncertainty)
    {
        var prefix = applicability switch
        {
            IntelligenceApplicability.Current => string.Empty,
            IntelligenceApplicability.PartiallySuperseded => "Partially superseded history: ",
            IntelligenceApplicability.Uncertain => "Applicability uncertain: ",
            _ => "Historical guidance: ",
        };
        var capsule = $"{prefix}{statement} [Confidence: {confidence}; evidence: {evidenceClass}.]";
        if (uncertainty is not null)
        {
            capsule += " Uncertainty: " + uncertainty;
        }

        // Never truncate away meaning or material uncertainty. Oversized items stay inspectable without a capsule.
        return capsule.Length <= 1024 ? capsule : null;
    }

    private static Guid StableGuid(params string[] fields) => new(Convert.FromHexString(RepositoryProfileCollector.Digest(JsonSerializer.Serialize(fields))).AsSpan(0, 16));
}
