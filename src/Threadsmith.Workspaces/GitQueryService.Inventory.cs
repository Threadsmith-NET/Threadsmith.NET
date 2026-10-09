namespace Threadsmith.Workspaces;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Provides normalized Git metadata through the same bounded query service as Git tools.</summary>
public sealed partial class GitQueryService
{
    private async Task<GitShowResult> ShowInventoryAsync(string root, GitShowRequest request, CancellationToken cancellationToken)
    {
        if (request.InventoryMaximumBytes is { } byteLimit && (byteLimit < 1 || byteLimit > _limits.MaximumMetadataBytes || (request.IncludeWorkingTree && request.IncludeWorkingTreeState)))
        {
            throw new ArgumentException("Byte-bounded inventory requires a positive allowance within configured limits and excludes working-tree state hashing.", nameof(request));
        }

        long acquiredBytes = 0;
        if (request.Path is not null || request.Paths.Count > 64)
        {
            throw new ArgumentException("Inventory accepts up to 64 literal paths, without Path.");
        }

        if (request.InventoryExtensions.Count > 16 || request.InventoryExtensions.Any(extension =>
            extension.Length is < 2 or > 16 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException("Inventory extensions must be bounded literal suffixes.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.InventoryMaximumScannedEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.InventoryMaximumScannedEntries, 10000);
        var scanLimitReached = false;
        var revision = await ResolveCommitAsync(root, request.Revision, cancellationToken);
        var selectedPaths = request.Paths.Select(path => ValidatePath(root, path) ?? throw new ArgumentException("Inventory paths must be literal non-empty paths.")).ToArray();

        ArgumentOutOfRangeException.ThrowIfNegative(request.InventoryOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.InventoryMaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.InventoryMaximumEntries, 500);
        var treePage = request.IncludeTrackedFiles
            ? await ReadPageAsync(["ls-tree", "-r", "-l", "-z", revision, "--", .. selectedPaths.Select(LiteralPathspec)])
            : (Text: string.Empty, Count: 0);
        var rawTree = treePage.Text;
        var files = new List<GitTreeFile>();
        foreach (var record in rawTree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            var fields = tab < 0 ? [] : record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 || fields[2].Length is not (40 or 64) || !fields[2].All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Git returned malformed tree metadata.");
            }

            var size = long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
            files.Add(new GitTreeFile(fields[0], fields[2], record[(tab + 1)..], size));
        }

        string? branch = null;
        string? defaultBranch = null;
        string? statusDigest = null;
        string[] paths = [];
        var pathCount = 0;
        if (request.IncludeWorkingTree)
        {
            if (request.IncludeWorkingTreeState)
            {
                branch = (await RequiredAsync(["rev-parse", "--abbrev-ref", "HEAD"])).Trim();
                var refs = await RequiredAsync(["for-each-ref", "--format=%(symref)", "refs/remotes/*/HEAD"]);
                var defaults = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
                defaultBranch = defaults.Length == 1 ? defaults[0] : null;
                var status = await RunAsync(root, ["status", "--porcelain=v2", "--untracked-files=all", "--", .. selectedPaths.Select(LiteralPathspec)], cancellationToken, async (reader, token) =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[4096];
                    long total = 0;
                    int read;
                    while ((read = await reader.BaseStream.ReadAsync(buffer, token)) > 0)
                    {
                        total += read;
                        if (total > 16 * 1024 * 1024)
                        {
                            return new BoundedText(string.Empty, true, StopAfterPage: true);
                        }

                        hash.AppendData(buffer, 0, read);
                    }

                    return new BoundedText(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total > 16 * 1024 * 1024);
                });
                statusDigest = status.IsTruncated ? throw new InvalidDataException("Git status exceeds its metadata bound.") : status.Text;
            }

            string[] trackedOptions = request.IncludeTrackedFiles ? ["--cached"] : [];
            var pathPage = await ReadPageAsync(["ls-files", "-z", .. trackedOptions, "--others", "--exclude-standard", "--", .. selectedPaths.Select(LiteralPathspec)]);
            paths = pathPage.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            pathCount = pathPage.Count;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(request.InventoryOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.InventoryMaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.InventoryMaximumEntries, 500);
        var next = checked(request.InventoryOffset + request.InventoryMaximumEntries);
        var inventory = new GitShowInventory(
            revision,
            branch,
            defaultBranch,
            statusDigest,
            paths,
            files)
        {
            NextOffset = next < Math.Max(pathCount, treePage.Count) ? next : null,
            ScanLimitReached = scanLimitReached,
        };
        var content = JsonSerializer.SerializeToElement(inventory).GetRawText();
        return new GitShowResult(revision, GitObjectKind.Tree, content, false, false) { ContentDigest = HashMetadata(content), AcquiredMetadataBytes = acquiredBytes };

        async Task<(string Text, int Count)> ReadPageAsync(IReadOnlyList<string> arguments)
        {
            if (request.InventoryMaximumBytes is { } totalLimit && acquiredBytes >= totalLimit)
            {
                scanLimitReached = true;
                return (string.Empty, 0);
            }

            var count = 0;
            var scanned = 0;
            var output = await RunAsync(root, arguments, cancellationToken, async (reader, token) =>
            {
                var captured = request.InventoryMaximumBytes is { } maximumBytes
                    ? await ReadMetadataAsync(reader, maximumBytes - (int)acquiredBytes - 1, token) : null;
                acquiredBytes += captured?.AcquiredBytes ?? 0;
                scanLimitReached |= captured?.IsTruncated ?? false;
                using var limited = captured is null ? null : new StringReader(captured.Text[..(captured.Text.LastIndexOf('\0') + 1)]);
                TextReader sourceReader = limited is null ? reader : limited;
                var page = new StringBuilder();
                var record = new StringBuilder();
                var buffer = new char[4096];
                int read;
                while ((read = await sourceReader.ReadAsync(buffer, token)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        var character = buffer[index];
                        if (character != '\0')
                        {
                            if (record.Length >= 16384)
                            {
                                scanLimitReached = true;
                                return new BoundedText(page.ToString(), false, StopAfterPage: true);
                            }

                            record.Append(character);
                            continue;
                        }

                        scanned++;
                        var entry = record.ToString();
                        record.Clear();
                        var matches = request.InventoryExtensions.Count == 0
                            || request.InventoryExtensions.Any(extension => entry.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
                        if (matches)
                        {
                            if (count >= request.InventoryOffset && count < (long)request.InventoryOffset + request.InventoryMaximumEntries)
                            {
                                if (page.Length + entry.Length + 1 > _limits.MaximumCapturedCharacters)
                                {
                                    scanLimitReached = true;
                                    return new BoundedText(page.ToString(), false, StopAfterPage: true);
                                }

                                page.Append(entry).Append('\0');
                            }

                            count++;
                            if (count > (long)request.InventoryOffset + request.InventoryMaximumEntries)
                            {
                                return new BoundedText(page.ToString(), false, StopAfterPage: true);
                            }
                        }

                        if (scanned >= request.InventoryMaximumScannedEntries)
                        {
                            scanLimitReached = true;
                            return new BoundedText(page.ToString(), false, StopAfterPage: true);
                        }
                    }
                }

                return new BoundedText(page.ToString(), record.Length != 0, StopAfterPage: captured?.IsTruncated ?? false);
            });
            return output.IsTruncated
                ? throw new InvalidDataException("Git inventory ended with an incomplete record.")
                : (output.Text, count);
        }

        async Task<string> RequiredAsync(IReadOnlyList<string> arguments)
        {
            var output = await RunAsync(root, arguments, cancellationToken);
            return output.IsTruncated ? throw new InvalidDataException("Git inventory exceeds the configured capture bound.") : output.Text;
        }
    }

    private async Task<string> ResolveCommitAsync(string root, string reference, CancellationToken cancellationToken)
    {
        var output = await RunAsync(root, ["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], cancellationToken);
        var revision = output.Text.Trim();
        if (output.IsTruncated || revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Git returned an invalid immutable revision.");
        }

        return revision;
    }

    private static string HashMetadata(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
