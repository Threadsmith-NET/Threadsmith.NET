namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Optional analyzer work shares candidate identity, queue, retirement and feedback with compiler analysis.</summary>
public sealed partial class SemanticEngine
{
    private readonly SemaphoreSlim _advisorySlots;
    private readonly TimeProvider _analyzerTimeProvider;
    private readonly HashSet<string> _warmAnalyzerIdentities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabledAnalyzerIdentities = new(StringComparer.Ordinal);
    private bool _automaticAnalyzerFailure;
    private long _advisoryOperationsStarted;

    /// <summary>Allows deterministic fixture identities in tests without broadening the production qualification catalog.</summary>
    internal Func<EditAnalyzerIdentity, bool> AnalyzerQualification { get; init; } = IsQualifiedEditAnalyzer;

    /// <summary>Admitted physical operations, including work abandoned by its caller.</summary>
    internal int ActiveAdvisoryOperations => _editOptions.Analyzers.MaximumConcurrentOperations - _advisorySlots.CurrentCount;

    /// <summary>Physical operation entries, used to verify coalescing independently of occupied permits.</summary>
    internal long GetAdvisoryOperationsStarted() => Interlocked.Read(ref _advisoryOperationsStarted);

    private static bool IsQualifiedEditAnalyzer(EditAnalyzerIdentity identity)
    {
        return AnalyzerIdentityKey(identity) is
            "Microsoft.CodeAnalysis.CSharp.NetAnalyzers/10.0.403.0/Microsoft.NetCore.CSharp.Analyzers.Runtime.CSharpForwardCancellationTokenToInvocationsAnalyzer"
            or "Microsoft.CodeAnalysis.NetAnalyzers/10.0.403.0/Microsoft.NetCore.Analyzers.Tasks.UseValueTasksCorrectlyAnalyzer"
            or "Microsoft.VisualStudio.Threading.Analyzers.CSharp/18.7.23.44529/Microsoft.VisualStudio.Threading.Analyzers.VSTHRD002UseJtfRunAnalyzer"
            or "Microsoft.VisualStudio.Threading.Analyzers/18.7.23.44529/Microsoft.VisualStudio.Threading.Analyzers.VSTHRD100AsyncVoidMethodAnalyzer";
    }

    private async Task AnalyzeAutomaticEditAnalyzersAsync(EditCandidate candidate)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var checkId = SemanticCheckId.New();
        var phase = candidate.CommittedOnly ? SemanticCheckPhase.PostMutation : SemanticCheckPhase.PreMutation;
        var outcome = SemanticCheckOutcome.Completed;
        var options = _editOptions.Analyzers with { DiagnosticIds = [.. _editOptions.Analyzers.DiagnosticIds.Where(id => !_diagnosticReporting.IsExcluded(id))] };
        var excluded = options.DiagnosticIds.Length != _editOptions.Analyzers.DiagnosticIds.Length;
        var execution = new EditAnalyzerExecution(this, options);
        var admitted = false;
        UpdateEditResult(candidate, result => result with
        {
            Analyzers = new() { Status = "pending", DocumentsInScope = candidate.Documents.Count, OutputFiltered = excluded },
        });
        try
        {
            await _events.PublishAsync(
                new SemanticCheckStarted(candidate.Command.SessionId, DateTimeOffset.UtcNow, candidate.Command.RunId, checkId, phase, "advisory edit analyzers") { EffectId = candidate.Command.EffectId },
                CancellationToken.None);
            candidate.Token.ThrowIfCancellationRequested();
            if (candidate.Documents.Count == 0 || options.DiagnosticIds.Length == 0)
            {
                SetAnalyzerOmission(candidate, "skipped", _prompts.Get(PromptFileNames.ContextSourceEditAnalyzerSkipped));
                outcome = SemanticCheckOutcome.Skipped;
                return;
            }

            lock (_gate)
            {
                if (_automaticAnalyzerFailure)
                {
                    SetAnalyzerOmission(candidate, "unavailable", _prompts.Get(PromptFileNames.ContextSourceEditAnalyzerUnavailable));
                    outcome = SemanticCheckOutcome.Degraded;
                    return;
                }
            }

            // Await admission outside the worker pool. Cancelled candidates leave no queued advisory waiter.
            await _advisorySlots.WaitAsync(candidate.Token);
            admitted = true;
            var comparison = await AnalyzeAnalyzerCandidateCoreAsync(candidate.Base, candidate.Solution, [.. candidate.Documents], options, false, true, candidate, execution, candidate.Token);
            lock (_gate)
            {
                if (!IsEditCandidateCurrent(candidate))
                {
                    throw new OperationCanceledException(candidate.Token);
                }

                _warmAnalyzerIdentities.UnionWith(execution.Identities.Keys);
                PublishAnalyzerComparison(candidate, comparison, excluded);
            }
        }
        catch (OperationCanceledException) when (candidate.Token.IsCancellationRequested)
        {
            outcome = SemanticCheckOutcome.Cancelled;
            SetAnalyzerOmission(candidate, "cancelled", _prompts.Get(PromptFileNames.ContextSourceEditAnalyzerCancelled));
        }
        catch (OperationCanceledException)
        {
            outcome = SemanticCheckOutcome.Cancelled;
            lock (_gate)
            {
                // A driver executes a selected group; a timeout cannot identify the guilty callback safely.
                _disabledAnalyzerIdentities.UnionWith(execution.Identities.Keys);
                _automaticAnalyzerFailure |= execution.Started && execution.Identities.IsEmpty;
            }

            SetAnalyzerOmission(candidate, "timed-out", _prompts.Get(PromptFileNames.ContextSourceEditAnalyzerTimedOut));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Optional edit analyzer execution failed");
            outcome = SemanticCheckOutcome.Degraded;
            lock (_gate)
            {
                var failed = execution.Failures.IsEmpty ? execution.Identities.Keys : execution.Failures.Keys;
                _disabledAnalyzerIdentities.UnionWith(failed);
                _automaticAnalyzerFailure |= execution.Started && execution.Identities.IsEmpty;
            }

            SetAnalyzerOmission(candidate, "failed", _prompts.Get(PromptFileNames.ContextSourceEditAnalyzerFailed));
        }
        finally
        {
            if (admitted)
            {
                execution.ReleaseUnusedAdmission();
            }

            try
            {
                SourceEditAnalysis result;
                lock (_gate)
                {
                    result = candidate.Result with { Obsolete = candidate.Result.Obsolete || !IsEditCandidateCurrent(candidate) };
                }

                await _events.PublishAsync(
                    new SemanticCheckCompleted(
                        candidate.Command.SessionId,
                        DateTimeOffset.UtcNow,
                        candidate.Command.RunId,
                        checkId,
                        phase,
                        "advisory edit analyzers",
                        outcome,
                        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        "Optional document analyzer work finished independently of compiler analysis; build and tests remain separate.")
                    { Analysis = result },
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Advisory analyzer activity delivery failed");
            }
        }
    }

    private void SetAnalyzerOmission(EditCandidate candidate, string status, string omission)
    {
        UpdateEditResult(candidate, result => result with
        {
            Analyzers = result.Analyzers with { Status = status, Omissions = [BoundEditText(omission, _editOptions.MaximumDiagnosticMessageCharacters)] },
        });
    }

    // Caller holds _gate; publish only complete, current comparison evidence into the existing revision stream.
    private void PublishAnalyzerComparison(EditCandidate candidate, EditAnalyzerComparison comparison, bool excluded)
    {
        var findings = new List<EditFinding>();
        var omissions = new List<string>();
        if (candidate.CommittedOnly)
        {
            omissions.Add(_prompts.Get(PromptFileNames.ContextSourceEditAnalyzerGraphReplacement));
        }

        var count = 0;
        var introduced = 0;
        var resolved = 0;
        var complete = true;
        var truncated = false;
        var filtered = excluded;
        var groups = candidate.Documents.GroupBy(id => id.ProjectId).ToArray();
        for (var index = 0; index < comparison.After.Count; index++)
        {
            var before = comparison.Before[index];
            var after = comparison.After[index];
            var comparable = !candidate.CommittedOnly && before.UnavailableIds.Count == 0 && after.UnavailableIds.Count == 0 && !before.Truncated && !after.Truncated;
            complete &= comparable;
            truncated |= after.Truncated;
            filtered |= before.OutputFiltered || after.OutputFiltered;
            count += after.ReportableCount ?? after.Diagnostics.Count;
            omissions.AddRange(after.Omissions);
            if (after.UnavailableIds.Count != 0)
            {
                omissions.Add(_prompts.Render(PromptFileNames.ContextSourceEditAnalyzerMissingIds, new Dictionary<string, string>(StringComparer.Ordinal) { ["DiagnosticIds"] = string.Join(", ", after.UnavailableIds) }));
            }

            var preceding = before.Diagnostics.GroupBy(AnalyzerFingerprint, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var current = after.Diagnostics.GroupBy(AnalyzerFingerprint, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            introduced += current.Sum(pair => Math.Max(0, pair.Value - preceding.GetValueOrDefault(pair.Key)));
            resolved += preceding.Sum(pair => Math.Max(0, pair.Value - current.GetValueOrDefault(pair.Key)));
            var projectId = groups[index].Key;
            var basis = GetEditProjectBasis(projectId);
            var initial = basis.AnalyzerInitial;
            if (basis.AnalyzerInitialDocuments?.SetEquals(groups[index]) != true)
            {
                initial = null;
            }

            if (initial is null && candidate.AnalyzerInitialKnown && comparable)
            {
                initial = preceding;
                _editDiagnosticBases[projectId] = basis with { AnalyzerInitial = initial, AnalyzerInitialDocuments = [.. groups[index]] };
            }

            var remainingInitial = initial?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var finding in after.Diagnostics)
            {
                var fingerprint = AnalyzerFingerprint(finding);
                var original = remainingInitial?.GetValueOrDefault(fingerprint) > 0;
                if (original && remainingInitial is not null)
                {
                    remainingInitial[fingerprint]--;
                }

                var severity = Enum.TryParse<Core.DiagnosticSeverity>(finding.Severity, out var parsed) ? parsed : Core.DiagnosticSeverity.Info;
                var file = string.IsNullOrEmpty(finding.Path) ? null : ToRepositoryRelativePath(candidate.RepositoryPath, Path.GetFullPath(finding.Path));
                var diagnostic = new SourceEditDiagnostic(
                    finding.Id,
                    BoundEditText(finding.Message, _editOptions.MaximumDiagnosticMessageCharacters),
                    file is null ? null : BoundEditText(file, _editOptions.MaximumDiagnosticPathCharacters),
                    finding.Line,
                    BoundEditText(after.Project, _editOptions.MaximumDiagnosticProjectCharacters),
                    "unknown",
                    !comparable || initial is null ? "unknown" : original ? "initial" : "introduced")
                {
                    Severity = severity,
                    Producer = "analyzer",
                    AnalyzerIdentity = BoundEditText(finding.Producer, _editOptions.MaximumDiagnosticMessageCharacters),
                };
                findings.Add(new(fingerprint, diagnostic, New: comparable && preceding.GetValueOrDefault(fingerprint) == 0));
            }
        }

        var retained = OrderEditFindings(findings).Take(_editOptions.MaximumRetainedDiagnostics).Select(item => item.Diagnostic).ToArray();
        var status = comparison.After.All(project => project.Analyzers.Count == 0) ? "unavailable" : complete ? "completed" : "partial";
        UpdateEditResult(candidate, result => result with
        {
            Analyzers = new()
            {
                Status = status,
                DocumentsInScope = candidate.Documents.Count,
                DocumentsAnalyzed = groups.Where((_, index) => comparison.After[index].Analyzers.Count > 0).Sum(group => group.Count()),
                ReportableFindings = count,
                NewFindings = complete ? introduced : null,
                ResolvedFindings = complete ? resolved : null,
                OutputFiltered = filtered,
                Truncated = truncated || count > retained.Length,
                Omissions = omissions.Distinct(StringComparer.Ordinal).Take(_editOptions.MaximumOmissions).Select(text => BoundEditText(text, _editOptions.MaximumDiagnosticMessageCharacters)).ToArray(),
            },
            Diagnostics = MergeEditDiagnostics(result.Diagnostics.Where(item => item.Producer != "analyzer"), retained),
        });
    }

    private static string AnalyzerFingerprint(EditAnalyzerFinding finding) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', finding.Producer, finding.Id, finding.Severity, finding.Path, finding.Message))));

    private sealed class EditAnalyzerExecution
    {
        private readonly SemanticEngine _owner;
        private readonly EditAnalyzerOptions _options;
        private bool _cold;
        private int _admission;

        public EditAnalyzerExecution(SemanticEngine owner, EditAnalyzerOptions options)
        {
            _owner = owner;
            _options = options;
        }

        public ConcurrentDictionary<string, byte> Identities { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, byte> Failures { get; } = new(StringComparer.Ordinal);

        public bool Started => Volatile.Read(ref _admission) is 1 or 2;

        public int BudgetMilliseconds => _cold ? Math.Max(_options.ColdStartBudgetMilliseconds, _options.BudgetMilliseconds) : _options.BudgetMilliseconds;

        public int MaximumRetainedDiagnostics => _owner._editOptions.MaximumRetainedDiagnostics;

        public string? Qualify(EditAnalyzerIdentity identity)
        {
            lock (_owner._gate)
            {
                if (_owner._disabledAnalyzerIdentities.Contains(AnalyzerIdentityKey(identity)))
                {
                    return _owner._prompts.Render(PromptFileNames.ContextSourceEditAnalyzerDisabledIdentity, new Dictionary<string, string>(StringComparer.Ordinal) { ["AnalyzerIdentity"] = identity.Type });
                }
            }

            return _owner.AnalyzerQualification(identity) ? null : _owner._prompts.Render(PromptFileNames.ContextSourceEditAnalyzerUnqualifiedIdentity, new Dictionary<string, string>(StringComparer.Ordinal) { ["AnalyzerIdentity"] = identity.Type + " " + identity.Version });
        }

        public void Selected(IReadOnlyList<EditAnalyzerIdentity> identities)
        {
            lock (_owner._gate)
            {
                foreach (var identity in identities)
                {
                    var key = AnalyzerIdentityKey(identity);
                    Identities.TryAdd(key, 0);
                    _cold |= !_owner._warmAnalyzerIdentities.Contains(key);
                }
            }
        }

        public void Failed(EditAnalyzerIdentity identity)
        {
            var key = AnalyzerIdentityKey(identity);
            Failures.TryAdd(key, 0);
            lock (_owner._gate)
            {
                // A callback may fault after its observer was superseded; health outlives that result.
                _owner._disabledAnalyzerIdentities.Add(key);
            }
        }

        public bool TryBegin()
        {
            if (Interlocked.CompareExchange(ref _admission, 1, 0) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _owner._advisoryOperationsStarted);
            return true;
        }

        public void ReleaseUnusedAdmission()
        {
            if (Interlocked.CompareExchange(ref _admission, 3, 0) == 0)
            {
                _owner._advisorySlots.Release();
            }
        }

        public void ActualCompletion(Task task)
        {
            // The handshake closes the race between caller cancellation and actual worker entry.
            // Once entered, the permit follows the entire physical task, including extension cleanup.
            _ = task.ContinueWith(
                _ =>
            {
                if (Interlocked.CompareExchange(ref _admission, 2, 1) == 1)
                {
                    _owner._advisorySlots.Release();
                }
            },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public bool IsExcluded(EditAnalyzerFinding finding) => _owner._diagnosticReporting.IsExcluded(finding.Id);

        public bool ShouldReport(EditAnalyzerFinding finding) => !finding.Suppressed && finding.Severity != "Hidden"
            && _options.DiagnosticIds.Contains(finding.Id, StringComparer.OrdinalIgnoreCase) && !IsExcluded(finding);
    }
}
