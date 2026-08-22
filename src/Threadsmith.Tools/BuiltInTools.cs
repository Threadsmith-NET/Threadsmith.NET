namespace Threadsmith.Tools;

using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>Input for bounded repository file listing.</summary>
public sealed record ListFilesInput
{
    /// <summary>Repository-relative directory.</summary>
    public string Path { get; init; } = ".";

    /// <summary>
    /// Maximum entries returned. <c>0</c> means "use the host default"
    /// (see <see cref="ToolLimits.ListFilesDefaultEntries"/>).
    /// </summary>
    public int MaximumEntries { get; init; } = 0;
}

/// <summary>One repository file entry.</summary>
public sealed record RepositoryFileEntry(
    string Path,
    long Length,
    DateTimeOffset LastWriteTime);

/// <summary>Bounded repository file listing.</summary>
public sealed record ListFilesOutput(
    IReadOnlyList<RepositoryFileEntry> Files,
    bool IsTruncated);

/// <summary>Lists repository files without following reparse points.</summary>
public sealed class ListFilesTool : Tool<ListFilesInput, ListFilesOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<ListFilesInput, ListFilesOutput>(
        "list_files",
        "Fast repository file inventory under a bounded approved root. Use for directory/file discovery before reading files, and batch with other independent read-only inspections when possible.",
        ToolCategory.RepositoryInspection,
        RepositoryTrustLevel.UntrustedInspection,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(10),
        128 * 1024);

    private readonly ToolLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="ListFilesTool"/> class.</summary>
    public ListFilesTool(ToolLimits? limits = null)
    {
        _limits = limits ?? ToolLimits.Default;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override Task<ToolExecution<ListFilesOutput>> ExecuteAsync(
        ListFilesInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var root = ToolPathRules.NormalizeAndValidate(input.Path, context.Invocation);
        var maximumEntries = ResolveMaximumEntries(input);
        var files = new List<RepositoryFileEntry>();
        var truncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(context.Invocation.RepositoryPath, path)
                .Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment is ".git" or "bin" or "obj")
                || ToolPathRules.ContainsReservedWindowsDeviceName(relative))
            {
                continue;
            }

            if (ToolPathRules.IsProhibited(relative, context.Invocation.ProhibitedPaths))
            {
                continue;
            }

            if (files.Count == maximumEntries)
            {
                truncated = true;
                break;
            }

            var info = new FileInfo(path);
            files.Add(new RepositoryFileEntry(relative, info.Length, info.LastWriteTimeUtc));
        }

        var output = new ListFilesOutput(files, truncated);
        return Task.FromResult(new ToolExecution<ListFilesOutput>(
            output,
            [new ToolProvenanceSource("directory", root)],
            truncated));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(ListFilesInput input)
    {
        return input.Path;
    }

    /// <inheritdoc />
    protected override void ValidateInput(ListFilesInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
        if (input.MaximumEntries < 0 || input.MaximumEntries > _limits.ListFilesMaxEntries)
        {
            throw new ToolArgumentValidationException(
                $"maximumEntries must be between 0 and {_limits.ListFilesMaxEntries} (0 uses the host default).");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        ListFilesInput input,
        ToolInvocationContext context)
    {
        return [input.Path];
    }

    private int ResolveMaximumEntries(ListFilesInput input)
    {
        return input.MaximumEntries > 0 ? input.MaximumEntries : _limits.ListFilesDefaultEntries;
    }
}

/// <summary>Input for a bounded file-range read.</summary>
public sealed record ReadFileInput
{
    /// <summary>Repository-relative file path.</summary>
    public required string Path { get; init; }

    /// <summary>One-based first line.</summary>
    public int StartLine { get; init; } = 1;

    /// <summary>
    /// Maximum lines returned. <c>0</c> means "use the host default"
    /// (see <see cref="ToolLimits.ReadFileDefaultLines"/>).
    /// </summary>
    public int MaximumLines { get; init; } = 0;
}

/// <summary>Reason one bounded file read stopped before the end of the file.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReadFileTruncationReason>))]
public enum ReadFileTruncationReason
{
    /// <summary>The configured or requested line limit was reached.</summary>
    LineLimit,

    /// <summary>The host textual-content byte limit was reached.</summary>
    ContentByteLimit,
}

/// <summary>Bounded file content with explicit range and continuation metadata.</summary>
public sealed record ReadFileOutput(
    string Path,
    int StartLine,
    int? EndLine,
    int TotalLines,
    IReadOnlyList<string> Lines,
    bool IsTruncated,
    int? NextStartLine,
    ReadFileTruncationReason? TruncationReason);

/// <summary>Reads a bounded UTF-8 file range.</summary>
public sealed class ReadFileTool : Tool<ReadFileInput, ReadFileOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<ReadFileInput, ReadFileOutput>(
        "read_file",
        "Reads an approved repository text file. Omit startLine and maximumLines to read from the beginning up to 2,000 lines or 50 KiB, whichever comes first. Set a narrower range only when exact relevant line numbers are already known. Do not paginate with adjacent small ranges; continue only when the result reports truncation and use nextStartLine. Batch independent file reads in one response.",
        ToolCategory.FileRead,
        RepositoryTrustLevel.TrustedRead,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(10),
        384 * 1024);

    private readonly ToolLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="ReadFileTool"/> class.</summary>
    public ReadFileTool(ToolLimits? limits = null)
    {
        _limits = limits ?? ToolLimits.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.ReadFileMaximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.ReadFileDefaultLines, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.ReadFileMaxLines, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            _limits.ReadFileMaxLines,
            ToolLimits.ReadFileLineLimitCeiling);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            _limits.ReadFileDefaultLines,
            _limits.ReadFileMaxLines);
        ArgumentOutOfRangeException.ThrowIfLessThan(_limits.ReadFileMaximumContentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            _limits.ReadFileMaximumContentBytes,
            ToolLimits.ReadFileContentByteLimitCeiling);
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<ReadFileOutput>> ExecuteAsync(
        ReadFileInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var path = ToolPathRules.NormalizeAndValidate(input.Path, context.Invocation);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The requested repository file does not exist.", path);
        }

        if (info.Length > _limits.ReadFileMaximumBytes)
        {
            throw new InvalidOperationException(
                $"The requested file exceeds the {_limits.ReadFileMaximumBytes}-byte read limit.");
        }

        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var startIndex = Math.Min(input.StartLine - 1, lines.Length);
        var maximumLines = ResolveMaximumLines(input);
        var selected = new List<string>(Math.Min(maximumLines, lines.Length - startIndex));
        var selectedContentBytes = 0;
        var contentLimitReached = false;
        for (var index = startIndex; index < lines.Length && selected.Count < maximumLines; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineBytes = Encoding.UTF8.GetByteCount(lines[index]);
            var separatorBytes = selected.Count == 0 ? 0 : 1;
            if (selectedContentBytes + separatorBytes + lineBytes > _limits.ReadFileMaximumContentBytes)
            {
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Line {index + 1} exceeds the {_limits.ReadFileMaximumContentBytes}-byte read content limit.");
                }

                contentLimitReached = true;
                break;
            }

            selected.Add(lines[index]);
            selectedContentBytes += separatorBytes + lineBytes;
        }

        int? endLine = selected.Count == 0 ? null : input.StartLine + selected.Count - 1;
        var truncated = startIndex + selected.Count < lines.Length;
        ReadFileTruncationReason? truncationReason = truncated
            ? contentLimitReached
                ? ReadFileTruncationReason.ContentByteLimit
                : ReadFileTruncationReason.LineLimit
            : null;
        int? nextStartLine = truncated ? input.StartLine + selected.Count : null;
        var relative = Path.GetRelativePath(context.Invocation.RepositoryPath, path).Replace('\\', '/');
        var sourceLocation = endLine is null
            ? $"L{input.StartLine}"
            : $"L{input.StartLine}-L{endLine.Value}";
        var source = new ToolProvenanceSource("file", relative, sourceLocation);
        return new ToolExecution<ReadFileOutput>(
            new ReadFileOutput(
                relative,
                input.StartLine,
                endLine,
                lines.Length,
                selected,
                truncated,
                nextStartLine,
                truncationReason),
            [source],
            truncated);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(ReadFileInput input)
    {
        var maximumLines = ResolveMaximumLines(input);
        var endLine = (long)input.StartLine + maximumLines - 1;
        return $"lines {input.StartLine}-{endLine}, {input.Path}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(ReadFileInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
        if (input.StartLine < 1 || input.MaximumLines < 0 || input.MaximumLines > _limits.ReadFileMaxLines)
        {
            throw new ToolArgumentValidationException(
                $"startLine must be positive and maximumLines must be between 0 and {_limits.ReadFileMaxLines} (0 uses the host default).");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        ReadFileInput input,
        ToolInvocationContext context)
    {
        return [input.Path];
    }

    private int ResolveMaximumLines(ReadFileInput input)
    {
        return input.MaximumLines > 0 ? input.MaximumLines : _limits.ReadFileDefaultLines;
    }
}

/// <summary>Input for bounded repository text search.</summary>
public sealed record SearchTextInput
{
    /// <summary>Text or regex pattern.</summary>
    public required string Query { get; init; }

    /// <summary>Repository-relative file or directory to search; defaults to the repository root.</summary>
    public string? Path { get; init; }

    /// <summary>Simple repository-relative glob.</summary>
    public string Glob { get; init; } = "*";

    /// <summary>Whether query is a regular expression.</summary>
    public bool UseRegularExpression { get; init; }

    /// <summary>
    /// Maximum matching lines returned. <c>0</c> means "use the host default"
    /// (see <see cref="ToolLimits.SearchDefaultMatches"/>).
    /// </summary>
    public int MaximumMatches { get; init; } = 0;
}

/// <summary>One bounded text match.</summary>
public sealed record TextSearchMatch(string Path, int Line, int Column, string Text);

/// <summary>Bounded repository text-search result.</summary>
public sealed record SearchTextOutput(
    IReadOnlyList<TextSearchMatch> Matches,
    bool IsTruncated,
    string? Warning = null);

/// <summary>Searches bounded text without executing repository code.</summary>
public sealed class SearchTextTool : Tool<SearchTextInput, SearchTextOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<SearchTextInput, SearchTextOutput>(
        "search",
        "Search file contents for exact literals, configuration keys, routes, log messages, comments, and docs. Use optional path to scope a file or directory and glob to filter files. MUST NOT replace an advertised semantic tool for C# symbols, references, implementations, call relationships, impact, syntax shapes, or generated code. Batch independent searches with other read-only inspections.",
        ToolCategory.FileSearch,
        RepositoryTrustLevel.TrustedRead,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(30),
        256 * 1024);

    private static readonly string[] _searchExcludedDirectories =
    [
        ".codegraph",
        ".git",
        ".idea",
        ".vs",
        "artifacts",
        "bin",
        "node_modules",
        "obj",
        "TestResults",
    ];

    private readonly ToolLimits _limits;
    private readonly IProcessManager? _processManager;
    private readonly string _ripgrepExecutable;

    /// <summary>Initializes a new instance of the <see cref="SearchTextTool"/> class.</summary>
    public SearchTextTool(
        ToolLimits? limits = null,
        IProcessManager? processManager = null,
        string ripgrepExecutable = "rg")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ripgrepExecutable);
        _limits = limits ?? ToolLimits.Default;
        _processManager = processManager;
        _ripgrepExecutable = ripgrepExecutable;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<SearchTextOutput>> ExecuteAsync(
        SearchTextInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<TextSearchMatch>();
        var sources = new List<ToolProvenanceSource>();
        var truncated = false;
        var regex = input.UseRegularExpression
            ? new Regex(
                input.Query,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250))
            : null;
        var repositoryPath = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var searchPath = ToolPathRules.NormalizeAndValidate(input.Path ?? ".", context.Invocation);
        if (!File.Exists(searchPath) && !Directory.Exists(searchPath))
        {
            throw new FileNotFoundException("The search path does not exist.", searchPath);
        }

        var maximumMatches = ResolveMaximumMatches(input);
        var ripgrepAttempt = await TryExecuteRipgrepAsync(
            input,
            context,
            repositoryPath,
            searchPath,
            maximumMatches,
            cancellationToken);
        if (ripgrepAttempt.Execution is not null)
        {
            return ripgrepAttempt.Execution;
        }

        var fileSet = await GetManagedSearchFilesAsync(
            repositoryPath,
            searchPath,
            context,
            cancellationToken);
        truncated = fileSet.IsTruncated;
        foreach (var path in fileSet.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(context.Invocation.RepositoryPath, path)
                .Replace('\\', '/');
            if (relative.Split('/').Any(IsManagedSearchExcludedDirectory)
                || ToolPathRules.ContainsReservedWindowsDeviceName(relative)
                || ToolPathRules.IsProhibited(relative, context.Invocation.ProhibitedPaths)
                || IsUnsupportedSearchFile(path)
                || !MatchesSearchGlob(input.Glob, relative))
            {
                continue;
            }

            try
            {
                if (IsManagedSearchLinkOrReparsePoint(path)
                    || new FileInfo(path).Length > _limits.SearchMaximumBytes)
                {
                    continue;
                }

                var lineNumber = 0;
                await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
                {
                    lineNumber++;
                    var match = regex?.Match(line);
                    var column = regex is null
                        ? line.IndexOf(input.Query, StringComparison.OrdinalIgnoreCase)
                        : match is { Success: true }
                            ? match.Index
                            : -1;
                    if (column < 0)
                    {
                        continue;
                    }

                    if (matches.Count == maximumMatches)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(new TextSearchMatch(relative, lineNumber, column + 1, line));
                    sources.Add(new ToolProvenanceSource("file", relative, $"L{lineNumber}"));
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (truncated)
            {
                break;
            }
        }

        var warning = ripgrepAttempt.FallbackWarning;
        if (fileSet.IsTruncated)
        {
            const string inventoryWarning =
                "The ignore-aware managed search file inventory was truncated; results may be incomplete.";
            warning = string.IsNullOrEmpty(warning)
                ? inventoryWarning
                : $"{warning} {inventoryWarning}";
        }

        return new ToolExecution<SearchTextOutput>(
            new SearchTextOutput(matches, truncated, warning),
            sources,
            truncated);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(SearchTextInput input)
    {
        return $"{input.Query} in {input.Path ?? "."}";
    }

    /// <inheritdoc />
    protected override void ValidateInput(SearchTextInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Glob);
        if (input.Path is not null && string.IsNullOrWhiteSpace(input.Path))
        {
            throw new ToolArgumentValidationException("path cannot be empty when supplied.");
        }

        if (input.Query.Length > 500 || input.MaximumMatches < 0 || input.MaximumMatches > _limits.SearchMaxMatches)
        {
            throw new ToolArgumentValidationException(
                $"query is limited to 500 characters and maximumMatches to 0..{_limits.SearchMaxMatches} (0 uses the host default).");
        }

        if (input.UseRegularExpression)
        {
            try
            {
                _ = new Regex(input.Query, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                throw new ToolArgumentValidationException("query is not a valid regular expression.", exception);
            }
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        SearchTextInput input,
        ToolInvocationContext context)
    {
        return [ToolPathRules.NormalizeAndValidate(input.Path ?? ".", context)];
    }

    private int ResolveMaximumMatches(SearchTextInput input)
    {
        return input.MaximumMatches > 0 ? input.MaximumMatches : _limits.SearchDefaultMatches;
    }

    private async Task<RipgrepSearchAttempt> TryExecuteRipgrepAsync(
        SearchTextInput input,
        ToolExecutionContext context,
        string repositoryPath,
        string searchPath,
        int maximumMatches,
        CancellationToken cancellationToken)
    {
        if (_processManager is null)
        {
            return new RipgrepSearchAttempt(
                null,
                "Ripgrep process execution is unavailable; used the managed text-search fallback.");
        }

        if (input.Glob.StartsWith('!'))
        {
            throw new ToolArgumentValidationException("glob must be an inclusion pattern, not an exclusion pattern.");
        }

        var arguments = new List<string>
        {
            "--json",
            "--color=never",
            "--hidden",
            $"--max-filesize={_limits.SearchMaximumBytes}",
            $"--max-count={maximumMatches}",
            "--iglob=!**/*.db",
            "--iglob=!**/*.db-shm",
            "--iglob=!**/*.db-wal",
            "--iglob=!**/*.sqlite",
            "--iglob=!**/*.sqlite3",
        };
        foreach (var excludedDirectory in _searchExcludedDirectories)
        {
            arguments.Add($"--iglob=!**/{excludedDirectory}/**");
        }

        foreach (var prohibitedPath in context.Invocation.ProhibitedPaths)
        {
            if (TryCreateRipgrepExclusionGlob(prohibitedPath, out var exclusionGlob))
            {
                arguments.Add($"--iglob=!{exclusionGlob}");
            }
        }

        if (!input.UseRegularExpression)
        {
            arguments.Add("--fixed-strings");
            arguments.Add("--ignore-case");
        }

        if (!string.Equals(input.Glob, "*", StringComparison.Ordinal))
        {
            arguments.Add($"--iglob={input.Glob}");
        }

        arguments.Add("--regexp");
        arguments.Add(input.Query);
        arguments.Add("--");
        arguments.Add(Path.GetRelativePath(repositoryPath, searchPath));

        ProcessExecutionResult result;
        try
        {
            result = await _processManager.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = context.ToolInvocationId,
                    RunId = context.RunId,
                    FileName = _ripgrepExecutable,
                    Arguments = arguments,
                    WorkingDirectory = repositoryPath,
                    Timeout = TimeSpan.FromSeconds(25),
                    MaximumOutputCharacters = Definition.MaximumOutputBytes,
                    StandardOutputFormat = ProcessStandardOutputFormat.RipgrepJsonLines,
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return new RipgrepSearchAttempt(
                null,
                "Ripgrep was not found; used the managed text-search fallback.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return new RipgrepSearchAttempt(
                null,
                "Ripgrep was not found; used the managed text-search fallback.");
        }

        if (result.TimedOut)
        {
            throw new TimeoutException("The ripgrep search exceeded its 25-second timeout.");
        }

        if (result.ExitCode is not 0 and not 1)
        {
            throw new InvalidOperationException(
                $"Ripgrep exited with code {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");
        }

        var matches = new List<TextSearchMatch>();
        var sources = new List<ToolProvenanceSource>();
        var truncated = result.StandardOutputTruncated;
        var records = result.StandardOutput.Split('\n');
        for (var index = 0; index < records.Length; index++)
        {
            var trimmed = records[index].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonException) when (result.StandardOutputTruncated
                && index == records.Length - 1
                && !result.StandardOutput.EndsWith('\n'))
            {
                break;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type)
                    || !string.Equals(type.GetString(), "match", StringComparison.Ordinal)
                    || !root.TryGetProperty("data", out var data)
                    || !TryReadRipgrepText(data, "path", out var relative)
                    || !TryReadRipgrepSanitizedPath(data, out var projectedRelative)
                    || !TryReadRipgrepText(data, "lines", out var text)
                    || !data.TryGetProperty("line_number", out var lineNumber)
                    || !lineNumber.TryGetInt32(out var line)
                    || !TryReadRipgrepColumn(data, out var column))
                {
                    continue;
                }

                if (relative.StartsWith("./", StringComparison.Ordinal)
                    || relative.StartsWith(".\\", StringComparison.Ordinal))
                {
                    relative = relative[2..];
                }

                relative = relative.Replace('\\', '/');
                string matchedPath;
                try
                {
                    matchedPath = ToolPathRules.NormalizeAndValidate(relative, context.Invocation);
                }
                catch (Exception exception) when (
                    exception is ToolArgumentValidationException
                        or UnauthorizedAccessException
                        or IOException)
                {
                    continue;
                }

                if (!IsWithinSearchPath(matchedPath, searchPath))
                {
                    continue;
                }

                relative = Path.GetRelativePath(repositoryPath, matchedPath).Replace('\\', '/');
                projectedRelative = NormalizeProjectedRipgrepPath(projectedRelative);
                if (matches.Count == maximumMatches)
                {
                    truncated = true;
                    break;
                }

                matches.Add(new TextSearchMatch(projectedRelative, line, column, text.TrimEnd('\r', '\n')));
                sources.Add(new ToolProvenanceSource("file", projectedRelative, $"L{line}"));
            }
        }

        return new RipgrepSearchAttempt(
            new ToolExecution<SearchTextOutput>(
                new SearchTextOutput(matches, truncated),
                sources,
                truncated),
            null);
    }

    private static bool TryReadRipgrepText(JsonElement data, string propertyName, out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty(propertyName, out var container)
            || !container.TryGetProperty("text", out var text)
            || text.GetString() is not { } textValue)
        {
            return false;
        }

        value = textValue;
        return true;
    }

    private static bool TryReadRipgrepSanitizedPath(JsonElement data, out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty("path", out var container))
        {
            return false;
        }

        if (container.TryGetProperty("sanitizedText", out var sanitizedText)
            && sanitizedText.GetString() is { } sanitizedValue)
        {
            value = sanitizedValue;
            return true;
        }

        if (container.TryGetProperty("text", out var text)
            && text.GetString() is { } textValue)
        {
            value = textValue;
            return true;
        }

        return false;
    }

    private static string NormalizeProjectedRipgrepPath(string relative)
    {
        if (relative.StartsWith("./", StringComparison.Ordinal)
            || relative.StartsWith(".\\", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        return relative.Replace('\\', '/');
    }

    private static bool TryReadRipgrepColumn(JsonElement data, out int column)
    {
        column = 1;
        if (!data.TryGetProperty("submatches", out var submatches)
            || submatches.ValueKind != JsonValueKind.Array
            || submatches.GetArrayLength() == 0)
        {
            return true;
        }

        var first = submatches[0];
        if (!first.TryGetProperty("start", out var start)
            || !start.TryGetInt32(out var zeroBasedColumn))
        {
            return false;
        }

        column = zeroBasedColumn + 1;
        return true;
    }

    private async Task<SearchFileSet> GetManagedSearchFilesAsync(
        string repositoryPath,
        string searchPath,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var gitFileSet = await TryEnumerateGitSearchFilesAsync(
            repositoryPath,
            context,
            cancellationToken);
        if (gitFileSet is not null)
        {
            return new SearchFileSet(
                [.. gitFileSet.Paths.Where(path => IsWithinSearchPath(path, searchPath))],
                gitFileSet.IsTruncated);
        }

        return new SearchFileSet(
            File.Exists(searchPath) ? [searchPath] : EnumerateSearchFiles(searchPath),
            false);
    }

    private async Task<SearchFileSet?> TryEnumerateGitSearchFilesAsync(
        string repositoryPath,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (_processManager is null)
        {
            return null;
        }

        ProcessExecutionResult result;
        try
        {
            result = await _processManager.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = context.ToolInvocationId,
                    RunId = context.RunId,
                    FileName = "git",
                    Arguments =
                    [
                        "--no-pager",
                        "-c",
                        "color.ui=false",
                        "-c",
                        "core.pager=cat",
                        "-c",
                        "core.quotepath=false",
                        "-c",
                        "core.fsmonitor=false",
                        "ls-files",
                        "--cached",
                        "--others",
                        "--exclude-standard",
                        "-z",
                    ],
                    WorkingDirectory = repositoryPath,
                    Timeout = TimeSpan.FromSeconds(10),
                    MaximumOutputCharacters = Definition.MaximumOutputBytes,
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["GIT_OPTIONAL_LOCKS"] = "0",
                        ["GIT_TERMINAL_PROMPT"] = "0",
                        ["GIT_CONFIG_NOSYSTEM"] = "1",
                    },
                    Origin = ProcessRequestOrigin.Host,
                },
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        var relativePaths = result.StandardOutput.Split('\0');
        var pathCount = relativePaths.Length;
        if (result.StandardOutputTruncated && !result.StandardOutput.EndsWith('\0'))
        {
            pathCount--;
        }

        var paths = new List<string>(pathCount);
        for (var index = 0; index < pathCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = relativePaths[index];
            if (relativePath.Length == 0)
            {
                continue;
            }

            try
            {
                var normalizedPath = ToolPathRules.NormalizeAndValidate(relativePath, context.Invocation);
                if (!IsManagedSearchLinkOrReparsePoint(normalizedPath))
                {
                    paths.Add(normalizedPath);
                }
            }
            catch (Exception exception) when (
                exception is ToolArgumentValidationException
                    or UnauthorizedAccessException
                    or IOException)
            {
                continue;
            }
        }

        return new SearchFileSet(paths, result.StandardOutputTruncated);
    }

    private static bool IsWithinSearchPath(string path, string searchPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (File.Exists(searchPath))
        {
            return path.Equals(searchPath, comparison);
        }

        var root = Path.TrimEndingDirectorySeparator(searchPath);
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static bool TryCreateRipgrepExclusionGlob(
        string prohibitedPattern,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? exclusionGlob)
    {
        exclusionGlob = null;
        var pattern = prohibitedPattern.Replace('\\', '/').Trim().TrimStart('/');
        if (pattern.Length == 0 || ContainsRipgrepOnlyGlobSyntax(pattern))
        {
            return false;
        }

        if (pattern.EndsWith('/'))
        {
            pattern += "**";
        }

        exclusionGlob = pattern;
        return true;
    }

    private static bool ContainsRipgrepOnlyGlobSyntax(string pattern)
    {
        return pattern.Contains('[')
            || pattern.Contains(']')
            || pattern.Contains('{')
            || pattern.Contains('}')
            || pattern.Contains('!');
    }

    private static bool MatchesSearchGlob(string glob, string relativePath)
    {
        var ignoreCase = OperatingSystem.IsWindows();
        if (FileSystemName.MatchesSimpleExpression(glob, relativePath, ignoreCase))
        {
            return true;
        }

        if (glob.Contains('/') || glob.Contains('\\'))
        {
            return false;
        }

        var fileName = Path.GetFileName(relativePath);
        return FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase);
    }

    private static bool IsManagedSearchLinkOrReparsePoint(string path)
    {
        var file = new FileInfo(path);
        return file.LinkTarget is not null
            || (file.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsManagedSearchExcludedDirectory(string segment)
    {
        return _searchExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnsupportedSearchFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".db-shm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".db-wal", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSearchFiles(string repositoryPath)
    {
        var files = new FileSystemEnumerable<string>(
            repositoryPath,
            static (ref entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldIncludePredicate = static (ref entry) => !entry.IsDirectory,
            ShouldRecursePredicate = static (ref entry) =>
            {
                var name = entry.FileName.ToString();
                return !IsManagedSearchExcludedDirectory(name)
                    && !ToolPathRules.ContainsReservedWindowsDeviceName(name);
            },
        };
        return files;
    }

    private sealed record RipgrepSearchAttempt(
        ToolExecution<SearchTextOutput>? Execution,
        string? FallbackWarning);

    private sealed record SearchFileSet(
        IEnumerable<string> Paths,
        bool IsTruncated);
}

/// <summary>Input for read-only Git status.</summary>
public sealed record GitStatusInput;

/// <summary>Normalized Git status output.</summary>
public sealed record GitStatusOutput(
    string Branch,
    IReadOnlyList<string> Entries,
    int ExitCode,
    bool IsTruncated);

/// <summary>Gets machine-readable read-only Git status through the process manager.</summary>
public sealed class GitStatusTool : Tool<GitStatusInput, GitStatusOutput>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<GitStatusInput, GitStatusOutput>(
        "git_status",
        "Gets bounded read-only Git branch and working-tree status.",
        ToolCategory.GitInspection,
        RepositoryTrustLevel.TrustedRead,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(15),
        128 * 1024);

    private readonly IProcessManager _processManager;

    /// <summary>Initializes a new instance of the <see cref="GitStatusTool"/> class.</summary>
    public GitStatusTool(IProcessManager processManager)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        _processManager = processManager;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitStatusOutput>> ExecuteAsync(
        GitStatusInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var repositoryPath = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var result = await _processManager.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = context.ToolInvocationId,
                RunId = context.RunId,
                FileName = "git",
                Arguments = ["status", "--short", "--branch", "--untracked-files=normal"],
                WorkingDirectory = repositoryPath,
                Timeout = Definition.Timeout,
                MaximumOutputCharacters = Definition.MaximumOutputBytes / 2,
                Origin = ProcessRequestOrigin.Host,
            },
            cancellationToken);
        if (result.TimedOut)
        {
            throw new TimeoutException("Git status exceeded its execution timeout.");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git status exited with code {result.ExitCode}: {result.StandardError}");
        }

        var lines = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var branch = lines.FirstOrDefault(line => line.StartsWith("## ", StringComparison.Ordinal))
            ?? string.Empty;
        string[] entries = [.. lines.Where(line => !line.StartsWith("## ", StringComparison.Ordinal))];
        entries = [.. entries
            .Where(line => !ToolPathRules.IsProhibited(
                line.Length > 3 ? line[3..].Trim('"') : line,
                context.Invocation.ProhibitedPaths))];
        var truncated = result.StandardOutputTruncated || result.StandardErrorTruncated;
        return new ToolExecution<GitStatusOutput>(
            new GitStatusOutput(branch, entries, result.ExitCode ?? -1, truncated),
            [new ToolProvenanceSource("git", repositoryPath)],
            truncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitStatusInput input)
    {
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitStatusInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitStatusInput input)
    {
        return "git";
    }
}

/// <summary>Input for semantic symbol lookup.</summary>
public sealed record FindSymbolInput
{
    /// <summary>Declaration name.</summary>
    public required string Query { get; init; }
}

/// <summary>Input for reference lookup.</summary>
public sealed record FindReferencesInput
{
    /// <summary>Stable symbol identity returned by find_symbol.</summary>
    public required string SymbolId { get; init; }

    /// <summary>Whether explicit text fallback is allowed below partial compilation.</summary>
    public bool AllowTextFallback { get; init; }
}

/// <summary>Input for implementation lookup.</summary>
public sealed record FindImplementationsInput
{
    /// <summary>Stable symbol identity returned by find_symbol.</summary>
    public required string SymbolId { get; init; }
}

/// <summary>Compiler-aware declaration search tool.</summary>
public sealed class FindSymbolTool : Tool<FindSymbolInput, IReadOnlyList<SymbolResult>>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<FindSymbolInput, IReadOnlyList<SymbolResult>>(
        "find_symbol",
        "Primary compiler-aware tool for C# declarations. MUST use before search for a symbol; returns stable symbol identifiers for find_references, find_implementations, call_hierarchy, and symbol_impact.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedBuild,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(30),
        256 * 1024);

    private readonly ISemanticEngineResolver _semanticEngine;
    private readonly ToolLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="FindSymbolTool"/> class.</summary>
    public FindSymbolTool(ISemanticEngineResolver semanticEngine, ToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(semanticEngine);
        _semanticEngine = semanticEngine;
        _limits = limits ?? ToolLimits.Default;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<IReadOnlyList<SymbolResult>>> ExecuteAsync(
        FindSymbolInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Semantic symbol search requires an opened workspace.");
        var results = await _semanticEngine.FindSymbolsAsync(
            workspaceId,
            input.Query,
            cancellationToken);
        SymbolResult[] selected = [.. results.Take(_limits.FindSymbolMaxResults)];
        return new ToolExecution<IReadOnlyList<SymbolResult>>(
            selected,
            selected.Select(result => new ToolProvenanceSource(
                "symbol",
                result.Symbol.Id,
                $"{result.Location.FilePath}:L{result.Location.Range.StartLine}"))
                .ToArray(),
            results.Count > selected.Length);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(FindSymbolInput input)
    {
        return input.Query;
    }

    /// <inheritdoc />
    protected override void ValidateInput(FindSymbolInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        if (input.Query.Length > 256)
        {
            throw new ToolArgumentValidationException("query exceeds 256 characters.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        FindSymbolInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }
}

/// <summary>Compiler-aware reference search tool with explicit degraded fallback.</summary>
public sealed class FindReferencesTool : Tool<FindReferencesInput, IReadOnlyList<ReferenceResult>>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<FindReferencesInput, IReadOnlyList<ReferenceResult>>(
        "find_references",
        "Primary compiler-aware tool for C# symbol references. Use the symbolId returned by find_symbol; MUST use before search and fall back only if this tool fails or reports incomplete evidence.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedRead,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(30),
        256 * 1024);

    private readonly ISemanticEngineResolver _semanticEngine;
    private readonly ToolLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="FindReferencesTool"/> class.</summary>
    public FindReferencesTool(ISemanticEngineResolver semanticEngine, ToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(semanticEngine);
        _semanticEngine = semanticEngine;
        _limits = limits ?? ToolLimits.Default;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<IReadOnlyList<ReferenceResult>>> ExecuteAsync(
        FindReferencesInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Semantic reference search requires an opened workspace.");
        var results = await _semanticEngine.FindReferencesAsync(
            workspaceId,
            input.SymbolId,
            input.AllowTextFallback,
            cancellationToken);
        ReferenceResult[] selected = [.. results.Take(_limits.FindReferencesMaxResults)];
        return new ToolExecution<IReadOnlyList<ReferenceResult>>(
            selected,
            selected.Select(result => new ToolProvenanceSource(
                "reference",
                result.Symbol.Id,
                $"{result.Location.FilePath}:L{result.Location.Range.StartLine}"))
                .ToArray(),
            results.Count > selected.Length);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(FindReferencesInput input)
    {
        return input.SymbolId;
    }

    /// <inheritdoc />
    protected override void ValidateInput(FindReferencesInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        FindReferencesInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }
}

/// <summary>Compiler-aware implementation search tool.</summary>
public sealed class FindImplementationsTool : Tool<FindImplementationsInput, IReadOnlyList<ImplementationResult>>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<FindImplementationsInput, IReadOnlyList<ImplementationResult>>(
        "find_implementations",
        "Primary compiler-aware tool for interface implementations and derived or overriding symbols. Use the symbolId returned by find_symbol; MUST use before search and fall back only if this tool fails or reports incomplete evidence.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedBuild,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(30),
        256 * 1024);

    private readonly ISemanticEngineResolver _semanticEngine;
    private readonly ToolLimits _limits;

    /// <summary>Initializes a new instance of the <see cref="FindImplementationsTool"/> class.</summary>
    public FindImplementationsTool(ISemanticEngineResolver semanticEngine, ToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(semanticEngine);
        _semanticEngine = semanticEngine;
        _limits = limits ?? ToolLimits.Default;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<IReadOnlyList<ImplementationResult>>> ExecuteAsync(
        FindImplementationsInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException(
                "Semantic implementation search requires an opened workspace.");
        var results = await _semanticEngine.FindImplementationsAsync(
            workspaceId,
            input.SymbolId,
            cancellationToken);
        ImplementationResult[] selected = [.. results.Take(_limits.FindImplementationsMaxResults)];
        return new ToolExecution<IReadOnlyList<ImplementationResult>>(
            selected,
            selected.Select(result => new ToolProvenanceSource(
                "implementation",
                result.Symbol.Id,
                $"{result.Location.FilePath}:L{result.Location.Range.StartLine}"))
                .ToArray(),
            results.Count > selected.Length);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(FindImplementationsInput input)
    {
        return input.SymbolId;
    }

    /// <inheritdoc />
    protected override void ValidateInput(FindImplementationsInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SymbolId);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        FindImplementationsInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }
}

/// <summary>Input for bounded shell-command execution.</summary>
public sealed record RunProcessInput
{
    /// <summary>Shell command to execute in the repository root.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// Timeout in seconds. <c>0</c> means "use the host default"
    /// (see <see cref="ToolLimits.RunProcessDefaultTimeoutSeconds"/>).
    /// </summary>
    public int TimeoutSeconds { get; init; } = 0;
}

/// <summary>Runs one bounded command through a configured allow-listed shell.</summary>
public sealed partial class RunProcessTool : Tool<RunProcessInput, ProcessExecutionResult>
{
    private readonly ToolDefinition _definition;
    private readonly IProcessManager _processManager;
    private readonly ToolLimits _limits;
    private readonly string _shellExecutable;

    /// <summary>Initializes a new instance of the <see cref="RunProcessTool"/> class.</summary>
    public RunProcessTool(
        IProcessManager processManager,
        ToolLimits? limits = null,
        IEnumerable<string>? allowedExecutables = null,
        bool requireApproval = true,
        string? shellExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(processManager);
        _processManager = processManager;
        _limits = limits ?? ToolLimits.Default;
        _shellExecutable = string.IsNullOrWhiteSpace(shellExecutable)
            ? OperatingSystem.IsWindows() ? "powershell" : "bash"
            : shellExecutable.Trim();
        var shellFileName = Path.GetFileName(_shellExecutable);
        var shellBasename = Path.GetFileNameWithoutExtension(shellFileName);
        var shellSupported = string.Equals(
                shellFileName,
                _shellExecutable,
                StringComparison.Ordinal)
            && IsSupportedShell(shellBasename);
        var shellAllowed = shellSupported
            && (allowedExecutables?.Contains(
                shellBasename,
                StringComparer.OrdinalIgnoreCase) ?? false);
        _definition = ToolDefinitionFactory.Create<RunProcessInput, ProcessExecutionResult>(
            "run_process",
            $"Executes a {GetShellLanguage(_shellExecutable)} command in the repository root and returns bounded stdout and stderr.",
            ToolCategory.ProcessExecution,
            RepositoryTrustLevel.TrustedBuild,
            requireApproval ? ApprovalLevel.User : ApprovalLevel.None,
            ToolSideEffect.ExecutesCode,
            TimeSpan.FromSeconds(60),
            256 * 1024) with
        {
            ConversationAvailable = shellAllowed && !requireApproval,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<ProcessExecutionResult>> ExecuteAsync(
        RunProcessInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var workingDirectory = ToolPathRules.NormalizeAndValidate(
            ".",
            context.Invocation);
        var timeoutSeconds = ResolveTimeoutSeconds(input);
        var result = await _processManager.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = context.ToolInvocationId,
                RunId = context.RunId,
                FileName = _shellExecutable,
                Arguments = CreateShellArguments(_shellExecutable, input.Command),
                WorkingDirectory = workingDirectory,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                MaximumOutputCharacters = Definition.MaximumOutputBytes / 2,
                Origin = ProcessRequestOrigin.Model,
            },
            cancellationToken);
        if (result.TimedOut)
        {
            throw new TimeoutException(
                $"Shell command exceeded its {timeoutSeconds}-second timeout.");
        }

        var truncated = result.StandardOutputTruncated || result.StandardErrorTruncated;
        return new ToolExecution<ProcessExecutionResult>(
            result,
            [new ToolProvenanceSource("process", _shellExecutable)],
            truncated);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(RunProcessInput input)
    {
        return CommandCredentialPattern().Replace(input.Command, "$1[REDACTED]");
    }

    /// <inheritdoc />
    protected override void ValidateInput(RunProcessInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Command);
        if (input.Command.Length > 32 * 1024
            || input.Command.Contains('\0', StringComparison.Ordinal)
            || input.TimeoutSeconds < 0 || input.TimeoutSeconds > _limits.RunProcessMaxTimeoutSeconds)
        {
            throw new ToolArgumentValidationException(
                $"Shell command or timeout exceeds the declared bounds (command must be at most 32768 characters; timeout must be between 0 and {_limits.RunProcessMaxTimeoutSeconds}; 0 uses the host default).");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        RunProcessInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(RunProcessInput input)
    {
        return _shellExecutable;
    }

    [GeneratedRegex(
        "(?i)((?:^|\\s)(?:--?|/)(?:api[-_]?key|authorization|token|access[-_]?token|refresh[-_]?token|client[-_]?secret|password|passwd|pwd)(?:\\s+|[=:]\\s*))(?:\"[^\"]*\"|'[^']*'|[^\\s;&|]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CommandCredentialPattern();

    private static bool IsSupportedShell(string shellBasename)
    {
        return shellBasename.ToLowerInvariant() is
            "pwsh" or "powershell" or "bash" or "sh" or "zsh" or "cmd";
    }

    private static string GetShellLanguage(string shellExecutable)
    {
        var shellName = Path.GetFileNameWithoutExtension(shellExecutable);
        return shellName.ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => "PowerShell",
            "bash" => "Bash",
            "sh" => "POSIX shell",
            "zsh" => "Z shell",
            "cmd" => "Windows command-shell",
            _ => shellName,
        };
    }

    private static IReadOnlyList<string> CreateShellArguments(string shellExecutable, string command)
    {
        var shellName = Path.GetFileNameWithoutExtension(shellExecutable);
        return shellName.ToLowerInvariant() switch
        {
            "pwsh" or "powershell" => ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
            "bash" or "sh" or "zsh" => ["-c", command],
            "cmd" => ["/d", "/s", "/c", command],
            _ => throw new ToolArgumentValidationException(
                $"Configured shell '{shellExecutable}' is not supported. Use pwsh, powershell, bash, sh, zsh, or cmd."),
        };
    }

    private int ResolveTimeoutSeconds(RunProcessInput input)
    {
        return input.TimeoutSeconds > 0 ? input.TimeoutSeconds : _limits.RunProcessDefaultTimeoutSeconds;
    }
}

/// <summary>Creates versioned built-in tool definitions from host-owned input and output types.</summary>
internal static class ToolDefinitionFactory
{
    private static readonly HashSet<string> _essentialToolIds = new(
        ["list_files", "read_file", "search", "find_symbol", "run_process"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _schemaOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Creates a built-in definition with the standard execution contract.</summary>
    public static ToolDefinition Create<TInput, TOutput>(
        string id,
        string description,
        ToolCategory category,
        RepositoryTrustLevel requiredTrust,
        ApprovalLevel requiredApproval,
        ToolSideEffect sideEffect,
        TimeSpan timeout,
        int maximumOutputBytes)
    {
        var inputSchema = _schemaOptions.GetJsonSchemaAsNode(
            typeof(TInput),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var inputTypeInfo = _schemaOptions.GetTypeInfo(typeof(TInput));
        if (inputTypeInfo.Kind == JsonTypeInfoKind.Object
            && inputTypeInfo.Properties.Count == 0
            && inputSchema is JsonObject inputObject)
        {
            inputObject["properties"] = new JsonObject();
        }

        SealObjectSchemas(inputSchema);

        var outputSchema = _schemaOptions.GetJsonSchemaAsNode(
            typeof(TOutput));
        return new()
        {
            Id = id,
            DisplayName = id.Replace('_', ' '),
            Source = "Built-in",
            Essential = _essentialToolIds.Contains(id),
            Version = "1.0",
            Description = description,
            Category = category,
            InputSchema = new ToolSchema(
                    typeof(TInput).Name,
                    1,
                    inputSchema.ToJsonString()),
            OutputSchema = new ToolSchema(
                    typeof(TOutput).Name,
                    1,
                    outputSchema.ToJsonString()),
            RequiredTrust = requiredTrust,
            RequiredApproval = requiredApproval,
            SideEffect = sideEffect,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = timeout,
            MaximumOutputBytes = maximumOutputBytes,
            RequiresWorkspace = category == ToolCategory.SemanticSearch,
            Scheduling = CreateSchedulingDescriptor(category, sideEffect),
        };
    }

    private static void SealObjectSchemas(JsonNode? schema)
    {
        switch (schema)
        {
            case JsonObject schemaObject:
                if (IsObjectSchema(schemaObject))
                {
                    schemaObject["additionalProperties"] ??= false;
                }

                foreach (var property in schemaObject.ToArray())
                {
                    SealObjectSchemas(property.Value);
                }

                break;
            case JsonArray schemaArray:
                foreach (var item in schemaArray)
                {
                    SealObjectSchemas(item);
                }

                break;
        }
    }

    private static bool IsObjectSchema(JsonObject schemaObject)
    {
        if (schemaObject.ContainsKey("properties"))
        {
            return true;
        }

        return schemaObject["type"] switch
        {
            JsonValue typeValue when typeValue.TryGetValue<string>(out var type) =>
                string.Equals(type, "object", StringComparison.Ordinal),
            JsonArray typeArray => typeArray.OfType<JsonValue>()
                .Any(value => value.TryGetValue<string>(out var type)
                    && string.Equals(type, "object", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static ToolSchedulingDescriptor CreateSchedulingDescriptor(
        ToolCategory category,
        ToolSideEffect sideEffect)
    {
        var parallelSafe = sideEffect == ToolSideEffect.ReadOnly
            && category is ToolCategory.RepositoryInspection
                or ToolCategory.FileRead
                or ToolCategory.GitInspection
                or ToolCategory.SystemInformation;
        return new ToolSchedulingDescriptor
        {
            ConcurrencyMode = parallelSafe
                ? ToolConcurrencyMode.ParallelSafe
                : category == ToolCategory.SemanticSearch
                    ? ToolConcurrencyMode.SerializedPerResource
                    : ToolConcurrencyMode.SerializedPerRegistration,
            ClaimResolverId = parallelSafe
                ? "builtin-confined-resources-v1"
                : category == ToolCategory.SemanticSearch
                    ? "builtin-semantic-workspace-v1"
                    : "builtin-exclusive-registration-v1",
            MaximumSourceConcurrency = parallelSafe ? 4 : 1,
        };
    }
}
