namespace Threadsmith.Core;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

/// <summary>Coordinates atomic read-modify-replace operations for repository settings files.</summary>
public static class RepositorySettingsCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(PathComparer);

    /// <summary>Gets the shared permissive JSON syntax accepted by repository configuration.</summary>
    public static JsonDocumentOptions DocumentOptions => new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Owns a complete serialized configuration update, including safe temporary creation, replacement and cleanup.</summary>
    /// <param name="configurationPath">The host-selected configuration path.</param>
    /// <param name="prepareAsync">Reads and transforms configuration while the shared gate is held; null leaves the file unchanged.</param>
    /// <param name="createPrivateFile">Optional host-owned create-new factory for files requiring private permissions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the prepared content was published.</returns>
    public static async Task<bool> UpdateAsync(
        string configurationPath,
        Func<CancellationToken, Task<RepositorySettingsUpdate?>> prepareAsync,
        Func<string, FileStream>? createPrivateFile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(prepareAsync);
        var path = Path.GetFullPath(configurationPath);
        var gate = Gates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureUnlinkedRepositorySettingsPath(path);
            var isMixedConfiguration = Path.GetFileName(path).Equals("config.json", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Equals("session.json", StringComparison.OrdinalIgnoreCase);
            var existed = File.Exists(path);
            var update = await prepareAsync(cancellationToken);
            if (update is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (isMixedConfiguration && !(update.IsHostInitialization && !existed && !update.Overwrite))
            {
                var previous = update.ExpectedContent ?? (existed ? await ReadForComparisonAsync(path, cancellationToken) : "{}"u8.ToArray());
                AuthoritySettingsChangeGuard.Validate(previous, update.Content, update.AuthorizedPolicyKeys);
            }

            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Repository configuration has no parent directory.");
            EnsureUnlinkedRepositorySettingsPath(path);
            Directory.CreateDirectory(directory);
            EnsureUnlinkedRepositorySettingsPath(path);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            var created = false;
            try
            {
                // CreateNew refuses any pre-existing entry, including a symbolic link.
                // Cleanup is permitted only after this operation creates its own file.
                await using (var stream = createPrivateFile is null
                    ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous)
                    : createPrivateFile(temporary))
                {
                    created = true;
                    EnsureUnlinkedRepositorySettingsPath(temporary);
                    await stream.WriteAsync(update.Content, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                EnsureUnlinkedRepositorySettingsPath(temporary);
                EnsureUnlinkedRepositorySettingsPath(path);
                if (update.ExpectedContent is { } expected)
                {
                    // Migration preserves destination metadata and avoids replacing a file
                    // that an external writer changed while the update was prepared.
                    var current = await ReadForComparisonAsync(path, cancellationToken);
                    if (!expected.Span.SequenceEqual(current))
                    {
                        return false;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureUnlinkedRepositorySettingsPath(path);
                    File.Replace(temporary, path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(temporary, path, update.Overwrite);
                }

                created = false;
                return true;
            }
            finally
            {
                if (created)
                {
                    EnsureUnlinkedRepositorySettingsPath(temporary);
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Rejects an existing repository root, settings directory, or settings file that is a link or reparse point.</summary>
    public static void EnsureUnlinkedRepositorySettingsPath(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        var normalizedPath = Path.GetFullPath(configurationPath);
        var settingsDirectory = Path.GetDirectoryName(normalizedPath)
            ?? throw new InvalidOperationException("Repository configuration path has no parent directory.");
        var repositoryRoot = Path.GetDirectoryName(settingsDirectory)
            ?? throw new InvalidOperationException("Repository configuration path must be under a repository directory.");

        RejectReparsePointIfPresent(repositoryRoot);
        RejectReparsePointIfPresent(settingsDirectory);
        RejectReparsePointIfPresent(normalizedPath);
    }

    private static async Task<byte[]> ReadForComparisonAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("AuthoritySettingReviewRequired: settings exceed the 16 MiB semantic-update limit; edit this file manually.");
        }

        var content = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(content, cancellationToken);
        if (stream.ReadByte() != -1)
        {
            throw new IOException("Settings changed during semantic comparison; retry the update.");
        }

        return content;
    }

    private static void RejectReparsePointIfPresent(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException(
                "Repository settings paths cannot contain symbolic links, junctions, or reparse points.");
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>Prepared configuration bytes and publication semantics; the coordinator owns all filesystem mutations.</summary>
public sealed record RepositorySettingsUpdate(ReadOnlyMemory<byte> Content)
{
    /// <summary>Exact authority keys owned by an existing specifically authorized settings operation.</summary>
    public ImmutableArray<string> AuthorizedPolicyKeys { get; init; } = [];

    /// <summary>Whether host scaffolding may create a missing configuration; never permits replacing one.</summary>
    public bool IsHostInitialization { get; init; }

    /// <summary>Whether an existing destination may be replaced; false is used for first-run scaffolding.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>When supplied, publish only over these original bytes using metadata-preserving replacement.</summary>
    public ReadOnlyMemory<byte>? ExpectedContent { get; init; }
}
