namespace Threadsmith.Workspaces;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using LibGit2Sharp;
using Threadsmith.Core;

/// <summary>Validates appended standard reflog creation records through the existing Git object library.</summary>
public sealed class RepositoryCommitObserver : IRepositoryCommitObserver
{
    private readonly IRepositoryFileObserver _files;

    /// <summary>Initializes a new instance of the <see cref="RepositoryCommitObserver"/> class.</summary>
    public RepositoryCommitObserver(IRepositoryFileObserver files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RepositoryCommitCreation> ObserveAsync(
        string repositoryPath, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var discovered = Repository.Discover(repositoryPath);
        if (discovered is null)
        {
            yield break;
        }

        using var repository = new Repository(discovered);
        var gitDirectory = Path.TrimEndingDirectorySeparator(repository.Info.Path);
        var commonDirectory = gitDirectory;
        var commonFile = Path.Combine(gitDirectory, "commondir");
        if (File.Exists(commonFile))
        {
            if (new FileInfo(commonFile).Length > 4096)
            {
                throw new InvalidDataException("The Git common-directory metadata is invalid.");
            }

            commonDirectory = Path.GetFullPath((await File.ReadAllTextAsync(commonFile, cancellationToken)).Trim(), gitDirectory);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = new[] { Path.Combine(gitDirectory, "logs"), Path.Combine(commonDirectory, "logs", "refs") }.Distinct(comparer).ToArray();
        var observations = new Dictionary<string, IRepositoryDirectoryObservation>(comparer);
        var positions = new Dictionary<string, long>(comparer);
        var tails = new Dictionary<string, byte[]>(comparer);
        var relocated = new List<(long Offset, byte[] Tail)>();
        var queue = Channel.CreateBounded<RepositoryFileChange>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var lost = 0;
        void Enqueue(RepositoryFileChange change)
        {
            if (change.Kind == SemanticFileChangeKind.Uncertain || !queue.Writer.TryWrite(change))
            {
                Interlocked.Exchange(ref lost, 1);
                queue.Writer.TryWrite(new(gitDirectory, SemanticFileChangeKind.Uncertain));
            }
        }

        void Cover(string directory, bool baseline)
        {
            if (!Directory.Exists(directory) || observations.ContainsKey(directory))
            {
                return;
            }

            try
            {
                observations.Add(directory, _files.Subscribe(directory, Enqueue, () => Enqueue(new(directory, SemanticFileChangeKind.Uncertain))));
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (IsTemporaryLog(file))
                    {
                        continue;
                    }

                    if (baseline)
                    {
                        var length = GetLogLength(file);
                        positions.TryAdd(file, length);
                        tails[file] = ReadCheckpointTail(file, length);
                    }
                    else
                    {
                        queue.Writer.TryWrite(new(file, SemanticFileChangeKind.Changed));
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    Cover(child, baseline);
                }
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                ReleaseSubtree(directory);
            }
            catch (ArgumentException) when (!Directory.Exists(directory))
            {
                ReleaseSubtree(directory);
            }
        }

        void ReleaseSubtree(string path)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var directory in observations.Keys.Where(directory => comparer.Equals(directory, path) || directory.StartsWith(path + Path.DirectorySeparatorChar, comparison)).ToArray())
            {
                observations.Remove(directory, out var observation);
                observation?.Dispose();
            }

            foreach (var file in positions.Keys.Where(file => comparer.Equals(file, path) || file.StartsWith(path + Path.DirectorySeparatorChar, comparison)).ToArray())
            {
                if (positions.Remove(file, out var offset) && tails.Remove(file, out var tail) && tail.Length > 0)
                {
                    relocated.Add((offset, tail));
                    if (relocated.Count > 1024)
                    {
                        throw new IOException("Git reflog relocation exceeded observation capacity; manual refresh is required.");
                    }
                }
            }
        }

        try
        {
            // Parent interests also detect first commits/new reflog directories in an unborn repository.
            foreach (var parent in new[] { gitDirectory, commonDirectory, Path.Combine(commonDirectory, "logs") }.Distinct(comparer))
            {
                if (Directory.Exists(parent) && !observations.ContainsKey(parent))
                {
                    observations.Add(parent, _files.Subscribe(parent, Enqueue, () => Enqueue(new(parent, SemanticFileChangeKind.Uncertain))));
                }
            }

            foreach (var root in roots)
            {
                Cover(root, true);
            }

            // HEAD may share its directory with the parent interest above.
            var headLog = Path.Combine(gitDirectory, "logs", "HEAD");
            if (File.Exists(headLog))
            {
                var length = GetLogLength(headLog);
                positions.TryAdd(headLog, length);
                tails[headLog] = ReadCheckpointTail(headLog, length);
            }

            while (await queue.Reader.WaitToReadAsync(cancellationToken))
            {
                if (Interlocked.Exchange(ref lost, 0) != 0)
                {
                    throw new IOException("Git metadata observation lost events; automatic refresh stopped. Manual refresh remains available.");
                }

                if (!queue.Reader.TryRead(out var change))
                {
                    continue;
                }

                var path = change.Path;
                if (change.Kind == SemanticFileChangeKind.Deleted)
                {
                    ReleaseSubtree(path);
                }

                if (change.PreviousPath is { } previous)
                {
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    var moved = positions.Where(pair => comparer.Equals(pair.Key, previous)
                        || pair.Key.StartsWith(previous + Path.DirectorySeparatorChar, comparison)).ToArray();
                    ReleaseSubtree(previous);
                    foreach (var entry in moved)
                    {
                        positions[path + entry.Key[previous.Length..]] = entry.Value;
                    }

                    if (moved.Length == 0 && File.Exists(path))
                    {
                        positions[path] = GetLogLength(path);
                    }
                }

                foreach (var root in roots)
                {
                    Cover(root, false);
                }

                if (Directory.Exists(path))
                {
                    if (roots.Any(root => comparer.Equals(root, path) || path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    {
                        Cover(path, false);
                    }

                    continue;
                }

                if (IsTemporaryLog(path) || !File.Exists(path) || !roots.Any(root => path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    continue;
                }

                await using var stream = TryOpenLog(path);
                if (stream is null)
                {
                    continue;
                }

                var offset = positions.GetValueOrDefault(path);
                if (!positions.ContainsKey(path))
                {
                    // Nonrecursive watchers can report cross-directory renames as delete/create.
                    // Resume matching relocated bytes instead of replaying their historical commits.
                    var candidates = relocated.Concat(positions.Where(pair => tails.ContainsKey(pair.Key))
                        .Select(pair => (Offset: pair.Value, Tail: tails[pair.Key])));
                    foreach (var candidate in candidates)
                    {
                        if (candidate.Offset <= stream.Length && candidate.Tail.Length > 0
                            && ReadCheckpointTail(stream, candidate.Offset).AsSpan().SequenceEqual(candidate.Tail))
                        {
                            offset = candidate.Offset;
                            relocated.Remove(candidate);
                            break;
                        }
                    }
                }

                if (stream.Length < offset)
                {
                    // Expiration/rewriting is maintenance, not evidence of new commit creation.
                    positions[path] = stream.Length;
                    tails[path] = ReadCheckpointTail(stream, stream.Length);
                    continue;
                }

                positions[path] = offset;
                stream.Position = offset;
                var buffer = new byte[4096];
                var line = new List<byte>();
                var oversized = false;
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        offset++;
                        if (buffer[index] != (byte)'\n')
                        {
                            if (line.Count < 16 * 1024)
                            {
                                line.Add(buffer[index]);
                            }
                            else
                            {
                                oversized = true;
                            }

                            continue;
                        }

                        positions[path] = offset;
                        var record = oversized ? null : ValidateCreation(repository, Encoding.UTF8.GetString([.. line]));
                        line.Clear();
                        oversized = false;
                        if (record is not null)
                        {
                            yield return record;
                        }
                    }
                }

                if (positions.TryGetValue(path, out var checkpoint))
                {
                    tails[path] = ReadCheckpointTail(stream, checkpoint);
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            foreach (var observation in observations.Values)
            {
                observation.Dispose();
            }
        }
    }

    private static bool IsTemporaryLog(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.') || name.EndsWith(".lock", StringComparison.Ordinal);
    }

    private static FileStream? TryOpenLog(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Normal Git rename/delete churn is covered by subsequent metadata notifications.
            return null;
        }
    }

    private static long GetLogLength(string path)
    {
        using var stream = TryOpenLog(path);
        return stream?.Length ?? 0;
    }

    private static byte[] ReadCheckpointTail(string path, long offset)
    {
        using var stream = TryOpenLog(path);
        return stream is null ? [] : ReadCheckpointTail(stream, offset);
    }

    private static byte[] ReadCheckpointTail(FileStream stream, long offset)
    {
        if (stream.Length < offset)
        {
            return [];
        }

        var bytes = new byte[(int)Math.Min(offset, 4096)];
        stream.Position = offset - bytes.Length;
        try
        {
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (EndOfStreamException)
        {
            // Reflog expiration can truncate a concurrently open file.
            return [];
        }
    }

    private static RepositoryCommitCreation? ValidateCreation(Repository repository, string line)
    {
        var tab = line.IndexOf('\t');
        if (tab < 0)
        {
            return null;
        }

        var message = line[(tab + 1)..];
        var amend = message.StartsWith("commit (amend):", StringComparison.Ordinal);
        var initial = message.StartsWith("commit (initial):", StringComparison.Ordinal);
        if (!amend && !initial && !message.StartsWith("commit:", StringComparison.Ordinal)
            && !message.StartsWith("cherry-pick:", StringComparison.Ordinal)
            && !message.StartsWith("revert:", StringComparison.Ordinal)
            && !(message.StartsWith("merge ", StringComparison.Ordinal) && message.Contains("Merge made by", StringComparison.Ordinal)))
        {
            return null;
        }

        var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (metadata.Length < 5 || metadata[0] == metadata[1]
            || !long.TryParse(metadata[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
        {
            return null;
        }

        var commit = repository.Lookup<Commit>(metadata[1]);
        if (commit is null || commit.Committer.When.ToUnixTimeSeconds() != timestamp)
        {
            return null;
        }

        var parents = commit.Parents.Select(parent => parent.Sha).ToArray();
        var valid = initial ? metadata[0].All(character => character == '0') && parents.Length == 0
            : amend ? repository.Lookup<Commit>(metadata[0]) is { } previous && parents.SequenceEqual(previous.Parents.Select(parent => parent.Sha))
            : parents.FirstOrDefault() == metadata[0];
        return valid ? new RepositoryCommitCreation(commit.Sha) : null;
    }
}
