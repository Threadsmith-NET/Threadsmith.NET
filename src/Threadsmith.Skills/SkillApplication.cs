namespace Threadsmith.Skills;

using Threadsmith.Core;

/// <summary>Shared command boundary for skill discovery, verification, policy, invocation, and restoration.</summary>
public sealed class SkillApplication :
    ICommandHandler<RefreshSkillsCommand, SkillCatalogSnapshot>,
    ICommandHandler<ListSkillsCommand, IReadOnlyList<SkillCatalogCandidate>>,
    ICommandHandler<GetSkillCommand, SkillCatalogCandidate>,
    ICommandHandler<GetSkillCompatibilityCommand, SkillCompatibilityResult>,
    ICommandHandler<VerifySkillCommand, SkillCatalogCandidate>,
    ICommandHandler<SetSkillEnabledCommand, SkillCatalogCandidate>,
    ICommandHandler<InstallSkillCommand, SkillCatalogCandidate>,
    ICommandHandler<UninstallSkillCommand, bool>,
    ICommandHandler<PinSkillCommand, SkillPackageIdentity>,
    ICommandHandler<InvokeSkillCommand, SkillInvocationResult>,
    ICommandHandler<ResumeSkillCommand, SkillInvocationResult>,
    ICommandHandler<ContinueSkillCommand, SkillInvocationResult>,
    ICommandHandler<GetSkillInvocationCommand, SkillWorkflowCheckpoint?>,
    ICommandHandler<CancelSkillInvocationCommand, bool>
{
    private readonly ISkillCatalog _catalog;
    private readonly ISkillCompatibilityEvaluator _compatibility;
    private readonly ISkillTrustPolicyProvider _policy;
    private readonly SkillPackageInstaller _installer;
    private readonly ISkillStateStore _state;
    private readonly ISkillPackageVerifier _verifier;
    private readonly ISkillWorkflowOrchestrator _workflows;

    /// <summary>Initializes a new instance of the <see cref="SkillApplication"/> class.</summary>
    public SkillApplication(
        ISkillCatalog catalog,
        ISkillPackageVerifier verifier,
        ISkillTrustPolicyProvider policy,
        ISkillCompatibilityEvaluator compatibility,
        ISkillWorkflowOrchestrator workflows,
        ISkillStateStore state,
        SkillPackageInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(installer);
        _catalog = catalog;
        _verifier = verifier;
        _policy = policy;
        _compatibility = compatibility;
        _installer = installer;
        _workflows = workflows;
        _state = state;
    }

    /// <inheritdoc />
    public Task<SkillCatalogSnapshot> HandleAsync(
        RefreshSkillsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _catalog.RefreshAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SkillCatalogCandidate>> HandleAsync(
        ListSkillsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.Search(command.Query));
    }

    /// <inheritdoc />
    public Task<SkillCatalogCandidate> HandleAsync(
        GetSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_catalog.Resolve(command.Selector));
    }

    /// <inheritdoc />
    public Task<SkillCompatibilityResult> HandleAsync(
        GetSkillCompatibilityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = _catalog.Resolve(command.Selector);
        return Task.FromResult(_compatibility.Evaluate(candidate, command.Request));
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> HandleAsync(
        VerifySkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var candidate = await ResolveAsync(command.Selector, cancellationToken);
        var verified = await _verifier.VerifyAsync(candidate, cancellationToken);
        await RecordVerificationAsync(verified, cancellationToken);
        return Update(verified);
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> HandleAsync(
        SetSkillEnabledCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var candidate = await ResolveAsync(command.Selector, cancellationToken);
        var exact = await _verifier.VerifyAsync(candidate, cancellationToken);
        if (exact.Verification is SkillVerificationState.Invalid or SkillVerificationState.Revoked)
        {
            await RecordVerificationAsync(exact, cancellationToken);
            return Update(exact);
        }

        await _policy.SetEnabledAsync(exact, command.Enabled, cancellationToken);
        var verified = await _verifier.VerifyAsync(exact, cancellationToken);
        await RecordVerificationAsync(verified, cancellationToken);
        return Update(verified);
    }

    /// <inheritdoc />
    public async Task<SkillCatalogCandidate> HandleAsync(
        InstallSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var installedPath = await _installer.InstallArchiveAsync(
            command.ArchivePath,
            _verifier,
            SkillScope.User,
            command.Source,
            cancellationToken);
        _ = await _catalog.RefreshAsync(cancellationToken);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var installed = _catalog.Snapshot.Candidates.Single(candidate =>
            string.Equals(candidate.Provenance.PackageRoot, installedPath, pathComparison));
        await _policy.SetEnabledAsync(installed, enabled: true, cancellationToken);
        var verified = await _verifier.VerifyAsync(installed, cancellationToken);
        await RecordVerificationAsync(verified, cancellationToken);
        return Update(verified);
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        UninstallSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var candidate = _catalog.Resolve(command.Selector);
        var pin = await _state.GetPinAsync(
            candidate.Metadata.SkillId,
            cancellationToken);
        if (pin == candidate.Identity
            || await _state.HasActivePackageReferenceAsync(candidate.Identity, cancellationToken))
        {
            throw new InvalidOperationException(
                "Pinned packages and packages retained by active workflows cannot be uninstalled.");
        }

        var removed = await _installer.UninstallAsync(candidate, cancellationToken);
        if (removed)
        {
            _ = await _catalog.RefreshAsync(cancellationToken);
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task<SkillPackageIdentity> HandleAsync(
        PinSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var candidate = await ResolveAsync(command.Selector, cancellationToken);
        var verified = await _verifier.VerifyAsync(candidate, cancellationToken);
        if (!verified.Enabled)
        {
            throw new UnauthorizedAccessException("Only an enabled verified package may be pinned.");
        }

        await RecordVerificationAsync(verified, cancellationToken);
        await _state.SavePinAsync(
            verified.Metadata.SkillId,
            verified.Identity,
            cancellationToken);
        Update(verified);
        return verified.Identity;
    }

    /// <inheritdoc />
    public Task<SkillInvocationResult> HandleAsync(
        InvokeSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _workflows.InvokeAsync(command.Request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SkillInvocationResult> HandleAsync(
        ResumeSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _workflows.ResumeAsync(command.InvocationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SkillInvocationResult> HandleAsync(
        ContinueSkillCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _workflows.ContinueAsync(
            command.InvocationId,
            command.HostResultJson,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<SkillWorkflowCheckpoint?> HandleAsync(
        GetSkillInvocationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _state.GetCheckpointAsync(command.InvocationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(
        CancelSkillInvocationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _workflows.CancelAsync(command.InvocationId, cancellationToken);
    }

    private Task RecordVerificationAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken)
    {
        return _state.SaveVerificationAsync(
            new SkillVerificationRecord
            {
                Package = candidate.Identity,
                Scope = candidate.Provenance.Scope,
                Source = candidate.Provenance.Source,
                State = candidate.Verification,
                Reason = candidate.VerificationReason,
                SignerId = candidate.Metadata.Signature?.SignerId,
                VerifiedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
    }

    private Task<SkillCatalogCandidate> ResolveAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        return _catalog is IAsyncSkillCatalog asynchronous
            ? asynchronous.ResolveAsync(selector, cancellationToken)
            : Task.FromResult(_catalog.Resolve(selector));
    }

    private SkillCatalogCandidate Update(SkillCatalogCandidate candidate)
    {
        return _catalog is IUpdatableSkillCatalog updatable
            ? updatable.UpdateCandidate(candidate)
            : candidate;
    }
}
