namespace Threadsmith.Workspaces;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Streams chronological history through the existing local Git query boundary.</summary>
public sealed partial class GitQueryService
{
    /// <inheritdoc />
    public Task<IGitHistoryReader> OpenHistoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        return OpenHistoryAsync(repositoryPath, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IGitHistoryReader> OpenHistoryAsync(string repositoryPath, int? depth, CancellationToken cancellationToken = default)
    {
        if (depth is { } limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        }

        var root = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if (!Path.Exists(Path.Combine(root, ".git")))
        {
            return new HistoryReader(this, null, null, false, depth, cancellationToken);
        }

        root = await ValidateRepositoryAsync(root, cancellationToken);
        var shallow = await RunAsync(root, ["rev-parse", "--is-shallow-repository"], cancellationToken);
        if (shallow.Text.Trim() != "false" && depth is null)
        {
            throw new InvalidOperationException("Complete onboarding history requires a non-shallow repository. Fetch the missing history before onboarding.");
        }

        var shallowBoundaries = new HashSet<string>(StringComparer.Ordinal);
        if (shallow.Text.Trim() != "false")
        {
            var shallowPath = await RunAsync(root, ["rev-parse", "--git-path", "shallow"], cancellationToken);
            if (shallowPath.IsTruncated)
            {
                throw new InvalidDataException("Git shallow boundary path was truncated.");
            }

            await foreach (var boundary in File.ReadLinesAsync(Path.GetFullPath(shallowPath.Text.Trim(), root), cancellationToken))
            {
                shallowBoundaries.Add(boundary);
            }
        }

        var revision = await RunAsync(root, ["rev-parse", "--revs-only", "HEAD"], cancellationToken);
        var pinned = revision.Text.Trim();
        if (pinned.Length == 0)
        {
            return new HistoryReader(this, null, null, true, depth, cancellationToken);
        }

        if (revision.IsTruncated || pinned.Length is not (40 or 64) || !pinned.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Git did not return one immutable history revision.");
        }

        var arguments = new List<string> { "log", "--reverse", "--date-order", "--root", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--no-textconv", "--diff-merges=first-parent", "--format=%x00%H%x00%P%x00%aI%x00%B%x00" };
        if (depth is { } maximum)
        {
            arguments.Add($"--max-count={maximum.ToString(CultureInfo.InvariantCulture)}");
        }

        arguments.Add(pinned);
        arguments.Add("--");
        var process = CreateProcess(root, arguments);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git did not start.");
            }

            return new HistoryReader(this, process, pinned, true, depth, cancellationToken, shallowBoundaries);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class HistoryReader : IGitHistoryReader
    {
        private readonly GitQueryService _owner;
        private readonly Process? _process;
        private readonly string? _revision;
        private readonly bool _isGitRepository;
        private readonly int? _depth;
        private readonly IReadOnlySet<string>? _shallowBoundaries;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenRegistration _termination;
        private readonly Task<BoundedText>? _error;
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _length;
        private int _page;
        private bool _started;
        private bool _ended;
        private bool _firstPath;
        private bool _continuation;
        private GitCommitSummary? _commit;
        private string _message = string.Empty;
        private int _messageOffset;
        private string? _path;

        public HistoryReader(GitQueryService owner, Process? process, string? revision, bool isGitRepository, int? depth, CancellationToken cancellationToken, IReadOnlySet<string>? shallowBoundaries = null)
        {
            _owner = owner;
            _process = process;
            _revision = revision;
            _isGitRepository = isGitRepository;
            _depth = depth;
            _shallowBoundaries = shallowBoundaries;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (process is not null)
            {
                _termination = RegisterTermination(process, _lifetime.Token);
                _error = owner.ReadBoundedAsync(process.StandardError, _lifetime.Token);
            }
        }

        public async Task<GitLogResult> ReadPageAsync(int maximumCommits, int maximumCharacters, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 4096);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCharacters, 32 * 1024);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommits);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_owner._limits.TimeoutMilliseconds);
            using var termination = _process is null ? default : RegisterTermination(_process, timeout.Token);
            try
            {
                var commits = new List<GitCommitSummary>();
                var remaining = maximumCharacters;
                while (!_ended && commits.Count < Math.Min(maximumCommits, 32) && remaining >= 2048)
                {
                    if (_commit is null && !await ReadHeaderAsync(timeout.Token))
                    {
                        break;
                    }

                    var commit = _commit ?? throw new InvalidDataException("Git history header was unavailable.");
                    var metadataSize = commit.Commit.Length + commit.Parents.Sum(parent => parent.Length) + 128;
                    if (metadataSize > remaining / 2)
                    {
                        throw new InvalidDataException("Git history parent metadata exceeds the bounded page. The prior collection was preserved.");
                    }

                    remaining -= metadataSize;
                    var count = Math.Min(_message.Length - _messageOffset, remaining / 2);
                    if (count > 0 && _messageOffset + count < _message.Length && char.IsHighSurrogate(_message[_messageOffset + count - 1]))
                    {
                        count--;
                    }

                    var message = _message.Substring(_messageOffset, count);
                    _messageOffset += count;
                    remaining -= count;
                    var messageContinues = _messageOffset < _message.Length;
                    var paths = new List<string>();
                    var continues = messageContinues;
                    if (!messageContinues)
                    {
                        while (true)
                        {
                            _path ??= await ReadTokenAsync(timeout.Token);
                            if (_path is null || _path.Length == 0)
                            {
                                _ended = _path is null;
                                _commit = null;
                                _path = null;
                                break;
                            }

                            if (_firstPath)
                            {
                                _path = _path.StartsWith('\n') ? _path[1..] : _path;
                                _firstPath = false;
                            }

                            if (_path.Length + 8 > remaining)
                            {
                                if (paths.Count == 0 && _path.Length + 8 > maximumCharacters / 2)
                                {
                                    throw new InvalidDataException("Git history path exceeds the bounded page.");
                                }

                                continues = true;
                                break;
                            }

                            paths.Add(_path);
                            remaining -= _path.Length + 8;
                            _path = null;
                        }
                    }

                    commits.Add(commit with { Message = message, MessageContinues = messageContinues, ChangedPaths = paths, PathsContinue = continues, IsContinuation = _continuation });
                    _continuation = continues;
                    if (continues)
                    {
                        break;
                    }
                }

                if (_ended && _process is not null)
                {
                    await _process.WaitForExitAsync(timeout.Token);
                    var error = _error is null ? null : await _error.WaitAsync(timeout.Token);
                    if (_process.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Git history failed with exit code {_process.ExitCode}: {error?.Text}");
                    }
                }

                return new GitLogResult(commits, false)
                {
                    CollectionPage = _page,
                    HistoryDepth = _depth,
                    HistoryRevision = _revision,
                    IsGitRepository = _isGitRepository,
                    IsCollectionPage = true,
                    NextCollectionPage = _ended ? null : ++_page,
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException("Git history page exceeded its execution timeout.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DrainBeforeTerminationAsync();
            }
            finally
            {
                try
                {
                    await _lifetime.CancelAsync();
                    if (_process is not null)
                    {
                        await _process.WaitForExitAsync(CancellationToken.None);
                        if (_error is not null)
                        {
                            try
                            {
                                await _error.WaitAsync(CancellationToken.None);
                            }
                            catch (OperationCanceledException)
                            {
                                // The owning operation cancels the stderr drain on disposal.
                            }
                        }
                    }
                }
                finally
                {
                    _termination.Dispose();
                    _process?.Dispose();
                    _lifetime.Dispose();
                }
            }
        }

        private async Task DrainBeforeTerminationAsync()
        {
            if (_process is not null && !_lifetime.IsCancellationRequested)
            {
                // Let the Git launcher reap its child on ordinary early disposal.
                // Killing the tree and waiting only for the launcher can return while
                // a descendant still holds the repository's working directory.
                using var drain = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                drain.CancelAfter(_owner._limits.TimeoutMilliseconds);
                try
                {
                    // Discard unread history with bounded buffering, not ReadToEnd.
                    await _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, drain.Token);
                    await _process.WaitForExitAsync(drain.Token);
                    if (_error is not null)
                    {
                        await _error.WaitAsync(drain.Token);
                    }
                }
                catch (OperationCanceledException) when (drain.IsCancellationRequested)
                {
                    // Cancellation or the existing query deadline retains the kill fallback.
                }
            }
        }

        private async Task<bool> ReadHeaderAsync(CancellationToken cancellationToken)
        {
            if (!_started)
            {
                _started = true;
                var marker = await ReadTokenAsync(cancellationToken);
                if (marker is null)
                {
                    _ended = true;
                    return false;
                }

                if (marker.Length != 0)
                {
                    throw new InvalidDataException("Git history framing is invalid.");
                }
            }

            var hash = await ReadTokenAsync(cancellationToken);
            if (hash is not null && _shallowBoundaries?.Contains(hash) == true)
            {
                throw new InvalidOperationException("The requested onboarding depth reaches incomplete shallow history. Fetch more history or choose a smaller depth.");
            }

            if (hash is null)
            {
                _ended = true;
                return false;
            }

            var parents = await ReadTokenAsync(cancellationToken);
            var date = await ReadTokenAsync(cancellationToken);
            _message = await ReadTokenAsync(cancellationToken) ?? throw new InvalidDataException("Missing Git commit message.");
            var separator = await ReadTokenAsync(cancellationToken);
            if (hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit) || parents is null || separator != string.Empty
                || !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var authoredAt))
            {
                throw new InvalidDataException("Git history metadata is invalid.");
            }

            _commit = new GitCommitSummary(hash, parents.Split(' ', StringSplitOptions.RemoveEmptyEntries), string.Empty, string.Empty, authoredAt, string.Empty);
            _messageOffset = 0;
            _firstPath = true;
            _continuation = false;
            return true;
        }

        private async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
        {
            var token = new StringBuilder();
            while (_process is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset == _length)
                {
                    _length = await _process.StandardOutput.ReadAsync(_buffer, cancellationToken);
                    _offset = 0;
                    if (_length == 0)
                    {
                        if (token.Length != 0)
                        {
                            throw new InvalidDataException("Git history ended inside a record.");
                        }

                        return null;
                    }
                }

                var character = _buffer[_offset++];
                if (character == '\0')
                {
                    return token.ToString();
                }

                if (token.Length >= _owner._limits.MaximumCapturedCharacters)
                {
                    throw new InvalidDataException("Git history field exceeds the bounded capture limit. The prior collection was preserved.");
                }

                token.Append(character);
            }

            return null;
        }
    }
}
