namespace Threadsmith.DotNet;

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Analyzes exact writer candidates through the existing compiler queue and promotes only verified inputs.</summary>
public sealed partial class SemanticEngine
{
    private const int MaximumEditDiagnostics = 64;
    private const int MaximumReportedEditDiagnostics = 8;
    private const int MaximumDiagnosticProjects = 32;
    private readonly Dictionary<ProjectId, EditDiagnosticBasis> _editDiagnosticBases = [];
    private readonly HashSet<ProjectId> _editedProjects = [];
    private EditCandidate? _editCandidate;
    private long _editDiagnosticPasses;
    private long _editCandidatePromotions;
    private bool _hasAppliedEdits;

    /// <summary>Gets aggregate counters without retaining source or model payloads.</summary>
    internal (long DiagnosticPasses, long CandidatePromotions) EditAnalysisStatistics => (Interlocked.Read(ref _editDiagnosticPasses), Interlocked.Read(ref _editCandidatePromotions));

    /// <summary>Checks exact writer endpoints against the shared compiler input policy.</summary>
    internal bool HasSemanticInputs(string repositoryPath, MutationEffectSnapshot snapshot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var relevant = false;
            foreach (var endpoint in snapshot.Endpoints)
            {
                var path = Path.GetFullPath(endpoint.RelativePath, repositoryPath);
                if (!IsPathWithinRoot(path, repositoryPath))
                {
                    throw new UnauthorizedAccessException("Candidate source must remain inside its repository.");
                }

                relevant |= SemanticRefreshPathPolicy.Classify(repositoryPath, path, _refreshInventory) != SemanticInputKind.None;
            }

            return relevant;
        }
    }

    /// <summary>Admits exact candidate analysis with bounded immediate observation.</summary>
    internal async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(
        ApplySourceEditCommand command,
        string repositoryPath,
        MutationEffectSnapshot snapshot,
        TimeSpan immediateAllowance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(immediateAllowance, TimeSpan.Zero);
        EditCandidate candidate;
        EditCandidate? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!HasSemanticInputs(repositoryPath, snapshot))
            {
                return null;
            }

            var source = _solution;
            if (source is null)
            {
                return new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["No compiler-aware workspace is available."] };
            }

            var replacement = source;
            var changed = new Dictionary<string, string>(StringComparerForCurrentPlatform());
            var owners = new HashSet<ProjectId>();
            var documents = new HashSet<DocumentId>();
            var syntaxOnly = new Dictionary<string, SourceText>(StringComparerForCurrentPlatform());
            var omissions = new List<string>
            {
                "Compiler diagnostics are advisory; analyzer execution and build/test validation are separate.",
                "Coverage includes evaluated project instances only; unevaluated target frameworks are omitted.",
            };
            var promotable = true;

            // The writer owns exact filename spelling and absence checks. Compiler inputs use
            // filesystem identity, so a case-only move contributes its surviving byte identity once.
            var semanticEndpoints = snapshot.Endpoints
                .Where(endpoint => SemanticRefreshPathPolicy.Classify(
                    repositoryPath,
                    Path.GetFullPath(endpoint.RelativePath, repositoryPath),
                    _refreshInventory) != SemanticInputKind.None)
                .GroupBy(endpoint => Path.GetFullPath(endpoint.RelativePath, repositoryPath), StringComparerForCurrentPlatform())
                .Select(group => group.FirstOrDefault(endpoint => endpoint.AfterSha256 is not null) ?? group.First())
                .ToArray();
            foreach (var endpoint in semanticEndpoints)
            {
                var path = Path.GetFullPath(endpoint.RelativePath, repositoryPath);
                if (!IsPathWithinRoot(path, repositoryPath))
                {
                    throw new UnauthorizedAccessException("Candidate source must remain inside its repository.");
                }

                var ids = source.GetDocumentIdsWithFilePath(path);
                if (!IsCSharpSourcePath(path) || endpoint.AfterSha256 is null)
                {
                    promotable = false;
                    omissions.Add(_prompts.Get(PromptFileNames.ContextSourceEditMembershipRefresh));
                    continue;
                }

                var bytes = endpoint.FinalBytes ?? throw new InvalidOperationException("Exact candidate bytes are unavailable.");
                if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(endpoint.AfterSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Candidate bytes do not match the writer receipt.");
                }

                using var stream = new MemoryStream(bytes, writable: false);
                var text = SourceText.From(stream, encoding: null, checksumAlgorithm: SourceHashAlgorithm.Sha256);
                if (ids.Length == 0)
                {
                    promotable = false;
                    syntaxOnly.Add(path, text);
                    omissions.Add(_prompts.Get(PromptFileNames.ContextSourceEditNewSourceParseOptions));
                    continue;
                }

                changed.Add(path, endpoint.AfterSha256);
                foreach (var id in ids)
                {
                    replacement = replacement.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                    owners.Add(id.ProjectId);
                    documents.Add(id);
                }
            }

            var graph = source.GetProjectDependencyGraph();
            var affected = owners.Concat(owners.SelectMany(graph.GetProjectsThatTransitivelyDependOnThisProject)).Distinct().ToArray();
            var carried = _editedProjects.Where(id => source.GetProject(id) is not null && !affected.Contains(id)).ToArray();
            var carryLimit = Math.Max(0, MaximumDiagnosticProjects - affected.Length);
            var scope = affected.Concat(carried.Take(carryLimit)).ToArray();
            if (carried.Length > carryLimit)
            {
                omissions.Add(_prompts.Render(
                    PromptFileNames.ContextSourceEditOmittedProjectCoverage,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ProjectCount"] = (carried.Length - carryLimit).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }

            candidate = new(
                command,
                repositoryPath,
                source,
                replacement,
                changed,
                documents,
                scope,
                promotable,
                new SourceEditAnalysis
                {
                    EffectId = command.EffectId,
                    SourceGeneration = _solutionGeneration,
                    Pending = scope.Length > 0,
                    ProjectsInScope = scope.Length,
                    Omissions = omissions.Distinct(StringComparer.Ordinal).ToArray(),
                },
                _engineLifetime.Token)
            {
                PreviouslyEdited = [.. _editedProjects],
                SyntaxOnly = syntaxOnly,
                AppliedEndpoints = semanticEndpoints.ToDictionary(endpoint => Path.GetFullPath(endpoint.RelativePath, repositoryPath), endpoint => endpoint.AfterSha256, StringComparerForCurrentPlatform()),
            };
            previous = _editCandidate;
            _editCandidate = candidate;
        }

        previous?.Cancel();
        candidate.Work = AnalyzeEditCandidateAsync(candidate);
        try
        {
#pragma warning disable VSTHRD003 // The candidate owns its single continuation; this caller observes only the immediate allowance.
            await candidate.Work.WaitAsync(immediateAllowance, cancellationToken);
#pragma warning restore VSTHRD003
        }
        catch (TimeoutException)
        {
        }

        lock (_gate)
        {
            return candidate.Result;
        }
    }

    /// <summary>Returns only the exact applied edit owner's latest bounded feedback.</summary>
    internal SourceEditAnalysis? GetLatestEditAnalysis(SessionId sessionId, RunId runId, Guid effectId)
    {
        lock (_gate)
        {
            return _editCandidate is { Applied: true } candidate && candidate.Command.SessionId == sessionId
                && candidate.Command.RunId == runId && candidate.Command.EffectId == effectId
                ? candidate.Result with
                {
                    Pending = candidate.Result.Pending || candidate.Result.CommittedGeneration is null,
                    Obsolete = candidate.Result.Obsolete || (!ReferenceEquals(_solution, candidate.Base) && !ReferenceEquals(_solution, candidate.Solution)),
                } : null;
        }
    }

    /// <summary>Records the independent authoritative disk outcome.</summary>
    internal void ConfirmEditApplied(Guid effectId)
    {
        IReadOnlyDictionary<string, string?>? verifiedInputs = null;
        long? verifiedGeneration = null;
        lock (_gate)
        {
            if (_editCandidate is { } candidate && candidate.Command.EffectId == effectId)
            {
                candidate.Applied = true;
                _hasAppliedEdits = true;
                _editedProjects.UnionWith(candidate.Projects);
                if (candidate.VerifiedGraphGeneration == _solutionGeneration)
                {
                    verifiedInputs = candidate.AppliedEndpoints;
                    verifiedGeneration = candidate.VerifiedGraphGeneration;
                }
            }
        }

        if (verifiedInputs is not null)
        {
            ContinueEditAfterGraphRefresh(verifiedInputs, effectId, verifiedGeneration);
        }
    }

    /// <summary>Retires a candidate without publishing source state.</summary>
    internal void DiscardEditCandidate(Guid effectId)
    {
        EditCandidate? discarded = null;
        lock (_gate)
        {
            if (_editCandidate is { } candidate && candidate.Command.EffectId == effectId)
            {
                discarded = candidate;
                _editCandidate = null;
            }
        }

        discarded?.Cancel();
    }

    /// <summary>Continues advisory coverage after the refresh owner verifies a graph replacement and the applied endpoints.</summary>
    internal void ContinueEditAfterGraphRefresh(IReadOnlyDictionary<string, string?> verifiedInputs, Guid? expectedEffectId = null, long? expectedGeneration = null)
    {
        EditCandidate replacement;
        lock (_gate)
        {
            if (_editCandidate is not { } previous || _solution is null || !previous.Result.Obsolete
                || (expectedEffectId is { } expected && previous.Command.EffectId != expected)
                || (expectedGeneration is { } generation && generation != _solutionGeneration)
                || previous.AppliedEndpoints.Count == 0
                || previous.AppliedEndpoints.Any(endpoint => !verifiedInputs.TryGetValue(endpoint.Key, out var identity)
                    || !string.Equals(identity, endpoint.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            previous.VerifiedGraphGeneration = _solutionGeneration;
            if (!previous.Applied)
            {
                return;
            }

            // Graph changes can alter ownership and references. The evaluated graph is the conservative scope.
            replacement = new(
                previous.Command,
                previous.RepositoryPath,
                _solution,
                _solution,
                [],
                [],
                [.. _solution.ProjectIds],
                false,
                new SourceEditAnalysis
                {
                    EffectId = previous.Command.EffectId,
                    SourceGeneration = previous.Result.SourceGeneration,
                    CommittedGeneration = _solutionGeneration,
                    Revision = previous.Result.Revision + 1,
                    Pending = true,
                    ProjectsInScope = _solution.ProjectIds.Count,
                    Omissions = ["Graph replacement requires conservative evaluated-project coverage. Previous and initial error origins are unavailable across this replacement.", "Compiler diagnostics are advisory; unevaluated target frameworks, analyzers, build and tests remain separate."],
                },
                _engineLifetime.Token)
            {
                Applied = true,
                CommittedOnly = true,
                AppliedEndpoints = previous.AppliedEndpoints,
                PreviouslyEdited = [.. _solution.ProjectIds],
            };
            _editCandidate = replacement;
            _editedProjects.UnionWith(_solution.ProjectIds);
        }

        replacement.Work = AnalyzeEditCandidateAsync(replacement);
    }

    private async Task AnalyzeEditCandidateAsync(EditCandidate candidate)
    {
        var started = Stopwatch.GetTimestamp();
        var checkId = SemanticCheckId.New();
        var outcome = SemanticCheckOutcome.Completed;
        var phase = candidate.CommittedOnly ? SemanticCheckPhase.PostMutation : SemanticCheckPhase.PreMutation;
        try
        {
            await _events.PublishAsync(
                new SemanticCheckStarted(candidate.Command.SessionId, DateTimeOffset.UtcNow, candidate.Command.RunId, checkId, phase, "advisory edit analysis"),
                candidate.Token);
            var syntax = await RunEditCompilerOperationAsync(candidate, async token =>
            {
                var findings = new List<EditFinding>();
                foreach (var id in candidate.Documents)
                {
                    var document = candidate.Solution.GetDocument(id);
                    var tree = document is null ? null : await document.GetSyntaxTreeAsync(token);
                    if (tree is not null && document is not null)
                    {
                        findings.AddRange(ProjectEditFindings(tree.GetDiagnostics(token), document.Project, candidate.RepositoryPath));
                    }
                }

                foreach (var (path, text) in candidate.SyntaxOnly)
                {
                    var tree = CSharpSyntaxTree.ParseText(text, path: path, cancellationToken: token);
                    findings.AddRange(ProjectEditFindings(tree.GetDiagnostics(token), "unassigned", "unknown", candidate.RepositoryPath).Select(item => item with { Unassigned = true }));
                }

                return findings.Take(MaximumEditDiagnostics).ToArray();
            });
            UpdateEditResult(candidate, result => result with { Diagnostics = syntax.Select(item => item.Diagnostic).Take(MaximumReportedEditDiagnostics).ToArray(), CurrentErrors = syntax.Length });
            var syntaxOnlyFindings = syntax.Where(item => item.Unassigned).ToArray();
            foreach (var projectId in candidate.Projects)
            {
                var result = await RunEditCompilerOperationAsync(candidate, token => AnalyzeEditProjectAsync(candidate, projectId, token));
                lock (_gate)
                {
                    if (!IsEditCandidateCurrent(candidate))
                    {
                        return;
                    }

                    var remaining = Math.Max(0, MaximumEditDiagnostics - candidate.ProjectResults.Values.Sum(item => item.Current.Length));
                    candidate.ProjectResults[projectId] = result with { Current = [.. result.Current.Take(remaining)] };
                    var results = candidate.ProjectResults.Values.ToArray();
                    candidate.Result = candidate.Result with
                    {
                        Revision = candidate.Result.Revision + 1,
                        ProjectsAnalyzed = results.Length,
                        CurrentErrors = syntaxOnlyFindings.Length + results.Sum(item => item.Count),
                        NewErrors = results.Sum(item => item.New),
                        ResolvedErrors = results.Sum(item => item.Resolved),
                        Diagnostics = syntaxOnlyFindings.Concat(results.SelectMany(item => item.Current)).Select(item => item.Diagnostic).Take(MaximumReportedEditDiagnostics).ToArray(),
                        Omissions = candidate.Result.Omissions.Concat(results.SelectMany(item => item.Omissions)).Distinct(StringComparer.Ordinal).Take(16).ToArray(),
                    };
                }
            }

            UpdateEditResult(candidate, result => result with { Pending = false });
        }
        catch (OperationCanceledException)
        {
            outcome = SemanticCheckOutcome.Cancelled;
            UpdateEditResult(candidate, result => result with { Pending = false, Omissions = [.. result.Omissions, "Remaining advisory analysis was cancelled or exceeded its bounded lifetime."] });
        }
        catch (Exception)
        {
            outcome = SemanticCheckOutcome.Degraded;
            UpdateEditResult(candidate, result => result with { Pending = false, Omissions = [.. result.Omissions, "Scoped compiler analysis is unavailable for the remaining coverage."] });
        }
        finally
        {
            try
            {
                var completed = new SemanticCheckCompleted(
                    candidate.Command.SessionId,
                    DateTimeOffset.UtcNow,
                    candidate.Command.RunId,
                    checkId,
                    phase,
                    "advisory edit analysis",
                    outcome,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    "Advisory analysis finished; consult versioned coverage and omissions.");
                await _events.PublishAsync(completed, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Advisory analysis activity delivery failed");
            }
        }
    }

    private async Task<T> RunEditCompilerOperationAsync<T>(EditCandidate candidate, Func<CancellationToken, Task<T>> operation)
    {
        while (true)
        {
            SemanticCompilationCoordinator preparation;
            Solution source;
            lock (_gate)
            {
                if (!IsEditCandidateCurrent(candidate))
                {
                    throw new OperationCanceledException(candidate.Token);
                }

                source = _solution ?? throw new InvalidOperationException("The semantic source is unavailable.");
                preparation = _preparation ?? throw new InvalidOperationException("The compiler queue is unavailable.");
            }

            Task<T>? retained = null;
            try
            {
                return await preparation.RunAsync(
                    async token =>
                {
                    try
                    {
                        return await RunNonCooperativeAsync(operation, token, sourceSolution: source, retainedCandidateLifetime: candidate.Token, retainActualOperation: task => retained = task);
                    }
                    catch (SemanticOperationAbandonedException exception)
                    {
                        retained = (Task<T>)exception.Completion;
                        throw;
                    }
                },
                    candidate.Token);
            }
            catch (Exception) when (retained is not null)
            {
#pragma warning disable VSTHRD003 // The exact candidate owns this already-admitted compiler task across matching promotion.
                return await retained.WaitAsync(candidate.Token);
#pragma warning restore VSTHRD003
            }
            catch (InvalidOperationException) when (!candidate.Token.IsCancellationRequested && ReferenceEquals(source, candidate.Base))
            {
#pragma warning disable VSTHRD003 // Refresh resolves this candidate's promotion fence; denial or supersession cancels its owner.
                await candidate.Promotion.Task.WaitAsync(candidate.Token);
#pragma warning restore VSTHRD003
            }
        }
    }

    private async Task<EditProjectResult> AnalyzeEditProjectAsync(EditCandidate candidate, ProjectId id, CancellationToken token)
    {
        var baselineProject = candidate.Base.GetProject(id) ?? throw new InvalidOperationException("The baseline project is unavailable.");
        var project = candidate.Solution.GetProject(id) ?? throw new InvalidOperationException("The candidate project is unavailable.");
        if (candidate.CommittedOnly)
        {
            var current = await GetEditProjectFindingsAsync(project, candidate.RepositoryPath, token);
            return new(current, current.Length, 0, 0, current.Length == MaximumEditDiagnostics ? ["Diagnostic retention is bounded; error counts may be incomplete."] : []);
        }

        var baselineVersion = await baselineProject.GetDependentVersionAsync(token);
        EditDiagnosticBasis? basis;
        lock (_gate)
        {
            _editDiagnosticBases.TryGetValue(id, out basis);
        }

        var before = basis?.Version == baselineVersion ? basis.Previous : await GetEditProjectFindingsAsync(baselineProject, candidate.RepositoryPath, token);
        var version = await project.GetDependentVersionAsync(token);
        var after = basis?.Version == version ? basis.Previous
            : baselineVersion == version ? before
            : await GetEditProjectFindingsAsync(project, candidate.RepositoryPath, token);
        var beforeCounts = CountEditFindings(before);
        var afterCounts = CountEditFindings(after);
        Dictionary<string, int>? initial;
        lock (_gate)
        {
            initial = basis?.Initial ?? (!candidate.PreviouslyEdited.Contains(id) && before.Length < MaximumEditDiagnostics ? CountEditFindings(before) : null);
        }

        var remainingInitial = initial is null ? null : new Dictionary<string, int>(initial, StringComparer.Ordinal);
        var projected = after.Select(finding =>
        {
            var knownInitial = remainingInitial is not null && remainingInitial.TryGetValue(finding.Fingerprint, out var count) && count > 0;
            if (knownInitial && remainingInitial is not null)
            {
                remainingInitial[finding.Fingerprint]--;
            }

            return finding with { Diagnostic = finding.Diagnostic with { Origin = initial is null ? "unknown" : knownInitial ? "initial" : "introduced" } };
        }).ToArray();
        lock (_gate)
        {
            if (IsEditCandidateCurrent(candidate))
            {
                if (_editDiagnosticBases.Count >= MaximumDiagnosticProjects && !_editDiagnosticBases.ContainsKey(id))
                {
                    _editDiagnosticBases.Remove(_editDiagnosticBases.Keys.First());
                }

                _editDiagnosticBases[id] = new(version, after, initial);
            }
        }

        var omissions = after.Length == MaximumEditDiagnostics || before.Length == MaximumEditDiagnostics
            ? new[] { "Diagnostic retention is bounded; error counts and deltas may be incomplete." } : [];
        return new(
            projected,
            after.Length,
            afterCounts.Sum(pair => Math.Max(0, pair.Value - beforeCounts.GetValueOrDefault(pair.Key))),
            beforeCounts.Sum(pair => Math.Max(0, pair.Value - afterCounts.GetValueOrDefault(pair.Key))),
            omissions);
    }

    private async Task<EditFinding[]> GetEditProjectFindingsAsync(Project project, string repositoryPath, CancellationToken token)
    {
        var compilation = await project.GetCompilationAsync(token) ?? throw new InvalidOperationException("Compilation unavailable.");
        EditFinding[] findings = [.. ProjectEditFindings(compilation.GetDiagnostics(token), project, repositoryPath).Take(MaximumEditDiagnostics)];
        Interlocked.Increment(ref _editDiagnosticPasses);
        SemanticLoadMetrics.EditDiagnosticPasses.Add(1);
        return findings;
    }

    private static IEnumerable<EditFinding> ProjectEditFindings(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics, Project project, string repositoryPath)
    {
        var framework = project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.TargetFramework", out var value)
            ? value : "unknown";
        return ProjectEditFindings(diagnostics, project.Name, framework, repositoryPath);
    }

    private static IEnumerable<EditFinding> ProjectEditFindings(IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics, string project, string framework, string repositoryPath)
    {
        foreach (var diagnostic in diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
        {
            var path = diagnostic.Location.SourceTree?.FilePath;
            var file = path is null ? null : ToRepositoryRelativePath(repositoryPath, Path.GetFullPath(path));
            var message = diagnostic.GetMessage();
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', diagnostic.Id, file, message))));
            var line = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 : (int?)null;
            yield return new(fingerprint, new(diagnostic.Id, BoundEditText(message, 384), file is null ? null : BoundEditText(file, 256), line, BoundEditText(project, 128), BoundEditText(framework, 64), "unknown"));
        }
    }

    private static string BoundEditText(string value, int maximum)
    {
        return value.Length > maximum ? value[..maximum] : value;
    }

    private static Dictionary<string, int> CountEditFindings(IEnumerable<EditFinding> findings)
    {
        return findings.GroupBy(item => item.Fingerprint, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private bool IsEditCandidateCurrent(EditCandidate candidate)
    {
        return ReferenceEquals(_editCandidate, candidate) && !candidate.Token.IsCancellationRequested
            && (ReferenceEquals(_solution, candidate.Base) || ReferenceEquals(_solution, candidate.Solution));
    }

    private void UpdateEditResult(EditCandidate candidate, Func<SourceEditAnalysis, SourceEditAnalysis> update)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_editCandidate, candidate) && (ReferenceEquals(_solution, candidate.Base) || ReferenceEquals(_solution, candidate.Solution)))
            {
                candidate.Result = update(candidate.Result) with { Revision = candidate.Result.Revision + 1 };
            }
        }
    }

    private void ResetEditAnalysisInputs()
    {
        _editDiagnosticBases.Clear();
        _editedProjects.Clear();
        if (_hasAppliedEdits && _solution is not null)
        {
            // A graph replacement cannot silently turn introduced errors into a new initial basis.
            _editedProjects.UnionWith(_solution.ProjectIds);
        }

        if (_editCandidate is { } candidate)
        {
            candidate.Result = candidate.Result with { Obsolete = true, Pending = candidate.Applied, Revision = candidate.Result.Revision + 1 };
            candidate.Cancel();
        }
    }

    private sealed record EditFinding(string Fingerprint, SourceEditDiagnostic Diagnostic, bool Unassigned = false);

    private sealed record EditDiagnosticBasis(VersionStamp Version, EditFinding[] Previous, Dictionary<string, int>? Initial);

    private sealed record EditProjectResult(EditFinding[] Current, int Count, int New, int Resolved, IReadOnlyList<string> Omissions);

    private sealed class EditCandidate
    {
        private readonly CancellationTokenSource _lifetime;
        private int _cancelled;

        public EditCandidate(
            ApplySourceEditCommand command,
            string repositoryPath,
            Solution baseline,
            Solution solution,
            Dictionary<string, string> endpoints,
            HashSet<DocumentId> documents,
            ProjectId[] projects,
            bool promotable,
            SourceEditAnalysis result,
            CancellationToken engineLifetime)
        {
            Command = command;
            RepositoryPath = repositoryPath;
            Base = baseline;
            Solution = solution;
            Endpoints = endpoints;
            Documents = documents;
            Projects = projects;
            Promotable = promotable;
            Result = result;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(engineLifetime);
            _lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            Token = _lifetime.Token;
        }

        public ApplySourceEditCommand Command { get; }

        public string RepositoryPath { get; }

        public Solution Base { get; }

        public Solution Solution { get; }

        public Dictionary<string, string> Endpoints { get; }

        public HashSet<DocumentId> Documents { get; }

        public Dictionary<string, SourceText> SyntaxOnly { get; init; } = [];

        public IReadOnlyDictionary<string, string?> AppliedEndpoints { get; init; } = new Dictionary<string, string?>();

        public bool CommittedOnly { get; init; }

        public long? VerifiedGraphGeneration { get; set; }

        public ProjectId[] Projects { get; }

        public HashSet<ProjectId> PreviouslyEdited { get; init; } = [];

        public bool Promotable { get; }

        public bool Applied { get; set; }

        public SourceEditAnalysis Result { get; set; }

        public CancellationToken Token { get; }

        public Task Work { get; set; } = Task.CompletedTask;

        public TaskCompletionSource Promotion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<ProjectId, EditProjectResult> ProjectResults { get; } = [];

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                _ = _lifetime.CancelAsync().ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        _lifetime.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }
}
