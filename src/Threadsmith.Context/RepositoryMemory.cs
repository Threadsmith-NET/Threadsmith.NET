namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Result of conservatively invalidating repository-scoped memory.</summary>
public sealed record RepositoryMemoryInvalidationResult(
    int InvalidatedCount,
    IReadOnlyList<RepositoryMemoryId> MemoryIds);

/// <summary>Host-owned bounds for repository-scoped memory command policy.</summary>
public sealed record RepositoryMemoryPolicy
{
    /// <summary>Maximum retained characters per repository-memory item.</summary>
    public int MaximumItemCharacters { get; init; } = 2_000;

    /// <summary>Maximum active repository-memory items preserved per repository.</summary>
    public int MaximumActiveItems { get; init; } = 512;

    /// <summary>Validates configured repository-memory bounds.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumItemCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumActiveItems);
    }
}

/// <summary>Conservatively invalidates repository-dependent memory after host-observed repository changes.</summary>
public sealed class RepositoryMemoryInvalidator
{
    private const string InvalidationReason = "Repository-dependent memory invalidated at the turn boundary.";

    private readonly IDomainEventStream? _events;
    private readonly IRepositoryMemoryStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryInvalidator"/> class.</summary>
    public RepositoryMemoryInvalidator(
        IRepositoryMemoryStore store,
        TimeProvider? timeProvider = null,
        IDomainEventStream? events = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _events = events;
    }

    /// <summary>Marks active repository-dependent memory stale when a supporting key changes.</summary>
    public async Task<RepositoryMemoryInvalidationResult> InvalidateAtTurnBoundaryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        IReadOnlyList<string> invalidationKeys,
        string? currentRepositoryRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        ArgumentNullException.ThrowIfNull(invalidationKeys);
        var snapshot = await _store.GetSnapshotAsync(repositoryIdentity, cancellationToken);
        var affected = snapshot.Items
            .Where(item => item.Validity == RepositoryMemoryValidity.Active
                && IsAffected(item, invalidationKeys, currentRepositoryRevision))
            .OrderBy(item => item.Id.Value)
            .ToArray();
        var invalidated = new List<RepositoryMemoryId>();
        foreach (var item in affected)
        {
            var updated = await _store.UpdateValidityAsync(
                repositoryIdentity,
                item.Id,
                RepositoryMemoryValidity.Stale,
                InvalidationReason,
                cancellationToken);
            if (!updated)
            {
                continue;
            }

            invalidated.Add(item.Id);
            if (_events is not null)
            {
                await _events.PublishAsync(
                    new RepositoryMemoryValidityChanged(
                        sessionId,
                        _timeProvider.GetUtcNow(),
                        repositoryIdentity,
                        item.Id,
                        RepositoryMemoryValidity.Stale,
                        InvalidationReason),
                    cancellationToken);
            }
        }

        return new RepositoryMemoryInvalidationResult(invalidated.Count, invalidated);
    }

    private static bool IsAffected(
        RepositoryMemoryItem item,
        IReadOnlyList<string> invalidationKeys,
        string? currentRepositoryRevision)
    {
        if (!string.IsNullOrWhiteSpace(currentRepositoryRevision)
            && !string.IsNullOrWhiteSpace(item.RepositoryRevision)
            && !string.Equals(item.RepositoryRevision, currentRepositoryRevision, StringComparison.Ordinal))
        {
            return true;
        }

        var repositoryDependent = !string.IsNullOrWhiteSpace(item.RepositoryRevision)
            || item.Scope.Paths.Count > 0
            || item.Scope.Symbols.Count > 0
            || item.Scope.Projects.Count > 0;
        if (!repositoryDependent)
        {
            return false;
        }

        if (invalidationKeys.Any(key => string.Equals(key, "repository", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var changedFiles = KeyValues(invalidationKeys, "file:");
        var changedSymbols = KeyValues(invalidationKeys, "symbol:");
        var changedProjects = KeyValues(invalidationKeys, "project:");
        return changedFiles.Any(file => Matches(item.Scope.Paths, file)
                || Matches(item.Scope.Projects, file)
                || !string.IsNullOrWhiteSpace(item.RepositoryRevision)
                || item.Scope.Symbols.Count > 0)
            || changedSymbols.Any(symbol => Matches(item.Scope.Symbols, symbol))
            || changedProjects.Any(project => Matches(item.Scope.Projects, project));
    }

    private static IReadOnlyList<string> KeyValues(IReadOnlyList<string> keys, string prefix)
    {
        return [.. keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..])];
    }

    private static bool Matches(IReadOnlyList<string> values, string changedValue)
    {
        return values.Any(value => string.Equals(value, changedValue, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Applies deterministic host policy to explicit repository-memory commands.</summary>
public sealed class RepositoryMemoryGovernor : IRepositoryMemoryGovernor
{
    private readonly RepositoryMemoryPolicy _policy;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IRepositoryMemoryStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="RepositoryMemoryGovernor"/> class.</summary>
    public RepositoryMemoryGovernor(
        IRepositoryMemoryStore store,
        IOutputSanitizer sanitizer,
        RepositoryMemoryPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _policy = policy ?? new RepositoryMemoryPolicy();
        _policy.Validate();
        _store = store;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryRememberResult> RememberAsync(
        RememberRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Text);
        if (!Enum.IsDefined(command.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var content = BoundContent(_sanitizer.Sanitize(command.Text));
        var now = _timeProvider.GetUtcNow();
        return await _store.InsertBoundedAsync(
            new RepositoryMemoryItem
            {
                Id = RepositoryMemoryId.New(),
                RepositoryIdentity = command.RepositoryIdentity,
                Kind = command.Kind,
                Authority = RepositoryMemoryAuthority.UserAuthored,
                Sensitivity = ConversationSensitivity.Sensitive,
                Content = content,
                Sources = [CreateUserCommandSource(command.SessionId, "remember")],
                CreatedAt = now,
                UpdatedAt = now,
            },
            _policy.MaximumActiveItems,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RepositoryMemoryRememberResult> PromoteHostObservedAsync(
        HostObservedRepositoryMemoryPromotion promotion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ValidateRepositoryCommand(promotion.SessionId, promotion.RepositoryIdentity);
        if (promotion.RunId == default)
        {
            throw new ArgumentException("Repository memory promotion requires a non-default run identifier.", nameof(promotion));
        }

        if (!Enum.IsDefined(promotion.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(promotion));
        }

        var content = BoundContent(_sanitizer.Sanitize(promotion.Content));
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var now = _timeProvider.GetUtcNow();
        return _store.InsertBoundedAsync(
            new RepositoryMemoryItem
            {
                Id = RepositoryMemoryId.New(),
                RepositoryIdentity = promotion.RepositoryIdentity,
                Kind = promotion.Kind,
                Authority = RepositoryMemoryAuthority.HostObserved,
                Sensitivity = ConversationSensitivity.Sensitive,
                Content = content,
                Sources =
                [
                    new RepositoryMemorySource
                    {
                        Kind = RepositoryMemorySourceKind.Run,
                        SourceId = $"run:{promotion.RunId.Value:D}",
                        Description = "Host-observed completed workflow boundary.",
                    },
                ],
                CreatedAt = now,
                UpdatedAt = now,
            },
            _policy.MaximumActiveItems,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemorySnapshot> ListAsync(
        ListRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        if (command.Validity is { } validity && !Enum.IsDefined(validity))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        var snapshot = await _store.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        return command.Validity is null
            ? snapshot
            : snapshot with
            {
                Items = [.. snapshot.Items.Where(item => item.Validity == command.Validity)],
            };
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryItem?> InspectAsync(
        InspectRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        ValidateMemoryId(command.MemoryId);
        var snapshot = await _store.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        return snapshot.Items.FirstOrDefault(item => item.Id == command.MemoryId);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemorySupersedeResult> SupersedeAsync(
        SupersedeRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        ValidateMemoryId(command.MemoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ReplacementText);
        var snapshot = await _store.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        var superseded = snapshot.Items.FirstOrDefault(item => item.Id == command.MemoryId)
            ?? throw new InvalidOperationException("The repository memory item to supersede was not found.");
        var validityChanges = superseded.Validity == RepositoryMemoryValidity.Active
            ? []
            : CreateActiveItemBoundUpdates(
                snapshot,
                activeItemCapacity: _policy.MaximumActiveItems - 1);
        var now = _timeProvider.GetUtcNow();
        var replacement = new RepositoryMemoryItem
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = command.RepositoryIdentity,
            Kind = superseded.Kind,
            Authority = RepositoryMemoryAuthority.UserAuthored,
            Sensitivity = superseded.Sensitivity,
            Content = BoundContent(_sanitizer.Sanitize(command.ReplacementText)),
            Sources = [CreateUserCommandSource(command.SessionId, "supersede")],
            SupersedesId = superseded.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var persisted = await _store.SupersedeAsync(
            command.RepositoryIdentity,
            superseded.Id,
            replacement,
            validityChanges,
            "User correction superseded this repository memory item.",
            cancellationToken);
        return new RepositoryMemorySupersedeResult(persisted, validityChanges);
    }

    /// <inheritdoc />
    public Task<bool> ForgetAsync(
        ForgetRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        ValidateMemoryId(command.MemoryId);
        return _store.UpdateValidityAsync(
            command.RepositoryIdentity,
            command.MemoryId,
            RepositoryMemoryValidity.Forgotten,
            "User forgot this repository memory item.",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryMemoryValidationResult> ValidateAsync(
        ValidateRepositoryMemoryCommand command,
        CancellationToken cancellationToken = default)
    {
        ValidateRepositoryCommand(command.SessionId, command.RepositoryIdentity);
        var snapshot = await _store.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        var requestedChanges = snapshot.Items
            .Where(item => item.Validity is RepositoryMemoryValidity.Active or RepositoryMemoryValidity.Stale)
            .Select(EvaluateValidity)
            .OfType<RepositoryMemoryStateUpdate>()
            .ToDictionary(change => change.MemoryId);
        var projected = snapshot.Items
            .Select(item => requestedChanges.TryGetValue(item.Id, out var change)
                ? item with { Validity = change.Validity, StateReason = change.Reason }
                : item)
            .ToArray();
        foreach (var overflow in SelectOverflow(projected, _policy.MaximumActiveItems))
        {
            requestedChanges[overflow.Id] = new RepositoryMemoryStateUpdate(
                overflow.Id,
                snapshot.Items.Single(item => item.Id == overflow.Id).Validity,
                RepositoryMemoryValidity.Stale,
                "Repository memory active-item bound was exceeded during validation.");
        }

        var appliedChanges = new List<RepositoryMemoryStateUpdate>();
        foreach (var change in requestedChanges.Values.OrderBy(change => change.MemoryId.Value))
        {
            var updated = await _store.UpdateValidityAsync(
                command.RepositoryIdentity,
                change.MemoryId,
                change.Validity,
                change.Reason,
                cancellationToken);
            if (updated)
            {
                appliedChanges.Add(change);
            }
        }

        var validated = await _store.GetSnapshotAsync(command.RepositoryIdentity, cancellationToken);
        return new RepositoryMemoryValidationResult(validated, appliedChanges);
    }

    private static RepositoryMemorySource CreateUserCommandSource(SessionId sessionId, string operation)
    {
        return new RepositoryMemorySource
        {
            Kind = RepositoryMemorySourceKind.UserCommand,
            SourceId = $"session:{sessionId.Value:D}:memory:{operation}",
            Description = "Explicit user repository-memory command.",
        };
    }

    private static void ValidateMemoryId(RepositoryMemoryId memoryId)
    {
        if (memoryId == default)
        {
            throw new ArgumentException("Repository memory requires a non-default identifier.", nameof(memoryId));
        }
    }

    private static void ValidateRepositoryCommand(SessionId sessionId, string repositoryIdentity)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("Repository memory commands require a non-default session identifier.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
    }

    private string BoundContent(string content)
    {
        return content.Length <= _policy.MaximumItemCharacters
            ? content
            : content[.._policy.MaximumItemCharacters];
    }

    private static RepositoryMemoryStateUpdate? EvaluateValidity(RepositoryMemoryItem item)
    {
        if (!Enum.IsDefined(item.Kind)
            || !Enum.IsDefined(item.Authority)
            || !Enum.IsDefined(item.Sensitivity)
            || item.Sources.Count == 0
            || item.Sources.Any(source => !Enum.IsDefined(source.Kind)
                || string.IsNullOrWhiteSpace(source.SourceId)))
        {
            return new RepositoryMemoryStateUpdate(
                item.Id,
                item.Validity,
                RepositoryMemoryValidity.Rejected,
                "Repository memory validation rejected invalid type or provenance metadata.");
        }

        if (item.Validity != RepositoryMemoryValidity.Stale)
        {
            return null;
        }

        var hasRepositorySupport = !string.IsNullOrWhiteSpace(item.RepositoryRevision)
            || item.Scope.Paths.Count > 0
            || item.Scope.Symbols.Count > 0
            || item.Scope.Projects.Count > 0;
        if (item.Authority == RepositoryMemoryAuthority.UserAuthored && !hasRepositorySupport)
        {
            return new RepositoryMemoryStateUpdate(
                item.Id,
                item.Validity,
                RepositoryMemoryValidity.Active,
                "Explicit user-authored memory has no repository-dependent support and was reactivated.");
        }

        return new RepositoryMemoryStateUpdate(
            item.Id,
            item.Validity,
            RepositoryMemoryValidity.Stale,
            "Repository-dependent support could not be verified locally; the item remains stale.");
    }

    private static IReadOnlyList<RepositoryMemoryStateUpdate> CreateActiveItemBoundUpdates(
        RepositoryMemorySnapshot snapshot,
        int activeItemCapacity)
    {
        var changes = new List<RepositoryMemoryStateUpdate>();
        foreach (var item in SelectOverflow(snapshot.Items, activeItemCapacity))
        {
            var reason = "Repository memory active-item bound was exceeded.";
            changes.Add(new RepositoryMemoryStateUpdate(
                item.Id,
                item.Validity,
                RepositoryMemoryValidity.Stale,
                reason));
        }

        return changes;
    }

    private static IReadOnlyList<RepositoryMemoryItem> SelectOverflow(
        IReadOnlyList<RepositoryMemoryItem> items,
        int activeItemCapacity)
    {
        var active = items
            .Where(item => item.Validity == RepositoryMemoryValidity.Active)
            .OrderByDescending(item => PreservationOrder(item.Authority, item.Kind))
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id.Value)
            .ToArray();
        var overflowCount = Math.Max(0, active.Length - activeItemCapacity);
        return active.Take(overflowCount).ToArray();
    }

    private static int PreservationOrder(RepositoryMemoryAuthority authority, RepositoryMemoryKind kind)
    {
        var authorityOrder = authority switch
        {
            RepositoryMemoryAuthority.UserAuthored => 0,
            RepositoryMemoryAuthority.HostObserved => 1,
            RepositoryMemoryAuthority.EvidenceBacked => 2,
            RepositoryMemoryAuthority.ModelProposedValidated => 3,
            _ => 4,
        };
        var kindOrder = kind switch
        {
            RepositoryMemoryKind.UserConstraint => 0,
            RepositoryMemoryKind.UserPreference => 1,
            RepositoryMemoryKind.ArchitectureDecision => 2,
            RepositoryMemoryKind.RepositoryConvention => 3,
            RepositoryMemoryKind.WorkflowFact => 4,
            RepositoryMemoryKind.KnownFailure => 5,
            RepositoryMemoryKind.UnresolvedQuestion => 6,
            RepositoryMemoryKind.EvidenceBackedRepositoryFact => 7,
            _ => 8,
        };
        return (authorityOrder * 16) + kindOrder;
    }
}
