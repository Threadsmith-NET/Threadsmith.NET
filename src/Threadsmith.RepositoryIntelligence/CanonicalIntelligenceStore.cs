namespace Threadsmith.RepositoryIntelligence;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;

/// <summary>Repository-local canonical storage, resolved only by explicitly admitted persistent work.</summary>
internal sealed class CanonicalIntelligenceStore
{
    private readonly RepositoryIntelligenceFeature _feature;
    private readonly string _root;
    private readonly string _connectionString;
    private readonly GitSnapshotMetadata _context;

    private CanonicalIntelligenceStore(RepositoryIntelligenceFeature feature, string root, GitSnapshotMetadata context)
    {
        _feature = feature;
        _root = System.IO.Path.GetFullPath(root);
        _context = context;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = VerifyPath(_root),
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 1,
        }.ToString();
    }

    /// <summary>Opens and migrates under trusted persistent authority; ordinary startup never calls this factory.</summary>
    internal static Task<CanonicalIntelligenceStore> OpenAsync(
        RepositoryIntelligenceFeature feature,
        RepositoryIntelligenceAdmission admission,
        string repositoryRoot,
        GitSnapshotMetadata context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(context);
        if (RepositoryIdentity.Create(repositoryRoot) != admission.RepositoryIdentity
            || context.CheckoutIdentity != admission.RepositoryIdentity)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Storage must belong to the admitted checkout.");
        }

        return feature.PersistAsync(
            admission,
            async token =>
        {
            try
            {
                var store = new CanonicalIntelligenceStore(feature, repositoryRoot, context);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(VerifyPath(store._root))
                    ?? throw new InvalidOperationException("Canonical storage directory is unavailable."));
                _ = VerifyPath(store._root);
                var runner = new MigrationRunner(
                    store._connectionString,
                    [new InitialSchemaMigration(), new CanonicalSchema(), new ReconciliationSchema()],
                    managedMemoryPolicy: false,
                    commitAsync: async (migrationTransaction, commitToken) => await feature.CommitAsync(
                        admission,
                        () =>
                        {
                            commitToken.ThrowIfCancellationRequested();
                            migrationTransaction.Commit();
                            return 0;
                        },
                        commitToken));
                if (await runner.ReadCurrentVersionAsync(token) > 2)
                {
                    throw new IntelligenceStorageException(IntelligenceStorageFailure.MigrationFailed, "Canonical storage uses a newer unsupported schema.");
                }

                try
                {
                    await runner.RunAsync(token);
                }
                catch (SqliteException exception) when (!token.IsCancellationRequested && exception.SqliteErrorCode is not (11 or 26))
                {
                    throw new IntelligenceStorageException(IntelligenceStorageFailure.MigrationFailed, "Canonical schema migration failed; previous committed data was preserved.");
                }

                await using var connection = await store.ConnectAsync(token);
                await using var transaction = connection.BeginTransaction(deferred: false);
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_context VALUES(1,$repository,$checkout,0);", token, ("$repository", context.RepositoryIdentity), ("$checkout", context.CheckoutIdentity));
                await store.GenerationAsync(connection, token);
                token.ThrowIfCancellationRequested();
                await store.CommitAsync(admission, transaction, token);
                return store;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException && !token.IsCancellationRequested)
            {
                throw Classify(exception);
            }
        },
            cancellationToken);
    }

    /// <summary>Reads requested exact revisions or current identities without preloading the graph.</summary>
    internal Task<IntelligenceReadSnapshot> ReadAsync(
        RepositoryIntelligenceAdmission admission,
        IReadOnlyList<Guid> ids,
        IReadOnlyList<string> identityKeys,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count > 32 || identityKeys.Count > 32 || ids.Contains(Guid.Empty))
        {
            throw new InvalidDataException("Canonical lookup exceeds its identifier ceiling.");
        }

        CanonicalRecordValidation.Strings(identityKeys, 32, 64);
        var detachedIds = ids.Distinct().ToArray();
        var detachedKeys = identityKeys.ToArray();
        return AccessAsync(
            admission,
            async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            var generation = await GenerationAsync(connection, token);
            var parameters = new List<(string, object?)>();
            var idParameters = detachedIds.Select((id, index) =>
            {
                var name = "$id" + index.ToString(CultureInfo.InvariantCulture);
                parameters.Add((name, id));
                return name;
            }).ToArray();
            var keyParameters = detachedKeys.Select((key, index) =>
            {
                var name = "$key" + index.ToString(CultureInfo.InvariantCulture);
                parameters.Add((name, key));
                return name;
            }).ToArray();
            var sql1 = $"""
                SELECT r.body,c.text,h.alias_to FROM intelligence_items h
                JOIN intelligence_revisions r ON r.item_id=h.id AND r.revision=h.revision
                LEFT JOIN intelligence_capsules c ON c.item_id=r.item_id AND c.revision=r.revision
                WHERE h.id IN ({string.Join(',', idParameters)})
                    OR h.id IN (SELECT item_id FROM intelligence_claims WHERE key IN ({string.Join(',', keyParameters)}))
                ORDER BY h.id LIMIT 64;
                """;
            await using var command = Command(connection, sql1, [.. parameters]);
            await using var reader = await command.ExecuteReaderAsync(token);
            var items = new List<IntelligenceItemRevision>();
            var aliases = new Dictionary<Guid, Guid>();
            var bytes = 0;
            while (await reader.ReadAsync(token))
            {
                var item = Decode<IntelligenceItemRevision>(reader.GetString(0), ref bytes);
                item = item with { Capsule = await reader.IsDBNullAsync(1, token) ? null : reader.GetString(1) };
                items.Add(item);
                if (!await reader.IsDBNullAsync(2, token))
                {
                    aliases.Add(item.Reference.ItemId, Guid.Parse(reader.GetString(2)));
                }
            }

            return new IntelligenceReadSnapshot(generation, items, aliases);
        },
            cancellationToken);
    }

    /// <summary>Resolves an old citation at its original revision, independently of any current merge alias.</summary>
    internal Task<IntelligenceItemRevision?> ReadRevisionAsync(
        RepositoryIntelligenceAdmission admission,
        IntelligenceRevisionReference reference,
        CancellationToken cancellationToken = default)
    {
        return AccessAsync<IntelligenceItemRevision?>(
            admission,
            async (connection, token) =>
        {
            await GenerationAsync(connection, token);
            var sql2 = """
                SELECT r.body,c.text FROM intelligence_revisions r
                LEFT JOIN intelligence_capsules c ON c.item_id=r.item_id AND c.revision=r.revision
                WHERE r.item_id=$id AND r.revision=$revision;
                """;
            await using var command = Command(connection, sql2, ("$id", reference.ItemId), ("$revision", reference.Revision));
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return null;
            }

            var bytes = 0;
            return Decode<IntelligenceItemRevision>(reader.GetString(0), ref bytes) with { Capsule = await reader.IsDBNullAsync(1, token) ? null : reader.GetString(1) };
        },
            cancellationToken);
    }

    /// <summary>Finds a bounded collision frontier over the same normalized source references, never a graph preload.</summary>
    internal Task<IntelligenceReadSnapshot> ReadBySourcesAsync(
        RepositoryIntelligenceAdmission admission,
        IReadOnlyList<string> sourceIds,
        string kind,
        IntelligenceScope scope,
        CancellationToken cancellationToken = default)
    {
        CanonicalRecordValidation.Strings(sourceIds, 64, 64);
        CanonicalRecordValidation.Text(kind, 64);
        CanonicalRecordValidation.Scope(scope);
        var sources = sourceIds.ToArray();
        return AccessAsync(
            admission,
            async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: true);
                var generation = await GenerationAsync(connection, token);
                var parameters = sources.Select((id, index) => (Name: "$source" + index.ToString(CultureInfo.InvariantCulture), Value: (object?)id)).ToArray();
                var sql = $"""
                    SELECT DISTINCT r.body,c.text,h.alias_to FROM intelligence_evidence e INDEXED BY intelligence_evidence_source
                    JOIN intelligence_claim_support i INDEXED BY intelligence_claim_support_evidence ON i.evidence_id=e.id
                    JOIN intelligence_items h ON h.id=i.item_id
                    JOIN intelligence_revisions r ON r.item_id=h.id AND r.revision=h.revision
                    LEFT JOIN intelligence_capsules c ON c.item_id=r.item_id AND c.revision=r.revision
                    WHERE e.source_id IN ({string.Join(',', parameters.Select(parameter => parameter.Name))})
                        AND json_extract(r.body,'$.Knowledge.kind')=$kind AND json_extract(r.body,'$.Scope')=$scope
                    LIMIT 33;
                    """;
                await using var command = Command(connection, sql, [.. parameters, ("$kind", kind), ("$scope", CanonicalRecordValidation.Serialize(scope))]);
                await using var reader = await command.ExecuteReaderAsync(token);
                var items = new List<IntelligenceItemRevision>();
                var aliases = new Dictionary<Guid, Guid>();
                var bytes = 0;
                while (await reader.ReadAsync(token))
                {
                    if (items.Count == 32)
                    {
                        return new IntelligenceReadSnapshot(generation, items, aliases, HasMore: true);
                    }

                    var json = reader.GetString(0);
                    var length = Encoding.UTF8.GetByteCount(json);
                    if (length <= 65536 && length > (512 * 1024) - bytes)
                    {
                        return new IntelligenceReadSnapshot(generation, items, aliases, HasMore: true);
                    }

                    var item = Decode<IntelligenceItemRevision>(json, ref bytes);
                    items.Add(item with { Capsule = await reader.IsDBNullAsync(1, token) ? null : reader.GetString(1) });
                    if (!await reader.IsDBNullAsync(2, token))
                    {
                        aliases.Add(item.Reference.ItemId, Guid.Parse(reader.GetString(2)));
                    }
                }

                return new IntelligenceReadSnapshot(generation, items, aliases);
            },
            cancellationToken);
    }

    /// <summary>Reads a bounded normalized source excerpt by durable identity.</summary>
    internal Task<RepositoryEvidenceExcerpt?> ReadEvidenceAsync(
        RepositoryIntelligenceAdmission admission, string id, CancellationToken cancellationToken = default)
    {
        CanonicalRecordValidation.Text(id, 64);
        return AccessAsync<RepositoryEvidenceExcerpt?>(
            admission,
            async (connection, token) =>
        {
            await GenerationAsync(connection, token);
            var bytes = 0;
            return await ScalarAsync(connection, "SELECT body FROM intelligence_evidence WHERE id=$id;", token, ("$id", id)) is not string json ? null : Decode<RepositoryEvidenceExcerpt>(json, ref bytes);
        },
            cancellationToken);
    }

    /// <summary>Publishes a validated coherent unit with repository and item revision preconditions.</summary>
    internal Task<IntelligenceRelatedRecords> ReadRelatedAsync(
        RepositoryIntelligenceAdmission admission,
        IntelligenceRevisionReference reference,
        int maximumRecords = 16,
        CancellationToken cancellationToken = default)
    {
        if (maximumRecords is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        }

        return AccessAsync(
            admission,
            async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            await GenerationAsync(connection, token);
            var sql3 = """
                SELECT body FROM intelligence_relationships
                WHERE (from_id=$id AND from_revision=$revision) OR (to_id=$id AND to_revision=$revision) ORDER BY id LIMIT $limit;
                """;
            var relationships = await ReadBodiesAsync<IntelligenceRelationship>(connection, sql3, reference, maximumRecords, 512 * 1024, token);
            var sql4 = """
                SELECT e.body FROM intelligence_episode_items i JOIN intelligence_episodes e ON e.id=i.episode_id
                WHERE i.item_id=$id AND i.revision=$revision ORDER BY e.id LIMIT $limit;
                """;
            var episodes = await ReadBodiesAsync<IntelligenceEpisode>(connection, sql4, reference, maximumRecords, (512 * 1024) - relationships.Bytes, token);
            var sql5 = """
                SELECT body FROM intelligence_uncertainties WHERE item_id=$id AND revision=$revision ORDER BY id LIMIT $limit;
                """;
            var uncertainties = await ReadBodiesAsync<IntelligenceUncertainty>(connection, sql5, reference, maximumRecords, (512 * 1024) - relationships.Bytes - episodes.Bytes, token);
            var receiptSql = """
                SELECT r.body FROM intelligence_receipt_items i JOIN intelligence_receipts r ON r.id=i.receipt_id
                WHERE i.item_id=$id AND i.revision=$revision ORDER BY r.id LIMIT $limit;
                """;
            var receipts = await ReadBodiesAsync<IntelligenceReconciliationReceipt>(connection, receiptSql, reference, maximumRecords, (512 * 1024) - relationships.Bytes - episodes.Bytes - uncertainties.Bytes, token);
            return new IntelligenceRelatedRecords(relationships.Items, episodes.Items, uncertainties.Items, relationships.HasMore || episodes.HasMore || uncertainties.HasMore || receipts.HasMore, receipts.Items);
        },
            cancellationToken);
    }

    /// <summary>Reads pinned unfinished work without claiming that the operation or baseline completed.</summary>
    internal Task<IntelligenceContinuation?> ReadContinuationAsync(
        RepositoryIntelligenceAdmission admission, string unitId, CancellationToken cancellationToken = default)
    {
        CanonicalRecordValidation.Text(unitId, 128);
        return AccessAsync<IntelligenceContinuation?>(
            admission,
            async (connection, token) =>
        {
            await GenerationAsync(connection, token);
            var bytes = 0;
            return await ScalarAsync(connection, "SELECT body FROM intelligence_continuations WHERE id=$id;", token, ("$id", unitId)) is not string json ? null : Decode<IntelligenceContinuation>(json, ref bytes);
        },
            cancellationToken);
    }

    /// <summary>Recovers a completed publication unit independently of mutable item heads.</summary>
    internal Task<IntelligenceReconciliationReceipt?> ReadReceiptAsync(
        RepositoryIntelligenceAdmission admission, string id, CancellationToken cancellationToken = default)
    {
        CanonicalRecordValidation.Text(id, 64);
        return AccessAsync<IntelligenceReconciliationReceipt?>(
            admission,
            async (connection, token) =>
            {
                await GenerationAsync(connection, token);
                var bytes = 0;
                return await ScalarAsync(connection, "SELECT body FROM intelligence_receipts WHERE id=$id;", token, ("$id", id)) is not string json ? null : Decode<IntelligenceReconciliationReceipt>(json, ref bytes);
            },
            cancellationToken);
    }

    /// <summary>Reads an existing normalized episode without preloading other episodes.</summary>
    internal Task<IntelligenceEpisode?> ReadEpisodeAsync(
        RepositoryIntelligenceAdmission admission, string id, CancellationToken cancellationToken = default)
    {
        CanonicalRecordValidation.Text(id, 64);
        return AccessAsync<IntelligenceEpisode?>(
            admission,
            async (connection, token) =>
            {
                await GenerationAsync(connection, token);
                var bytes = 0;
                return await ScalarAsync(connection, "SELECT body FROM intelligence_episodes WHERE id=$id;", token, ("$id", id)) is not string json ? null : Decode<IntelligenceEpisode>(json, ref bytes);
            },
            cancellationToken);
    }

    /// <summary>Reads one existing immutable relation for repeat analysis without rewriting its original provenance.</summary>
    internal Task<IntelligenceRelationship?> ReadRelationshipAsync(RepositoryIntelligenceAdmission admission, Guid id, CancellationToken cancellationToken = default)
    {
        return AccessAsync<IntelligenceRelationship?>(
            admission,
            async (connection, token) =>
            {
                await GenerationAsync(connection, token);
                var bytes = 0;
                return await ScalarAsync(connection, "SELECT body FROM intelligence_relationships WHERE id=$id;", token, ("$id", id)) is not string json ? null : Decode<IntelligenceRelationship>(json, ref bytes);
            },
            cancellationToken);
    }

    /// <summary>Reads one retained question so repeated evidence does not duplicate its original provenance.</summary>
    internal Task<IntelligenceUncertainty?> ReadUncertaintyAsync(RepositoryIntelligenceAdmission admission, Guid id, CancellationToken cancellationToken = default)
    {
        return AccessAsync<IntelligenceUncertainty?>(
            admission,
            async (connection, token) =>
            {
                await GenerationAsync(connection, token);
                var bytes = 0;
                return await ScalarAsync(connection, "SELECT body FROM intelligence_uncertainties WHERE id=$id;", token, ("$id", id)) is not string json ? null : Decode<IntelligenceUncertainty>(json, ref bytes);
            },
            cancellationToken);
    }

    /// <summary>Checks indexed receipt-backed correction authority without changing immutable revision evidence.</summary>
    internal Task<bool> HasAttributedSupportAsync(RepositoryIntelligenceAdmission admission, Guid id, CancellationToken cancellationToken = default)
    {
        return AccessAsync(
            admission,
            async (connection, token) =>
            {
                await GenerationAsync(connection, token);
                return await ScalarAsync(connection, "SELECT 1 FROM intelligence_claim_support WHERE item_id=$id AND attributed_receipt IS NOT NULL LIMIT 1;", token, ("$id", id)) is not null;
            },
            cancellationToken);
    }

    /// <summary>Resolves a current merge alias; exact historical citations still use <see cref="ReadRevisionAsync"/>.</summary>
    internal Task<Guid> ResolveAliasAsync(
        RepositoryIntelligenceAdmission admission, Guid id, CancellationToken cancellationToken = default)
    {
        return AccessAsync(
            admission,
            async (connection, token) =>
            {
                await using var transaction = connection.BeginTransaction(deferred: true);
                await GenerationAsync(connection, token);
                return await ResolveAliasAsync(connection, id, token);
            },
            cancellationToken);
    }

    /// <summary>Publishes a validated coherent unit with repository and item revision preconditions.</summary>
    internal Task<long> PublishAsync(
        RepositoryIntelligenceAdmission admission,
        IntelligencePublication publication,
        CancellationToken cancellationToken = default)
    {
        var frozen = CanonicalRecordValidation.Freeze(publication, _context);
        return AccessAsync(
            admission,
            async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: false);
            var generation = await GenerationAsync(connection, token);
            if (generation != frozen.ExpectedGeneration)
            {
                throw Conflict();
            }

            foreach (var evidence in frozen.Evidence)
            {
                await ImmutableAsync(connection, "intelligence_sources", evidence.Source.Id, CanonicalRecordValidation.Serialize(evidence.Source), token);
                var json = CanonicalRecordValidation.Serialize(evidence);
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_evidence VALUES($id,$source,$body);", token, ("$id", evidence.Id), ("$source", evidence.Source.Id), ("$body", json));
                await EqualBodyAsync(connection, "intelligence_evidence", evidence.Id, json, token);
            }

            foreach (var write in frozen.Items)
            {
                var item = write.Item;
                if (write.ExpectedRevision == 0)
                {
                    var inserted = await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_items VALUES($id,$key,$revision,NULL);", token, ("$id", item.Reference.ItemId), ("$key", item.IdentityKey), ("$revision", item.Reference.Revision));
                    if (inserted != 1)
                    {
                        throw Conflict();
                    }
                }
                else
                {
                    var sql6 = """
                        UPDATE intelligence_items SET revision=$revision
                        WHERE id=$id AND revision=$expected AND identity_key=$key AND alias_to IS NULL;
                        """;
                    var updated = await ExecuteAsync(connection, sql6, token, ("$revision", item.Reference.Revision), ("$id", item.Reference.ItemId), ("$expected", write.ExpectedRevision), ("$key", item.IdentityKey));
                    if (updated != 1)
                    {
                        throw Conflict();
                    }
                }

                await ExecuteAsync(connection, "INSERT INTO intelligence_revisions VALUES($id,$revision,$body);", token, ("$id", item.Reference.ItemId), ("$revision", item.Reference.Revision), ("$body", CanonicalRecordValidation.Serialize(item with { Capsule = null })));
                await ClaimAsync(connection, new IntelligenceClaimIdentity(item.IdentityKey, item.Reference.ItemId), token);
                if (item.Capsule is { } capsule)
                {
                    await ExecuteAsync(connection, "INSERT INTO intelligence_capsules VALUES($id,$revision,$text);", token, ("$id", item.Reference.ItemId), ("$revision", item.Reference.Revision), ("$text", capsule));
                }

                foreach (var id in item.SupportingEvidenceIds)
                {
                    await ItemEvidenceAsync(connection, item.Reference, id, "supporting", token);
                    await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_claim_support(item_id,evidence_id,revision) VALUES($item,$evidence,$revision);", token, ("$item", item.Reference.ItemId), ("$evidence", id), ("$revision", item.Reference.Revision));
                }

                foreach (var id in item.ConflictingEvidenceIds)
                {
                    await ItemEvidenceAsync(connection, item.Reference, id, "conflicting", token);
                }
            }

            foreach (var write in frozen.Items.Where(write => write.AliasTo is not null))
            {
                await ExecuteAsync(connection, "UPDATE intelligence_items SET alias_to=$alias WHERE id=$id;", token, ("$alias", write.AliasTo), ("$id", write.Item.Reference.ItemId));
                await ResolveAliasAsync(connection, write.Item.Reference.ItemId, token);
            }

            foreach (var relationship in frozen.Relationships)
            {
                var json = CanonicalRecordValidation.Serialize(relationship);
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_relationships VALUES($id,$from,$fromRevision,$to,$toRevision,$body);", token, ("$id", relationship.Id), ("$from", relationship.From.ItemId), ("$fromRevision", relationship.From.Revision), ("$to", relationship.To.ItemId), ("$toRevision", relationship.To.Revision), ("$body", json));
                await EqualBodyAsync(connection, "intelligence_relationships", relationship.Id, json, token);
                await EvidenceLinksAsync(connection, "intelligence_relationship_evidence", "relationship_id", relationship.Id, relationship.EvidenceIds, token);
            }

            foreach (var episode in frozen.Episodes)
            {
                await EpisodeAsync(connection, episode, token);
                await EvidenceLinksAsync(connection, "intelligence_episode_evidence", "episode_id", episode.Id, episode.EvidenceIds, token);
                foreach (var reference in episode.Items)
                {
                    await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_episode_items VALUES($id,$item,$revision);", token, ("$id", episode.Id), ("$item", reference.ItemId), ("$revision", reference.Revision));
                }
            }

            foreach (var uncertainty in frozen.Uncertainties)
            {
                var json = CanonicalRecordValidation.Serialize(uncertainty);
                await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_uncertainties VALUES($id,$item,$revision,$body);", token, ("$id", uncertainty.Id), ("$item", uncertainty.Item.ItemId), ("$revision", uncertainty.Item.Revision), ("$body", json));
                await EqualBodyAsync(connection, "intelligence_uncertainties", uncertainty.Id, json, token);
                await EvidenceLinksAsync(connection, "intelligence_uncertainty_evidence", "uncertainty_id", uncertainty.Id, uncertainty.EvidenceIds, token);
            }

            if (frozen.Continuation is { } continuation)
            {
                await ExecuteAsync(connection, "INSERT INTO intelligence_continuations VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body;", token, ("$id", continuation.UnitId), ("$body", CanonicalRecordValidation.Serialize(continuation)));
            }

            foreach (var claim in frozen.Claims ?? [])
            {
                await ClaimAsync(connection, claim, token);
            }

            if (frozen.Receipt is { } receipt)
            {
                await ImmutableAsync(connection, "intelligence_receipts", receipt.Id, CanonicalRecordValidation.Serialize(receipt, 262144), token);
                foreach (var reference in receipt.Outcomes.SelectMany(outcome => new[] { outcome.Item, outcome.RelatedItem }).OfType<IntelligenceRevisionReference>().Distinct())
                {
                    await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_receipt_items VALUES($receipt,$item,$revision);", token, ("$receipt", receipt.Id), ("$item", reference.ItemId), ("$revision", reference.Revision));
                }

                await EvidenceLinksAsync(connection, "intelligence_receipt_evidence", "receipt_id", receipt.Id, receipt.Outcomes.SelectMany(outcome => outcome.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray(), token);
                foreach (var outcome in receipt.Outcomes)
                {
                    if (outcome.Item is not { } item)
                    {
                        continue;
                    }

                    foreach (var id in outcome.SupportingEvidenceIds ?? [])
                    {
                        var attributedReceipt = receipt.Provenance.UserAttribution is null ? null : receipt.Id;
                        var supportSql = """
                            INSERT INTO intelligence_claim_support VALUES($item,$evidence,$revision,$receipt)
                            ON CONFLICT(item_id,evidence_id) DO UPDATE SET attributed_receipt=COALESCE(intelligence_claim_support.attributed_receipt,excluded.attributed_receipt);
                            """;
                        await ExecuteAsync(connection, supportSql, token, ("$receipt", attributedReceipt), ("$item", item.ItemId), ("$revision", item.Revision), ("$evidence", id));
                    }
                }
            }

            await ExecuteAsync(connection, "UPDATE intelligence_context SET generation=generation+1 WHERE singleton=1;", token);
            token.ThrowIfCancellationRequested();
            await CommitAsync(admission, transaction, token);
            return generation + 1;
        },
            cancellationToken);
    }

    private Task<T> AccessAsync<T>(
        RepositoryIntelligenceAdmission admission,
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (admission.RepositoryIdentity != _context.CheckoutIdentity)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Storage admission belongs to another checkout.");
        }

        return _feature.PersistAsync(
            admission,
            async token =>
        {
            try
            {
                await using var connection = await ConnectAsync(token);
                return await action(connection, token);
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or JsonException && !token.IsCancellationRequested)
            {
                throw Classify(exception);
            }
        },
            cancellationToken);
    }

    private async Task<SqliteConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        _ = VerifyPath(_root);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task CommitAsync(RepositoryIntelligenceAdmission admission, SqliteTransaction transaction, CancellationToken token)
    {
        await _feature.CommitAsync(
            admission,
            () =>
        {
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return 0;
        },
            token);
    }

    private async Task<long> GenerationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT repository,checkout,generation FROM intelligence_context WHERE singleton=1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != _context.RepositoryIdentity || reader.GetString(1) != _context.CheckoutIdentity)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Stored intelligence belongs to another repository context.");
        }

        return reader.GetInt64(2);
    }

    private static string VerifyPath(string root)
    {
        var directory = System.IO.Path.Combine(root, ".threadsmith", "repository-intelligence");
        var path = System.IO.Path.Combine(directory, "intelligence.db");
        for (var current = directory; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Canonical storage cannot redirect through linked directories.");
            }
        }

        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IntelligenceStorageException(IntelligenceStorageFailure.IncompatibleContext, "Canonical storage cannot redirect through linked files.");
            }
        }

        return path;
    }

    private static async Task<Guid> ResolveAliasAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(id))
            {
                throw new InvalidDataException("Canonical aliases cannot form a cycle.");
            }

            var next = await ScalarAsync(connection, "SELECT alias_to FROM intelligence_items WHERE id=$id;", cancellationToken, ("$id", id));
            if (next is DBNull)
            {
                return id;
            }

            if (next is not string value || !Guid.TryParse(value, out id))
            {
                throw new InvalidDataException("Canonical alias endpoint is unavailable.");
            }
        }

        throw new InvalidDataException("Canonical alias chain exceeds its depth ceiling.");
    }

    private static Task<int> ItemEvidenceAsync(SqliteConnection connection, IntelligenceRevisionReference reference, string id, string role, CancellationToken token) => ExecuteAsync(connection, "INSERT INTO intelligence_item_evidence VALUES($item,$revision,$evidence,$role);", token, ("$item", reference.ItemId), ("$revision", reference.Revision), ("$evidence", id), ("$role", role));

    private static async Task ClaimAsync(SqliteConnection connection, IntelligenceClaimIdentity claim, CancellationToken token)
    {
        await ExecuteAsync(connection, "INSERT OR IGNORE INTO intelligence_claims VALUES($key,$item);", token, ("$key", claim.Key), ("$item", claim.ItemId));
        var existing = await ScalarAsync(connection, "SELECT item_id FROM intelligence_claims WHERE key=$key;", token, ("$key", claim.Key));
        if (existing is not string id || !Guid.TryParse(id, out var existingId)
            || (existingId != claim.ItemId && await ResolveAliasAsync(connection, existingId, token) != await ResolveAliasAsync(connection, claim.ItemId, token)))
        {
            throw Conflict();
        }
    }

    private static async Task EpisodeAsync(SqliteConnection connection, IntelligenceEpisode episode, CancellationToken token)
    {
        var existing = await ScalarAsync(connection, "SELECT body FROM intelligence_episodes WHERE id=$id;", token, ("$id", episode.Id)) as string;
        if (existing is not null)
        {
            var bytes = 0;
            var prior = Decode<IntelligenceEpisode>(existing, ref bytes);
            if (prior.Scope != episode.Scope || !prior.Commits.SequenceEqual(episode.Commits, StringComparer.Ordinal)
                || prior.Provenance != episode.Provenance || prior.EvidenceIds.Except(episode.EvidenceIds, StringComparer.Ordinal).Any()
                || prior.Items.Except(episode.Items).Any())
            {
                throw new InvalidDataException("Episode updates must preserve constituents, original provenance and existing associations.");
            }
        }

        await ExecuteAsync(connection, "INSERT INTO intelligence_episodes VALUES($id,$body) ON CONFLICT(id) DO UPDATE SET body=excluded.body;", token, ("$id", episode.Id), ("$body", CanonicalRecordValidation.Serialize(episode)));
    }

    private static async Task EvidenceLinksAsync(SqliteConnection connection, string table, string column, object id, IReadOnlyList<string> evidence, CancellationToken token)
    {
        foreach (var reference in evidence)
        {
            await ExecuteAsync(connection, $"INSERT OR IGNORE INTO {table}({column},evidence_id) VALUES($id,$evidence);", token, ("$id", id), ("$evidence", reference));
        }
    }

    private static async Task ImmutableAsync(SqliteConnection connection, string table, object id, string json, CancellationToken token)
    {
        await ExecuteAsync(connection, $"INSERT OR IGNORE INTO {table}(id,body) VALUES($id,$body);", token, ("$id", id), ("$body", json));
        await EqualBodyAsync(connection, table, id, json, token);
    }

    private static async Task EqualBodyAsync(SqliteConnection connection, string table, object id, string json, CancellationToken token)
    {
        var existing = await ScalarAsync(connection, $"SELECT body FROM {table} WHERE id=$id;", token, ("$id", id));
        if (existing is not string body || body != json)
        {
            throw new InvalidDataException("A durable identity cannot be rebound to different content.");
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteScalarAsync(token);
    }

    private static T Decode<T>(string json, ref int bytes)
    {
        var length = Encoding.UTF8.GetByteCount(json);
        bytes += length;
        if (length > PhysicalMaximum<T>())
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.Corrupt, "Canonical record exceeds its physical byte ceiling.");
        }

        if (bytes > 512 * 1024)
        {
            throw new IntelligenceStorageException(IntelligenceStorageFailure.ReadLimit, "Canonical lookup exceeds its byte budget; request fewer records.");
        }

        return JsonSerializer.Deserialize<T>(json, CanonicalRecordValidation.JsonOptions)
            ?? throw new IntelligenceStorageException(IntelligenceStorageFailure.Corrupt, "Canonical record is empty.");
    }

    private static async Task<RelatedPage<T>> ReadBodiesAsync<T>(
        SqliteConnection connection, string sql, IntelligenceRevisionReference reference, int maximumRecords, int maximumBytes, CancellationToken token)
    {
        await using var command = Command(connection, sql, ("$id", reference.ItemId), ("$revision", reference.Revision), ("$limit", maximumRecords + 1));
        await using var reader = await command.ExecuteReaderAsync(token);
        var records = new List<T>();
        var bytes = 0;
        while (await reader.ReadAsync(token))
        {
            if (records.Count == maximumRecords)
            {
                return new RelatedPage<T>(records, true, bytes);
            }

            var json = reader.GetString(0);
            var length = Encoding.UTF8.GetByteCount(json);
            if (length <= PhysicalMaximum<T>() && length > maximumBytes - bytes)
            {
                return new RelatedPage<T>(records, true, bytes);
            }

            records.Add(Decode<T>(json, ref bytes));
        }

        return new RelatedPage<T>(records, false, bytes);
    }

    private static IntelligenceStorageException Conflict() => new(IntelligenceStorageFailure.RevisionConflict, "Canonical publication conflicted with another revision; reload and revalidate before retrying.");

    private static int PhysicalMaximum<T>() => typeof(T) == typeof(IntelligenceReconciliationReceipt) ? 262144 : 65536;

    private static IntelligenceStorageException Classify(Exception exception) => new(
        exception is JsonException or SqliteException { SqliteErrorCode: 11 or 26 }
            ? IntelligenceStorageFailure.Corrupt : IntelligenceStorageFailure.Unavailable,
        "Canonical storage is unavailable or invalid; previous committed knowledge was preserved.");

    private sealed record RelatedPage<T>(IReadOnlyList<T> Items, bool HasMore, int Bytes);
}
