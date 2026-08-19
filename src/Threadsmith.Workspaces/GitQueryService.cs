namespace Threadsmith.Workspaces;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Executes closed, bounded, local-only Git inspection queries.</summary>
public sealed class GitQueryService : IGitQueryService
{
    private const int MaximumCapturedCharacters = 512 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <inheritdoc />
    public async Task<string?> GetCurrentBranchAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var branchOutput = await RunAsync(
            await ValidateRepositoryAsync(repositoryPath, cancellationToken),
            ["rev-parse", "--abbrev-ref", "HEAD"],
            cancellationToken);
        if (branchOutput.IsTruncated)
        {
            throw new InvalidDataException("Git returned an overlong branch name.");
        }

        string branch = branchOutput.Text.Trim();
        return string.IsNullOrWhiteSpace(branch) || string.Equals(branch, "HEAD", StringComparison.Ordinal)
            ? null
            : branch;
    }

    /// <inheritdoc />
    public async Task<string?> GetRevisionAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var revisionOutput = await RunAsync(
            await ValidateRepositoryAsync(repositoryPath, cancellationToken),
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken);
        if (revisionOutput.IsTruncated)
        {
            throw new InvalidDataException("Git returned an overlong repository revision.");
        }

        string revision = revisionOutput.Text;
        return string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();
    }

    /// <inheritdoc />
    public async Task<GitDiffResult> DiffAsync(
        string repositoryPath,
        GitDiffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = request.Mode ?? GitComparisonMode.WorkingTree;
        int maximumEntries = request.MaximumEntries ?? 200;
        int maximumPatchCharacters = request.MaximumPatchCharacters ?? 131072;
        ValidateDiff(request, mode, maximumEntries, maximumPatchCharacters);
        string root = await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        string? path = ValidatePath(root, request.Path);
        var comparison = BuildComparison(request, mode);
        bool rootCommit = false;
        if (mode == GitComparisonMode.Commit)
        {
            var ancestry = await RunAsync(
                root,
                ["rev-list", "--parents", "--max-count=1", request.BaseRevision ?? string.Empty],
                cancellationToken);
            if (ancestry.IsTruncated)
            {
                throw new InvalidDataException("Git returned overlong commit ancestry.");
            }

            string[] revisions = ancestry.Text.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
            if (revisions.Length == 0)
            {
                throw new InvalidDataException("Git returned no commit ancestry.");
            }

            rootCommit = revisions.Length == 1;
            comparison = rootCommit ? [revisions[0]] : [revisions[1], revisions[0]];
        }

        string[] common = rootCommit
            ? ["diff-tree", "--root", "--no-commit-id", "-r", "--no-ext-diff", "--no-textconv"]
            : ["diff", "--no-ext-diff", "--no-textconv"];
        var names = await RunAsync(
            root,
            [.. common, "--name-status", "-z", "-M", .. comparison, .. Pathspec(path)],
            cancellationToken);
        var numstat = await RunAsync(
            root,
            [.. common, "--numstat", "-z", "-M", .. comparison, .. Pathspec(path)],
            cancellationToken);
        var patch = await RunAsync(
            root,
            [.. common, "--unified=3", "--binary", .. comparison, .. Pathspec(path)],
            cancellationToken);
        var binaryPaths = ParseBinaryPaths(numstat.Text);
        IReadOnlyList<GitDiffEntry> allEntries = ParseNameStatus(names.Text)
            .Select(entry => entry with { IsBinary = binaryPaths.Contains(entry.Path) })
            .ToArray();
        GitDiffEntry[] entries = [.. allEntries.Take(maximumEntries)];
        bool truncated = names.IsTruncated
            || numstat.IsTruncated
            || patch.IsTruncated
            || entries.Length < allEntries.Count
            || patch.Text.Length > maximumPatchCharacters;
        string boundedPatch = patch.Text[..Math.Min(patch.Text.Length, maximumPatchCharacters)];
        return new GitDiffResult(
            mode,
            request.BaseRevision,
            request.TargetRevision,
            entries,
            SummarizePatch(boundedPatch),
            boundedPatch,
            truncated);
    }

    /// <inheritdoc />
    public async Task<GitLogResult> LogAsync(
        string repositoryPath,
        GitLogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string revision = NormalizeRevisionOrDefault(request.Revision);
        int maximumCommits = request.MaximumCommits ?? 50;
        ValidateRevision(revision, nameof(request.Revision));
        if (maximumCommits is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCommits));
        }

        string root = await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        string? path = ValidatePath(root, request.Path);
        var output = await RunAsync(
            root,
            ["log", $"--max-count={maximumCommits + 1}", "--date=iso-strict", "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s%x1e", revision, .. Pathspec(path)],
            cancellationToken);
        string[] records = [.. output.Text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Where(record => !string.IsNullOrWhiteSpace(record))];
        GitCommitSummary[] commits = [.. records
            .Select(ParseCommit)
            .Take(maximumCommits)];
        return new GitLogResult(commits, output.IsTruncated || records.Length > commits.Length);
    }

    /// <inheritdoc />
    public async Task<GitShowResult> ShowAsync(
        string repositoryPath,
        GitShowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        int maximumCharacters = request.MaximumCharacters ?? 131072;
        ValidateRevision(request.Revision, nameof(request.Revision));
        if (maximumCharacters is < 1 or > MaximumCapturedCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCharacters));
        }

        string root = await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        string? path = ValidatePath(root, request.Path);
        string objectExpression = path is null ? request.Revision : $"{request.Revision}:{path}";
        var kindOutput = await RunAsync(root, ["cat-file", "-t", objectExpression], cancellationToken);
        if (kindOutput.IsTruncated)
        {
            throw new InvalidDataException("Git returned an overlong object kind.");
        }

        string kindText = kindOutput.Text.Trim();
        var kind = kindText switch
        {
            "commit" => GitObjectKind.Commit,
            "blob" => GitObjectKind.Blob,
            "tree" => GitObjectKind.Tree,
            "tag" => GitObjectKind.Tag,
            _ => throw new InvalidDataException("Git returned an unsupported object kind."),
        };
        if (kind == GitObjectKind.Blob)
        {
            var blob = await RunBytesAsync(root, ["cat-file", "-p", objectExpression], cancellationToken);
            bool binary = blob.IsBinary;
            string content = binary ? string.Empty : StrictUtf8.GetString(blob.Bytes);
            bool truncated = blob.IsTruncated || content.Length > maximumCharacters;
            return new GitShowResult(
                request.Revision,
                kind,
                content[..Math.Min(content.Length, maximumCharacters)],
                binary,
                truncated);
        }

        string[] objectArguments = kind == GitObjectKind.Commit
            ? ["show", "--no-ext-diff", "--no-textconv", "--format=fuller", "--binary", request.Revision, .. Pathspec(path)]
            : ["ls-tree", objectExpression];
        var objectOutput = await RunAsync(root, objectArguments, cancellationToken);
        bool objectTruncated = objectOutput.IsTruncated || objectOutput.Text.Length > maximumCharacters;
        return new GitShowResult(
            request.Revision,
            kind,
            objectOutput.Text[..Math.Min(objectOutput.Text.Length, maximumCharacters)],
            false,
            objectTruncated);
    }

    /// <inheritdoc />
    public async Task<GitBlameResult> BlameAsync(
        string repositoryPath,
        GitBlameRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string revision = NormalizeRevisionOrDefault(request.Revision);
        int maximumLines = request.MaximumLines ?? 500;
        ValidateRevision(revision, nameof(request.Revision));
        if (maximumLines is < 1 or > 2000
            || request.StartLine is < 1
            || request.EndLine is < 1
            || request.StartLine > request.EndLine)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        string root = await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        string path = ValidatePath(root, request.Path)
            ?? throw new ArgumentException("A blame path is required.", nameof(request));
        var arguments = new List<string> { "blame", "--line-porcelain" };
        if (request.StartLine is not null)
        {
            arguments.Add("-L");
            arguments.Add($"{request.StartLine},{request.EndLine ?? request.StartLine + maximumLines - 1}");
        }

        arguments.Add(revision);
        arguments.Add("--");
        arguments.Add(path);
        var output = await RunAsync(root, arguments, cancellationToken);
        var allLines = ParseBlame(output.Text);
        GitBlameRange[] lines = [.. allLines.Take(maximumLines)];
        return new GitBlameResult(path, lines, output.IsTruncated || allLines.Count > lines.Length);
    }

    /// <inheritdoc />
    public async Task<GitBranchComparisonResult> CompareBranchesAsync(
        string repositoryPath,
        GitBranchComparisonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        int maximumPaths = request.MaximumPaths ?? 500;
        ValidateRevision(request.BaseRevision, nameof(request.BaseRevision));
        ValidateRevision(request.TargetRevision, nameof(request.TargetRevision));
        if (maximumPaths is < 1 or > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumPaths));
        }

        string root = await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        var mergeBaseOutput = await RunAsync(
            root,
            ["merge-base", request.BaseRevision, request.TargetRevision],
            cancellationToken);
        var countsOutput = await RunAsync(
            root,
            ["rev-list", "--left-right", "--count", $"{request.BaseRevision}...{request.TargetRevision}"],
            cancellationToken);
        if (mergeBaseOutput.IsTruncated || countsOutput.IsTruncated)
        {
            throw new InvalidDataException("Git returned overlong branch-comparison metadata.");
        }

        string mergeBase = mergeBaseOutput.Text.Trim();
        string counts = countsOutput.Text.Trim();
        string[] countParts = counts.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (countParts.Length != 2
            || !int.TryParse(countParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int behind)
            || !int.TryParse(countParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ahead))
        {
            throw new InvalidDataException("Git returned invalid ahead/behind counts.");
        }

        var changedPathsOutput = await RunAsync(
            root,
            ["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "-M", mergeBase, request.TargetRevision],
            cancellationToken);
        var allPaths = ParseNameStatus(changedPathsOutput.Text);
        GitDiffEntry[] paths = [.. allPaths.Take(maximumPaths)];
        return new GitBranchComparisonResult(
            request.BaseRevision,
            request.TargetRevision,
            mergeBase,
            ahead,
            behind,
            paths,
            changedPathsOutput.IsTruncated || allPaths.Count > paths.Length);
    }

    private static List<string> BuildComparison(GitDiffRequest request, GitComparisonMode mode)
    {
        return mode switch
        {
            GitComparisonMode.WorkingTree => [],
            GitComparisonMode.Staged => ["--cached"],
            GitComparisonMode.Commit => [],
            GitComparisonMode.Range => [request.BaseRevision ?? string.Empty, request.TargetRevision ?? string.Empty],
            GitComparisonMode.MergeBase => [$"{request.BaseRevision}...{request.TargetRevision}"],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private static void ValidateDiff(
        GitDiffRequest request,
        GitComparisonMode mode,
        int maximumEntries,
        int maximumPatchCharacters)
    {
        if (maximumEntries is < 1 or > 2000
            || maximumPatchCharacters is < 1 or > MaximumCapturedCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (mode == GitComparisonMode.Commit)
        {
            ValidateRevision(request.BaseRevision, nameof(request.BaseRevision));
        }
        else if (mode is GitComparisonMode.Range or GitComparisonMode.MergeBase)
        {
            ValidateRevision(request.BaseRevision, nameof(request.BaseRevision));
            ValidateRevision(request.TargetRevision, nameof(request.TargetRevision));
        }
    }

    private static string NormalizeRevisionOrDefault(string? revision)
    {
        return revision is null ? "HEAD" : revision;
    }

    private static void ValidateRevision(string? revision, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new ArgumentException($"Git revision '{parameterName}' is required for this comparison.", parameterName);
        }

        if (revision.StartsWith("-", StringComparison.Ordinal)
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ArgumentException("Git revisions must be bounded non-option tokens without whitespace.", parameterName);
        }
    }

    private static async Task<string> ValidateRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository '{root}' does not exist.");
        }

        var topLevelOutput = await RunAsync(
            root,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        if (topLevelOutput.IsTruncated || string.IsNullOrWhiteSpace(topLevelOutput.Text))
        {
            throw new InvalidDataException("Git returned an invalid repository root.");
        }

        string topLevel = Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevelOutput.Text.Trim()));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!topLevel.Equals(root, comparison))
        {
            throw new UnauthorizedAccessException(
                "The opened directory must be the root of its Git worktree.");
        }

        return root;
    }

    private static string? ValidatePath(string repositoryRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(path) || path.Split(['/', '\\']).Any(segment => segment is ".." or "." or ""))
        {
            throw new ArgumentException("Git paths must be normalized repository-relative literal paths.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path, repositoryRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException("Git path escapes the repository.");
        }

        return path.Replace('\\', '/');
    }

    private static string[] Pathspec(string? path)
    {
        return path is null ? [] : ["--", LiteralPathspec(path)];
    }

    private static string LiteralPathspec(string path)
    {
        return $":(literal){path}";
    }

    private static IReadOnlyList<GitDiffEntry> ParseNameStatus(string output)
    {
        var entries = new List<GitDiffEntry>();
        string[] fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < fields.Length;)
        {
            string status = fields[index++];
            int tab = status.IndexOf('\t');
            string? inlinePath = tab >= 0 ? status[(tab + 1)..] : null;
            status = tab >= 0 ? status[..tab] : status;
            if (status.Length == 0)
            {
                continue;
            }

            string path = inlinePath ?? (index < fields.Length ? fields[index++] : string.Empty);
            if (path.Length == 0)
            {
                continue;
            }

            if ((status.StartsWith('R') || status.StartsWith('C')) && index < fields.Length)
            {
                entries.Add(new GitDiffEntry(status, fields[index++], path, false));
            }
            else
            {
                entries.Add(new GitDiffEntry(status, path, null, false));
            }
        }

        return entries;
    }

    private static IReadOnlySet<string> ParseBinaryPaths(string output)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string[] fields = output.Split('\0');
        for (int index = 0; index < fields.Length; index++)
        {
            string[] parts = fields[index].Split('\t');
            if (parts.Length < 3 || parts[0] != "-" || parts[1] != "-")
            {
                continue;
            }

            string path = parts[2];
            if (path.Length == 0 && index + 2 < fields.Length)
            {
                index++;
                path = fields[++index];
            }

            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    private static GitHunkSummary SummarizePatch(string patch)
    {
        string[] lines = patch.ReplaceLineEndings("\n").Split('\n');
        return new GitHunkSummary(
            lines.Count(line => line.StartsWith("diff --git ", StringComparison.Ordinal)),
            lines.Count(line => line.StartsWith("@@ ", StringComparison.Ordinal)),
            lines.Count(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)),
            lines.Count(line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)));
    }

    private static GitCommitSummary ParseCommit(string value)
    {
        string[] fields = value.TrimStart('\r', '\n').Split('\x1f');
        if (fields.Length < 6 || !DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var authoredAt))
        {
            throw new InvalidDataException(
                $"Git returned malformed commit metadata ({fields.Length} fields; date '{(fields.Length > 4 ? fields[4] : "missing")}').");
        }

        return new GitCommitSummary(
            fields[0],
            fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            fields[2],
            fields[3],
            authoredAt,
            string.Join(" ", fields.Skip(5)));
    }

    private static IReadOnlyList<GitBlameRange> ParseBlame(string output)
    {
        var lines = new List<GitBlameRange>();
        string commit = string.Empty;
        string author = string.Empty;
        string email = string.Empty;
        long timestamp = 0;
        int finalLine = 0;
        foreach (string line in output.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length >= 40 && line[40] == ' ')
            {
                string[] header = line.Split(' ');
                commit = header[0];
                _ = int.TryParse(header.ElementAtOrDefault(2), CultureInfo.InvariantCulture, out finalLine);
            }
            else if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = line[7..];
            }
            else if (line.StartsWith("author-mail ", StringComparison.Ordinal))
            {
                email = line[12..].Trim('<', '>');
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal))
            {
                _ = long.TryParse(line[12..], CultureInfo.InvariantCulture, out timestamp);
            }
            else if (line.StartsWith('\t'))
            {
                lines.Add(new GitBlameRange(commit, author, email, DateTimeOffset.FromUnixTimeSeconds(timestamp), finalLine, line[1..]));
            }
        }

        return lines;
    }

    private static async Task<BoundedText> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = CreateProcess(repositoryPath, arguments);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git did not start.");
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await using var registration = RegisterTermination(process, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git query failed with exit code {process.ExitCode}: {error.Text}");
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Git query exceeded its execution timeout.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Git executable was not found on PATH.", exception);
        }
    }

    private static async Task<BoundedBytes> RunBytesAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var process = CreateProcess(repositoryPath, arguments);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git did not start.");
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await using var registration = RegisterTermination(process, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git query failed with exit code {process.ExitCode}: {error.Text}");
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Git query exceeded its execution timeout.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Git executable was not found on PATH.", exception);
        }
    }

    private static Process CreateProcess(string repositoryPath, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        string[] fixedArguments =
        [
            "--no-pager",
            "-c",
            "color.ui=false",
            "-c",
            "core.pager=cat",
            "-c",
            "core.quotepath=false",
            "-c",
            "diff.external=",
            "-c",
            "diff.trustExitCode=false",
        ];
        foreach (string argument in fixedArguments.Concat(arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static CancellationTokenRegistration RegisterTermination(
        Process process,
        CancellationToken cancellationToken)
    {
        return cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        bool truncated = false;
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            int remaining = MaximumCapturedCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        return new BoundedText(builder.ToString(), truncated);
    }

    private static async Task<BoundedBytes> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(capacity: MaximumCapturedCharacters);
        var buffer = new byte[4096];
        var decoder = StrictUtf8.GetDecoder();
        bool binary = false;
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                byte value = buffer[index];
                binary |= value is 0 or < 0x08 or 0x0B or 0x0C or >= 0x0E and < 0x20;
            }

            if (!binary)
            {
                try
                {
                    _ = decoder.GetCharCount(buffer, 0, read, flush: false);
                }
                catch (DecoderFallbackException)
                {
                    binary = true;
                }
            }

            int remaining = MaximumCapturedCharacters - (int)output.Length;
            if (remaining > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, Math.Min(remaining, read)), cancellationToken);
            }

            truncated |= read > remaining;
        }

        if (!binary)
        {
            try
            {
                _ = decoder.GetCharCount([], 0, 0, flush: true);
            }
            catch (DecoderFallbackException)
            {
                binary = true;
            }
        }

        return new BoundedBytes(output.ToArray(), truncated, binary);
    }

    private sealed record BoundedText(string Text, bool IsTruncated);

    private sealed record BoundedBytes(byte[] Bytes, bool IsTruncated, bool IsBinary);
}
