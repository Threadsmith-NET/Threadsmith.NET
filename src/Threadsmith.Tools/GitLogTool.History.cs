namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Owns the host's history cursor within the ordinary tool-operation lifetime.</summary>
public sealed partial class GitLogTool
{
    private async Task<GitLogResult> ReadCollectionPageAsync(GitLogRequest input, ToolInvocationContext context, CancellationToken cancellationToken)
    {
        if (context.RequestedBy != GitLogRequest.CollectionRequestor
            || context.IntelligenceRun?.Operation != RepositoryIntelligenceOperation.Onboard
            || context.IntelligenceRun.PendingHistoryPage is not { } page
            || context.OperationScope is not { } scope
            || input.Path is not null || input.Revision != "HEAD")
        {
            throw new UnauthorizedAccessException("Only host onboarding can advance chronological history.");
        }

        var state = scope.GetOrCreate(this, () => new HistoryState(_service, context.RepositoryPath, scope.CancellationToken));
        var result = await state.ReadAsync(input, page, context.IntelligenceRun.HistoryDepth, context.IntelligenceRun.ResumeHistoryRevision, cancellationToken);
        return result with
        {
            Commits = result.Commits.Select(commit =>
            {
                var paths = commit.ChangedPaths.Where(path => IsAllowed(path, context)).ToArray();
                return commit with { ChangedPaths = paths, OmittedPaths = commit.ChangedPaths.Count - paths.Length };
            }).ToArray(),
        };
    }

    private static bool IsAllowed(string path, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(path, context);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class HistoryState : IAsyncDisposable
    {
        private readonly IGitQueryService _service;
        private readonly string _repositoryPath;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IGitHistoryReader? _reader;
        private GitLogResult? _last;
        private int _lastPage = -1;
        private bool _failed;

        public HistoryState(IGitQueryService service, string repositoryPath, CancellationToken lifetime)
        {
            _service = service;
            _repositoryPath = repositoryPath;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        public async Task<GitLogResult> ReadAsync(GitLogRequest input, int page, int? depth, string? resumeRevision, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_failed)
                {
                    throw new InvalidOperationException("The history traversal failed and cannot be resumed.");
                }

                if (page == _lastPage && _last is not null)
                {
                    return _last;
                }

                if (_last is not null && page != _last.NextCollectionPage
                    && !(resumeRevision == _last.HistoryRevision && _last.CollectionPage < page))
                {
                    throw new InvalidOperationException("The chronological history page is out of sequence.");
                }

                using var registration = cancellationToken.Register(_lifetime.Cancel);
                if (_reader is null)
                {
                    _reader = await _service.OpenHistoryAsync(_repositoryPath, depth, _lifetime.Token);
                }

                // Replay at most one bounded page per ordinary tool invocation.
                _last = await _reader.ReadPageAsync(input.MaximumCommits ?? 16, 16 * 1024, _lifetime.Token);
                if (resumeRevision == _last.HistoryRevision && _last.CollectionPage < page && _last.NextCollectionPage is null)
                {
                    throw new InvalidDataException("Saved onboarding progress exceeds the pinned history.");
                }

                _lastPage = _last.CollectionPage;
                return _last;
            }
            catch
            {
                _failed = true;
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_reader is not null)
                {
                    await _reader.DisposeAsync();
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
                _lifetime.Dispose();
            }
        }
    }
}
