namespace Threadsmith.RepositoryIntelligence;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Physical bounds and context checks shared by storage and reconciliation; no semantic deduplication.</summary>
internal static class CanonicalRecordValidation
{
    /// <summary>Strict versioned host-owned payload decoding.</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
    };

    /// <summary>Detaches caller collections and bounds serialized records before database work.</summary>
    internal static IntelligencePublication Freeze(IntelligencePublication publication, GitSnapshotMetadata context)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.ExpectedGeneration < 0 || publication.Items.Count > 16 || publication.Evidence.Count > 64
            || publication.Relationships.Count > 32 || publication.Episodes.Count > 16 || publication.Uncertainties.Count > 32)
        {
            throw new InvalidDataException("Publication exceeds canonical record limits.");
        }

        foreach (var write in publication.Items)
        {
            var item = write.Item;
            Reference(item.Reference);
            if (write.ExpectedRevision < 0 || item.Reference.Revision != write.ExpectedRevision + 1 || write.AliasTo == item.Reference.ItemId)
            {
                throw new InvalidDataException("Invalid revision or self alias.");
            }

            Text(item.IdentityKey, 64);
            Text(item.Title, 256);
            Text(item.Statement, 2048);
            Text(item.Coverage, 2048);
            Optional(item.Capsule, 1024);
            Optional(item.Uncertainty, 1024);
            Optional(item.AtTarget, 1024);
            Optional(item.CausalSupport?.ExactQuote, 1024);
            if (!Enum.IsDefined(item.Applicability) || !Enum.IsDefined(item.Confidence)
                || !Enum.IsDefined(item.EvidenceClass) || !Enum.IsDefined(item.Freshness))
            {
                throw new InvalidDataException("Unknown canonical assessment dimension.");
            }

            Scope(item.Scope);
            _ = MemoryConcepts.Normalize(item.Concepts);
            Strings(item.SupportingEvidenceIds, 64, 64);
            Strings(item.ConflictingEvidenceIds, 64, 64);
            if (item.SupportingEvidenceIds.Count == 0)
            {
                throw new InvalidDataException("Canonical intelligence requires supporting evidence.");
            }

            Provenance(item.Created, context);
            Provenance(item.Evaluated, context);
            if (item.LastEvaluatedCommit != item.Evaluated.Target.Commit)
            {
                throw new InvalidDataException("Evaluation anchor differs from its provenance.");
            }

            Knowledge(item.Knowledge);
            _ = Serialize(item);
        }

        foreach (var evidence in publication.Evidence)
        {
            Text(evidence.Id, 64);
            Text(evidence.Source.Id, 64);
            Text(evidence.Source.Kind, 64);
            Text(evidence.Source.SourceIdentity, 512);
            Text(evidence.State, 128);
            Path(evidence.Source.Path, allowRoot: true);
            Optional(evidence.Text, 32768);
            if (evidence.Source.RepositoryIdentity != context.RepositoryIdentity || evidence.Source.CheckoutIdentity != context.CheckoutIdentity
                || ((evidence.StartLine is null) != (evidence.EndLine is null))
                || evidence.StartLine is < 1 || evidence.EndLine < evidence.StartLine)
            {
                throw new InvalidDataException("Evidence context or range is invalid.");
            }

            Optional(evidence.Source.Revision, 64);
            Optional(evidence.Source.PreviousRevision, 64);
            if (evidence.Source.PreviousPath is { } previous)
            {
                Path(previous, allowRoot: true);
            }

            _ = Serialize(evidence);
        }

        foreach (var episode in publication.Episodes)
        {
            Text(episode.Id, 64);
            Path(episode.Scope, allowRoot: true);
            Strings(episode.Commits, 100, 64);
            Strings(episode.EvidenceIds, 64, 64);
            if (episode.Items.Count > 32)
            {
                throw new InvalidDataException("Too many episode items.");
            }

            foreach (var reference in episode.Items)
            {
                Reference(reference);
            }

            Provenance(episode.Provenance, context);
            _ = Serialize(episode);
        }

        foreach (var relationship in publication.Relationships)
        {
            Reference(relationship.From);
            Reference(relationship.To);
            Text(relationship.Explanation, 1024);
            Optional(relationship.RationaleQuote, 1024);
            Strings(relationship.EvidenceIds, 64, 64);
            if (relationship.Id == Guid.Empty || relationship.From.ItemId == relationship.To.ItemId
                || !Enum.IsDefined(relationship.Kind) || !Enum.IsDefined(relationship.Confidence))
            {
                throw new InvalidDataException("Invalid canonical relationship.");
            }

            Provenance(relationship.Provenance, context);
            _ = Serialize(relationship);
        }

        foreach (var uncertainty in publication.Uncertainties)
        {
            Reference(uncertainty.Item);
            Text(uncertainty.Question, 1024);
            Strings(uncertainty.EvidenceIds, 64, 64);
            if (uncertainty.Id == Guid.Empty)
            {
                throw new InvalidDataException("Uncertainty identity is empty.");
            }

            Provenance(uncertainty.Provenance, context);
            _ = Serialize(uncertainty);
        }

        if (publication.Continuation is { } continuation)
        {
            Text(continuation.UnitId, 128);
            Text(continuation.Scope, 2048);
            Text(continuation.State, 128);
            Optional(continuation.Cursor, 4096);
            Provenance(continuation.Provenance, context);
        }

        foreach (var claim in publication.Claims ?? [])
        {
            Text(claim.Key, 64);
            if (claim.ItemId == Guid.Empty || publication.Claims?.Count > 32)
            {
                throw new InvalidDataException("Canonical claim aliases exceed their bounds.");
            }
        }

        if (publication.Receipt is { } receipt)
        {
            Text(receipt.Id, 64);
            Text(receipt.Fingerprint, 64);
            Provenance(receipt.Provenance, context);
            Strings(receipt.Diagnostics, 32, 1024);
            if (receipt.Outcomes.Count > 16)
            {
                throw new InvalidDataException("Too many reconciliation outcomes.");
            }

            foreach (var outcome in receipt.Outcomes)
            {
                Text(outcome.CandidateKey, 64);
                Text(outcome.Reason, 1024);
                Strings(outcome.EvidenceIds, 64, 64);
                Strings(outcome.SupportingEvidenceIds ?? [], 64, 64);
                if (outcome.SupportingEvidenceIds?.Count > 0 && (outcome.Item is null
                    || outcome.SupportingEvidenceIds.Except(outcome.EvidenceIds, StringComparer.Ordinal).Any()
                    || outcome.Outcome is not (IntelligenceReconciliationOutcome.Added or IntelligenceReconciliationOutcome.Corroborating
                        or IntelligenceReconciliationOutcome.Revised or IntelligenceReconciliationOutcome.Merged or IntelligenceReconciliationOutcome.Superseding)))
                {
                    throw new InvalidDataException("Receipt supporting associations require an accepted exact revision and retained evidence.");
                }

                if (!Enum.IsDefined(outcome.Outcome))
                {
                    throw new InvalidDataException("Unknown reconciliation outcome.");
                }

                if (outcome.Item is { } item)
                {
                    Reference(item);
                }

                if (outcome.RelatedItem is { } related)
                {
                    Reference(related);
                }
            }

            _ = Serialize(receipt, 262144);
        }

        var json = JsonSerializer.Serialize(publication, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 512 * 1024)
        {
            throw new InvalidDataException("Publication exceeds its serialized byte ceiling.");
        }

        return JsonSerializer.Deserialize<IntelligencePublication>(json, JsonOptions)
            ?? throw new InvalidDataException("Publication is empty.");
    }

    /// <summary>Bounds one record including JSON escaping before retention.</summary>
    internal static string Serialize<T>(T value, int maximumBytes = 65536)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
        {
            throw new InvalidDataException("Canonical record exceeds its serialized byte ceiling.");
        }

        return json;
    }

    /// <summary>Validates explicit scope with no glob execution or invented semantic anchors.</summary>
    internal static void Scope(IntelligenceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Strings(scope.Paths, 32, 2048);
        Strings(scope.Modules, 16, 256);
        Strings(scope.Symbols, 32, 512);
        foreach (var path in scope.Paths)
        {
            Path(path, allowRoot: true);
        }
    }

    /// <summary>Validates a relative source locator without opening it.</summary>
    internal static void Path(string value, bool allowRoot = false)
    {
        Text(value, 2048);
        if (System.IO.Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':')
            || value.Split('/').Any(segment => segment is ".." or "" || (!allowRoot && segment == ".")))
        {
            throw new InvalidDataException("Canonical locator must be a normalized repository-relative path.");
        }
    }

    /// <summary>Rejects unknown context while permitting historical commits within the same local repository.</summary>
    internal static void Provenance(IntelligenceProvenance provenance, GitSnapshotMetadata context)
    {
        Text(provenance.OperationId, 128);
        Text(provenance.Method, 128);
        Optional(provenance.UserAttribution, 256);
        if (provenance.Target.RepositoryIdentity != context.RepositoryIdentity || provenance.Target.CheckoutIdentity != context.CheckoutIdentity)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Canonical provenance belongs to another repository context.");
        }

        _ = Serialize(provenance);
    }

    /// <summary>Bounds nonempty text; repository content never grants authority.</summary>
    internal static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new InvalidDataException("Canonical text is empty or exceeds its bound.");
        }
    }

    /// <summary>Bounds optional text while preserving unknown values.</summary>
    internal static void Optional(string? value, int maximum)
    {
        if (value is not null)
        {
            Text(value, maximum);
        }
    }

    /// <summary>Bounds and rejects duplicate references before serialization or SQL.</summary>
    internal static void Strings(IReadOnlyList<string> values, int count, int length)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > count || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException("Canonical reference set exceeds its bound or repeats a reference.");
        }

        foreach (var value in values)
        {
            Text(value, length);
        }
    }

    private static void Reference(IntelligenceRevisionReference reference)
    {
        if (reference.ItemId == Guid.Empty || reference.Revision < 1)
        {
            throw new InvalidDataException("Canonical citation requires a host identity and positive revision.");
        }
    }

    private static void Knowledge(IntelligenceKnowledge knowledge)
    {
        switch (knowledge)
        {
            case DecisionKnowledge decision:
                Optional(decision.Rationale, 1024);
                Optional(decision.EarlierChoice, 1024);
                Optional(decision.Replacement, 1024);
                Strings(decision.Alternatives, 8, 512);
                break;
            case ConstraintKnowledge constraint:
                Optional(constraint.Rationale, 1024);
                Optional(constraint.EarlierForm, 1024);
                Optional(constraint.Replacement, 1024);
                break;
            case ConventionKnowledge convention:
                Optional(convention.Rationale, 1024);
                Optional(convention.EarlierForm, 1024);
                Optional(convention.Replacement, 1024);
                break;
            case MigrationKnowledge migration:
                Optional(migration.Original, 1024);
                Optional(migration.Replacement, 1024);
                Optional(migration.SurvivingInvariant, 1024);
                break;
            case ReversalKnowledge reversal:
                Optional(reversal.Original, 1024);
                Optional(reversal.Replacement, 1024);
                Optional(reversal.Lesson, 1024);
                break;
            case HistoricalFailureKnowledge failure:
                Optional(failure.Impact, 1024);
                Optional(failure.Resolution, 1024);
                Optional(failure.Lesson, 1024);
                Optional(failure.FailedApproach, 1024);
                break;
            case PersistentPatternKnowledge pattern:
                Optional(pattern.HistoricalBounds, 1024);
                Optional(pattern.Invariant, 1024);
                Optional(pattern.EarlierForm, 1024);
                Optional(pattern.Replacement, 1024);
                break;
            case OpenTensionKnowledge tension:
                Optional(tension.Question, 1024);
                Strings(tension.Explanations, 8, 1024);
                Optional(tension.PriorExplanation, 1024);
                Optional(tension.ProposedResolution, 1024);
                break;
            default:
                throw new InvalidDataException("Unsupported knowledge kind.");
        }
    }
}
