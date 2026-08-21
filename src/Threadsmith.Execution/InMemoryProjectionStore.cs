namespace Threadsmith.Execution;

using System.Collections.ObjectModel;
using Threadsmith.Core;

/// <summary>Builds detached session snapshots from the event stream.</summary>
public sealed class InMemoryProjectionStore : IProjectionStore
{
    private readonly int _maximumToolResultPreviewCharacters;
    private readonly Lock _gate = new();
    private readonly Dictionary<ProjectionKey, SessionProjection> _sessions = [];

    /// <summary>Initializes a new instance of the <see cref="InMemoryProjectionStore"/> class.</summary>
    public InMemoryProjectionStore(ExecutionLimits? limits = null)
    {
        _maximumToolResultPreviewCharacters = (limits ?? ExecutionLimits.Default).MaxToolResultPreviewCharacters;
    }

    /// <inheritdoc />
    public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        var key = new ProjectionKey("session", domainEvent.SessionId.Value.ToString("D"));
        lock (_gate)
        {
            _sessions.TryGetValue(key, out var existing);
            var activity = existing?.Activity.ToList() ?? [];
            var toolActivity = existing?.ToolActivity.ToList() ?? [];
            var diagnostics = existing?.Diagnostics.ToList() ?? [];
            var approvals = existing?.PendingApprovals.ToList() ?? [];
            var resultPreview = domainEvent is ToolInvocationCompleted { ResultJson: { } resultJson }
                ? resultJson[..Math.Min(resultJson.Length, _maximumToolResultPreviewCharacters)]
                : null;
            var resultPreviewTruncated = domainEvent is ToolInvocationCompleted completedEvent
                && completedEvent.ResultJson is { Length: > 0 } truncatedJson
                && truncatedJson.Length > _maximumToolResultPreviewCharacters;
            var next = domainEvent switch
            {
                SessionCreated created => new SessionProjection
                {
                    Key = key,
                    SessionId = created.SessionId,
                    Name = created.Name,
                },
                TaskIntentRecorded intent when existing is not null => existing with
                {
                    Intent = intent.Intent,
                    Error = null,
                    Activity = [],
                },
                AcceptanceCriteriaRecorded criteria when existing is not null => existing with
                {
                    AcceptanceCriteria = criteria.Criteria.ToArray(),
                },
                RepositoryOpened repository when existing is not null => existing with
                {
                    WorkspaceId = repository.WorkspaceId,
                    RepositoryPath = repository.Path,
                    RepositoryTrust = repository.TrustLevel,
                    SemanticConfidence = SemanticConfidenceLevel.None,
                    IsSemanticLoadComplete = false,
                },
                SolutionLoaded solution when existing is not null => existing with
                {
                    WorkspaceId = solution.WorkspaceId,
                    SolutionPath = solution.Path,
                    TargetFrameworks = solution.TargetFrameworks?.ToArray() ?? [],
                    SemanticConfidence = SemanticConfidenceLevel.None,
                    IsSemanticLoadComplete = false,
                },
                ModelOutputObserved output when existing is not null => existing with
                {
                    Activity = [.. activity, output.Text],
                },
                ContextAssembled assembled when existing is not null => existing with
                {
                    ContextInspection = CopyInspection(assembled.Inspection),
                },
                PlanProposed proposed when existing is not null
                    && proposed.Plan is not null => existing with
                    {
                        Plan = new PlanProjection(
                            proposed.RunId,
                            proposed.ApprovalId,
                            proposed.Plan,
                            proposed.ReviewStatus),
                    },
                SemanticMutationWarningObserved warning when existing is not null => existing with
                {
                    Activity =
                    [
                        .. activity,
                        $"Semantic rename warning ({warning.Confidence}): {warning.Message}",
                    ],
                },
                MutationSetProposed proposed when existing is not null
                    && proposed.Preview is not null => existing with
                    {
                        Mutation = new MutationProjection(
                            proposed.MutationSetId,
                            proposed.ApprovalId,
                            CopyPreview(proposed.Preview),
                            proposed.RequiredApproval,
                            proposed.IsolationMode,
                            IsApproved: false,
                            IsApplied: false,
                            IsRolledBack: false),
                    },
                PlanRevisionRequested revision when existing?.Plan is not null
                    && existing.Plan.RunId == revision.RunId => existing with
                    {
                        PendingApprovals = approvals
                            .Where(item => item.ApprovalId != existing.Plan.ApprovalId)
                            .ToArray(),
                        Plan = existing.Plan with
                        {
                            Status = PlanReviewStatus.RevisionRequested,
                            DecisionReason = revision.Instructions,
                        },
                    },
                SemanticConfidenceChanged confidence when existing is not null
                    && Enum.TryParse<SemanticConfidenceLevel>(
                        confidence.Confidence,
                        ignoreCase: true,
                        out var parsedConfidence) => existing with
                        {
                            SemanticConfidence = parsedConfidence,
                        },
                SemanticLoadCompleted completion when existing is not null
                    && existing.WorkspaceId == completion.WorkspaceId
                    && Enum.TryParse<SemanticConfidenceLevel>(
                        completion.Confidence,
                        ignoreCase: true,
                        out var parsedCompletionConfidence) => existing with
                        {
                            SemanticConfidence = parsedCompletionConfidence,
                            IsSemanticLoadComplete = true,
                        },
                ToolInvocationStarted started when existing is not null => existing with
                {
                    ToolActivity =
                    [
                        .. toolActivity.TakeLast(99),
                        new ToolActivityProjection(
                            started.ToolInvocationId,
                            started.RunId,
                            started.ToolName,
                            started.RequestedBy,
                            IsCompleted: false,
                            Succeeded: false,
                            IsTruncated: false,
                            Error: null),
                    ],
                },
                ToolInvocationCompleted completed when existing is not null => existing with
                {
                    ToolActivity = toolActivity
                        .Select(item => item.ToolInvocationId == completed.ToolInvocationId
                            ? item with
                            {
                                IsCompleted = true,
                                Succeeded = completed.Succeeded,
                                IsTruncated = completed.IsTruncated || resultPreviewTruncated,
                                Error = completed.Error,
                                ResultPreview = resultPreview,
                            }
                            : item)
                        .ToArray(),
                },
                ApprovalRequested requested when existing is not null => existing with
                {
                    PendingApprovals =
                    [
                        .. approvals,
                        new ApprovalProjection(requested.ApprovalId, requested.Action),
                    ],
                },
                ApprovalGranted granted when existing is not null => existing with
                {
                    PendingApprovals = approvals
                        .Where(item => item.ApprovalId != granted.ApprovalId)
                        .ToArray(),
                    Plan = existing.Plan?.ApprovalId == granted.ApprovalId
                        ? existing.Plan with
                        {
                            Status = PlanReviewStatus.Approved,
                            DecisionReason = null,
                        }
                        : existing.Plan,
                    Mutation = existing.Mutation?.ApprovalId == granted.ApprovalId
                        ? existing.Mutation with
                        {
                            IsApproved = true,
                            DecisionReason = null,
                        }
                        : existing.Mutation,
                },
                ApprovalDenied denied when existing is not null => existing with
                {
                    PendingApprovals = approvals
                        .Where(item => item.ApprovalId != denied.ApprovalId)
                        .ToArray(),
                    Plan = existing.Plan?.ApprovalId == denied.ApprovalId
                        ? existing.Plan with
                        {
                            Status = PlanReviewStatus.Rejected,
                            DecisionReason = denied.Reason,
                        }
                        : existing.Plan,
                    Mutation = existing.Mutation?.ApprovalId == denied.ApprovalId
                        ? existing.Mutation with
                        {
                            DecisionReason = denied.Reason,
                        }
                        : existing.Mutation,
                },
                MutationApplied applied when existing is not null
                    && existing.Mutation?.MutationSetId == applied.MutationSetId => existing with
                    {
                        Mutation = existing.Mutation with
                        {
                            IsApproved = true,
                            IsApplied = true,
                        },
                    },
                MutationSetRolledBack rolledBack when existing is not null
                    && existing.Mutation?.MutationSetId == rolledBack.MutationSetId => existing with
                    {
                        Mutation = existing.Mutation with
                        {
                            IsRolledBack = true,
                            DecisionReason = null,
                        },
                    },
                RunTransitioned transition when existing is not null => existing with
                {
                    Phase = transition.Destination,
                },
                RunTransitionFailed failed when existing is not null => existing with
                {
                    Error = failed.Reason,
                },
                DiagnosticObserved diagnostic when existing is not null => existing with
                {
                    Error = $"{diagnostic.Code}: {diagnostic.Message}",
                    Diagnostics = diagnostic.StructuredDiagnostic is null
                        ? existing.Diagnostics
                        : [.. diagnostics.TakeLast(99), diagnostic.StructuredDiagnostic],
                },
                TestRunCompleted tests when existing is not null
                    && tests.StructuredResult is not null => existing with
                    {
                        TestValidation = CopyTestValidation(tests.StructuredResult),
                    },
                RunCompleted completed when existing is not null && completed.Succeeded => existing with
                {
                    Phase = RunPhase.Completion,
                },
                _ => existing,
            };
            if (next is not null)
            {
                _sessions[key] = next;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TProjection?> GetAsync<TProjection>(
        ProjectionKey key,
        CancellationToken cancellationToken = default)
        where TProjection : class, IProjection
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var snapshot = _sessions.TryGetValue(key, out var value)
                ? value with
                {
                    Activity = value.Activity.ToArray(),
                    TargetFrameworks = value.TargetFrameworks.ToArray(),
                    ToolActivity = value.ToolActivity.ToArray(),
                    Diagnostics = value.Diagnostics.ToArray(),
                    TestValidation = value.TestValidation is null
                        ? null
                        : CopyTestValidation(value.TestValidation),
                    PendingApprovals = value.PendingApprovals.ToArray(),
                    AcceptanceCriteria = value.AcceptanceCriteria.ToArray(),
                    Plan = value.Plan is null
                        ? null
                        : value.Plan with
                        {
                            Plan = CopyPlan(value.Plan.Plan),
                        },
                    ContextInspection = value.ContextInspection is null
                        ? null
                        : CopyInspection(value.ContextInspection),
                    Mutation = value.Mutation is null
                        ? null
                        : value.Mutation with
                        {
                            Preview = CopyPreview(value.Mutation.Preview),
                        },
                }
                : null;
            return Task.FromResult(snapshot as TProjection);
        }
    }

    /// <summary>Atomically publishes a fully detached session projection.</summary>
    public void ReplaceSession(SessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            _sessions[projection.Key] = projection with
            {
                Activity = projection.Activity.ToArray(),
                TargetFrameworks = projection.TargetFrameworks.ToArray(),
                ToolActivity = projection.ToolActivity.ToArray(),
                Diagnostics = projection.Diagnostics.ToArray(),
                PendingApprovals = projection.PendingApprovals.ToArray(),
                AcceptanceCriteria = projection.AcceptanceCriteria.ToArray(),
            };
        }
    }

    /// <summary>Gets one detached session projection synchronously for atomic transition preparation.</summary>
    public SessionProjection? GetSession(SessionId sessionId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        lock (_gate)
        {
            var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
            return _sessions.TryGetValue(key, out var projection)
                ? projection with
                {
                    Activity = projection.Activity.ToArray(),
                    TargetFrameworks = projection.TargetFrameworks.ToArray(),
                    ToolActivity = projection.ToolActivity.ToArray(),
                    Diagnostics = projection.Diagnostics.ToArray(),
                    PendingApprovals = projection.PendingApprovals.ToArray(),
                    AcceptanceCriteria = projection.AcceptanceCriteria.ToArray(),
                }
                : null;
        }
    }

    /// <summary>Clears stale context occupancy from every shared session projection.</summary>
    public void InvalidateContextInspections()
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Keys.ToArray())
            {
                _sessions[key] = _sessions[key] with { ContextInspection = null };
            }
        }
    }

    private static ContextInspectionProjection CopyInspection(
        ContextInspectionProjection inspection)
    {
        return inspection with
        {
            TokensByCategory = new ReadOnlyDictionary<string, int>(
                inspection.TokensByCategory.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)),
            Evidence = inspection.Evidence.ToArray(),
            PromptAssets = inspection.PromptAssets.ToArray(),
            ModelRationale = inspection.ModelRationale.ToArray(),
            Reductions = inspection.Reductions.ToArray(),
            ConversationItems = inspection.ConversationItems.Select(item => item with
            {
                SourceMessageIds = item.SourceMessageIds.ToArray(),
                SourceRunIds = item.SourceRunIds.ToArray(),
                SourceEvidenceIds = item.SourceEvidenceIds.ToArray(),
            }).ToArray(),
        };
    }

    private static ImplementationPlan CopyPlan(ImplementationPlan plan)
    {
        return plan with
        {
            Steps = plan.Steps.Select(step => step with
            {
                FileIntents = step.FileIntents.Select(intent => intent with { }).ToArray(),
                Validation = step.Validation.ToArray(),
            }).ToArray(),
            Risks = plan.Risks.ToArray(),
            OutstandingQuestions = plan.OutstandingQuestions.ToArray(),
        };
    }

    private static MutationPreview CopyPreview(MutationPreview preview)
    {
        return preview with
        {
            Changes = preview.Changes.ToArray(),
            LifecycleChanges = preview.LifecycleChanges.ToArray(),
        };
    }

    private static TestValidationResult CopyTestValidation(TestValidationResult validation)
    {
        return validation with
        {
            Selection = validation.Selection with
            {
                Projects = validation.Selection.Projects
                    .Select(project => project with
                    {
                        TargetFrameworks = project.TargetFrameworks.ToArray(),
                        ProjectReferences = project.ProjectReferences.ToArray(),
                    })
                    .ToArray(),
                TestCases = validation.Selection.TestCases.ToArray(),
                Rationale = validation.Selection.Rationale.ToArray(),
                RelatedMutationIds = validation.Selection.RelatedMutationIds.ToArray(),
            },
            Results = validation.Results.Select(result => result with
            {
                Project = result.Project with
                {
                    TargetFrameworks = result.Project.TargetFrameworks.ToArray(),
                    ProjectReferences = result.Project.ProjectReferences.ToArray(),
                },
                RelatedMutationIds = result.RelatedMutationIds.ToArray(),
            }).ToArray(),
        };
    }
}
