namespace Threadsmith.Telemetry;

using System.IO.Compression;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Configuration for diagnostic bundle generation (strategy §23.4, §19.6).</summary>
public sealed record DiagnosticBundleOptions
{
    /// <summary>Whether diagnostic bundle generation is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The directory bundles are written to. Relative paths resolve against the repository root.</summary>
    public string Directory { get; init; } = ".threadsmith/diagnostics";

    /// <summary>Whether to include recent logs in the bundle.</summary>
    public bool IncludeLogs { get; init; } = true;

    /// <summary>Whether to include recent domain events in the bundle.</summary>
    public bool IncludeEvents { get; init; } = true;

    /// <summary>Whether to include session artifacts in the bundle.</summary>
    public bool IncludeArtifacts { get; init; } = true;

    /// <summary>Whether to include sanitized configuration in the bundle.</summary>
    public bool IncludeConfiguration { get; init; } = true;

    /// <summary>Whether to include version information in the bundle.</summary>
    public bool IncludeVersionInfo { get; init; } = true;

    /// <summary>Maximum uncompressed bundle size in bytes before generation is refused.</summary>
    public long MaximumBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>Number of recent events to include per session.</summary>
    public int RecentEventsPerSession { get; init; } = 1000;
}

/// <summary>One entry collected into a diagnostic bundle (strategy §23.4).</summary>
public sealed record DiagnosticBundleEntry
{
    /// <summary>The entry kind (e.g. <c>logs</c>, <c>events</c>, <c>config</c>, <c>version</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The relative path inside the bundle archive.</summary>
    public required string RelativePath { get; init; }

    /// <summary>The sanitized text content, when stored inline.</summary>
    public string? Content { get; init; }
}

/// <summary>Outcome of generating a diagnostic bundle.</summary>
public sealed record DiagnosticBundleResult
{
    /// <summary>The absolute path of the generated bundle archive.</summary>
    public required string BundlePath { get; init; }

    /// <summary>The uncompressed byte length of the bundle.</summary>
    public required long UncompressedBytes { get; init; }

    /// <summary>The entries collected into the bundle.</summary>
    public required IReadOnlyList<DiagnosticBundleEntry> Entries { get; init; }

    /// <summary>The number of redactions applied during generation.</summary>
    public required int RedactionCount { get; init; }
}

/// <summary>Generates a secret-free diagnostic bundle for support (strategy §23.4, M8 exit criterion).</summary>
/// <remarks>
/// Every text entry passes through the host <c>SecretOutputSanitizer</c> before it is written so the
/// bundle excludes secrets by construction. A canary-secret test exercises the redaction path so the
/// "diagnostic bundles exclude secrets" exit criterion is objectively gated.
/// </remarks>
public sealed class DiagnosticBundleGenerator
{
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticBundleGenerator"/> class.</summary>
    public DiagnosticBundleGenerator(IOutputSanitizer sanitizer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Generates a diagnostic bundle from the supplied inputs.</summary>
    /// <param name="options">The bundle options.</param>
    /// <param name="inputs">The bundle inputs (logs, events, config, version).</param>
    /// <param name="cancellationToken">A token that cancels generation.</param>
    /// <returns>The generation result.</returns>
    public async Task<DiagnosticBundleResult> GenerateAsync(
        DiagnosticBundleOptions options,
        DiagnosticBundleInputs inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Diagnostic bundle generation is disabled by configuration.");
        }

        var directory = options.Directory;
        Directory.CreateDirectory(directory);
        var now = _timeProvider.GetUtcNow();
        var stamp = now.ToString("yyyyMMddHHmmss");
        var bundlePath = Path.Combine(directory, $"threadsmith-diagnostics-{stamp}.zip");
        var entries = new List<DiagnosticBundleEntry>();
        var redactions = 0;
        long totalBytes = 0;
        using (var archiveStream = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
        {
            void AddText(string relativePath, string kind, string content)
            {
                var sanitized = _sanitizer.Sanitize(content);
                redactions += CountRedactions(sanitized);
                var entry = archive.CreateEntry(relativePath);
#pragma warning disable VSTHRD103 // ZipArchiveEntry offers no async Open; the underlying stream is already opened asynchronously above.
                using var entryStream = entry.Open();
#pragma warning restore VSTHRD103
                var bytes = System.Text.Encoding.UTF8.GetBytes(sanitized);
                totalBytes += bytes.Length;
                entryStream.Write(bytes, 0, bytes.Length);
                entries.Add(new DiagnosticBundleEntry
                {
                    Kind = kind,
                    RelativePath = relativePath,
                    Content = sanitized,
                });
            }

            if (options.IncludeVersionInfo)
            {
                var versionInfo = new
                {
                    product = "Threadsmith.NET",
                    version = inputs.HostVersion,
                    generatedAt = now.ToString("O"),
                };
                AddText("version.json", "version", JsonSerializer.Serialize(versionInfo));
            }

            if (options.IncludeConfiguration)
            {
                AddText("config.json", "config", inputs.SanitizedConfigurationJson ?? "{}");
            }

            if (options.IncludeLogs && inputs.LogText is { Length: > 0 } logs)
            {
                AddText("logs.txt", "logs", logs);
            }

            if (options.IncludeEvents && inputs.EventJson is { Length: > 0 } events)
            {
                AddText("events.jsonl", "events", events);
            }

            if (options.IncludeArtifacts && inputs.Artifacts is { Count: > 0 } artifacts)
            {
                foreach (var artifact in artifacts)
                {
                    AddText($"artifacts/{artifact.Key}", "artifact", artifact.Value);
                }
            }
        }

        if (totalBytes > options.MaximumBytes)
        {
            // Refuse to ship an oversized bundle; delete the partial archive.
            File.Delete(bundlePath);
            throw new InvalidOperationException(
                $"Diagnostic bundle exceeded the {options.MaximumBytes}-byte limit ({totalBytes} bytes).");
        }

        return new DiagnosticBundleResult
        {
            BundlePath = bundlePath,
            UncompressedBytes = totalBytes,
            Entries = entries,
            RedactionCount = redactions,
        };
    }

    /// <summary>Verifies that a generated bundle contains no occurrence of the supplied canary secret (M8 exit gate).</summary>
    /// <param name="bundlePath">The bundle archive path.</param>
    /// <param name="canarySecret">The canary secret that must not appear anywhere in the bundle.</param>
    /// <param name="cancellationToken">A token that cancels the scan.</param>
    /// <returns><see langword="true"/> when the canary is absent; <see langword="false"/> when it leaked.</returns>
    public static async Task<bool> BundleExcludesCanaryAsync(
        string bundlePath,
        string canarySecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canarySecret);
        using var archiveStream = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
#pragma warning disable VSTHRD103 // ZipArchiveEntry offers no async Open; reading uses ReadToEndAsync below.
            using var entryStream = entry.Open();
#pragma warning restore VSTHRD103
            using var reader = new StreamReader(entryStream);
            var content = await reader.ReadToEndAsync(cancellationToken);
            if (content.Contains(canarySecret, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountRedactions(string sanitized)
    {
        // The sanitizer replaces matched secrets with [REDACTED]; count the markers as a proxy.
        return CountOccurrences(sanitized, "[REDACTED]");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}

/// <summary>Inputs collected into a diagnostic bundle (strategy §23.4).</summary>
public sealed record DiagnosticBundleInputs
{
    /// <summary>The host product version string.</summary>
    public required string HostVersion { get; init; }

    /// <summary>Sanitized configuration JSON (secrets already removed by the caller).</summary>
    public string? SanitizedConfigurationJson { get; init; }

    /// <summary>Recent log text, when available.</summary>
    public string? LogText { get; init; }

    /// <summary>Recent domain events as JSON Lines, when available.</summary>
    public string? EventJson { get; init; }

    /// <summary>Artifact entries keyed by relative path.</summary>
    public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new Dictionary<string, string>();
}