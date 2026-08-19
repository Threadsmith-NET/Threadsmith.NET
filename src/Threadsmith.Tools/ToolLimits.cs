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
    // ── list_files ───────────────────────────────────────────────────────

    /// <summary>Default <c>maximumEntries</c> when the model omits it. Historical default: 200.</summary>
    public int ListFilesDefaultEntries { get; init; } = 200;

    /// <summary>Upper bound for <c>maximumEntries</c>. Historical default: 2000.</summary>
    public int ListFilesMaxEntries { get; init; } = 2000;

    // ── read_file ────────────────────────────────────────────────────────

    /// <summary>Maximum byte size of a single readable file. Historical default: 1 MiB.</summary>
    public long ReadFileMaximumBytes { get; init; } = 1024 * 1024;

    /// <summary>Default <c>maximumLines</c> when the model omits it. Historical default: 200.</summary>
    public int ReadFileDefaultLines { get; init; } = 200;

    /// <summary>Upper bound for <c>maximumLines</c>. Historical default: 1000.</summary>
    public int ReadFileMaxLines { get; init; } = 1000;

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

    /// <summary>The compiled-in defaults used when no configuration is supplied.</summary>
    public static ToolLimits Default { get; } = new();
}