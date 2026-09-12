namespace Threadsmith.Tools;

/// <summary>
/// Host-owned operational limits for the built-in tool suite, sourced from layered
/// configuration (strategy §21.1). Bound once at the composition root and injected into
/// each tool. Defaults preserve the historical compiled-in values.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is used when no configuration is supplied, so tools constructed
/// without explicit limits retain their original behavior. Tool-input records use
/// <c>0</c> as a "use host default" sentinel for bounded fields; tools resolve the sentinel
/// against these limits before execution and validation.
/// </remarks>
public sealed record ToolLimits
{
    /// <summary>Maximum characters in a file-search query.</summary>
    public int SearchMaximumQueryCharacters { get; init; } = 500;

    /// <summary>Default size for one <c>read_file</c> line window.</summary>
    public const int DefaultReadFileLineLimit = 2000;

    /// <summary>Default bound for textual content returned by one <c>read_file</c> invocation.</summary>
    public const int DefaultReadFileContentByteLimit = 50 * 1024;

    // ── list_files ───────────────────────────────────────────────────────

    /// <summary>Default <c>maximumEntries</c> when the model omits it. Historical default: 200.</summary>
    public int ListFilesDefaultEntries { get; init; } = 200;

    /// <summary>Upper bound for <c>maximumEntries</c>. Historical default: 2000.</summary>
    public int ListFilesMaxEntries { get; init; } = 2000;

    // ── read_file ────────────────────────────────────────────────────────

    /// <summary>Maximum byte size of a single readable file. Historical default: 1 MiB.</summary>
    public long ReadFileMaximumBytes { get; init; } = 1024 * 1024;

    /// <summary>Default <c>maximumLines</c> when the model omits it.</summary>
    public int ReadFileDefaultLines { get; init; } = DefaultReadFileLineLimit;

    /// <summary>Configured upper bound for <c>maximumLines</c>.</summary>
    public int ReadFileMaxLines { get; init; } = DefaultReadFileLineLimit;

    /// <summary>
    /// Configured textual-content bound for one result.
    /// </summary>
    public int ReadFileMaximumContentBytes { get; init; } = DefaultReadFileContentByteLimit;

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>Maximum byte size of a single searchable file. Historical default: 1 MiB.</summary>
    public long SearchMaximumBytes { get; init; } = 1024 * 1024;

    /// <summary>Default <c>maximumMatches</c> when the model omits it. Historical default: 100.</summary>
    public int SearchDefaultMatches { get; init; } = 100;

    /// <summary>Upper bound for <c>maximumMatches</c>. Historical default: 500.</summary>
    public int SearchMaxMatches { get; init; } = 500;

    // ── find_symbol / find_references / find_implementations ─────────────

    /// <summary>Maximum symbol results returned by <c>find_symbol</c>. Historical default: 1000.</summary>
    public int FindSymbolMaxResults { get; init; } = 1000;

    /// <summary>Maximum reference results returned by <c>find_references</c>. Historical default: 1000.</summary>
    public int FindReferencesMaxResults { get; init; } = 1000;

    /// <summary>Maximum implementation results returned by <c>find_implementations</c>. Historical default: 1000.</summary>
    public int FindImplementationsMaxResults { get; init; } = 1000;

    // ── run_process ──────────────────────────────────────────────────────

    /// <summary>Default <c>timeoutSeconds</c> when the model omits it. Historical default: 30.</summary>
    public int RunProcessDefaultTimeoutSeconds { get; init; } = 30;

    /// <summary>Upper bound for <c>timeoutSeconds</c>. Historical default: 60.</summary>
    public int RunProcessMaxTimeoutSeconds { get; init; } = 60;

    /// <summary>Maximum UTF-8 content bytes accepted by write_file.</summary>
    public int WriteFileMaximumContentBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum destination path characters accepted by write_file.</summary>
    public int WriteFileMaximumPathCharacters { get; init; } = 4096;

    /// <summary>Maximum legacy semantic results projected into model context.</summary>
    public int SemanticMaximumModelResults { get; init; } = 100;

    /// <summary>Timeout in milliseconds for each search regex match.</summary>
    public int SearchRegexTimeoutMilliseconds { get; init; } = 250;

    /// <summary>Timeout in milliseconds for the ripgrep search process.</summary>
    public int SearchProcessTimeoutMilliseconds { get; init; } = 25_000;

    /// <summary>Rejects nonpositive limits and defaults larger than their request ceilings.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchMaximumQueryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ListFilesDefaultEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ListFilesMaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadFileMaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadFileDefaultLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadFileMaxLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadFileMaximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchMaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchDefaultMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchMaxMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FindSymbolMaxResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FindReferencesMaxResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FindImplementationsMaxResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RunProcessDefaultTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RunProcessMaxTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WriteFileMaximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WriteFileMaximumPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SemanticMaximumModelResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchRegexTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchProcessTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ListFilesDefaultEntries, ListFilesMaxEntries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReadFileDefaultLines, ReadFileMaxLines);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SearchDefaultMatches, SearchMaxMatches);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RunProcessDefaultTimeoutSeconds, RunProcessMaxTimeoutSeconds);
    }

    /// <summary>The compiled-in defaults used when no configuration is supplied.</summary>
    public static ToolLimits Default { get; } = new();
}
