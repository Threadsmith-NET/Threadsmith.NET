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
        if (request.Path is not null || request.Paths.Count > 0)
        {
            throw new ArgumentException("Inventory mode cannot be combined with file content paths.");
        }

        var revision = (await RequiredAsync(["rev-parse", "--verify", "--end-of-options", request.Revision + "^{commit}"])).Trim();
        if (revision.Length is not (40 or 64) || !revision.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Git returned an invalid immutable revision.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(request.InventoryOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.InventoryMaximumEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.InventoryMaximumEntries, 500);
        var treePage = await ReadPageAsync(["ls-tree", "-r", "-l", "-z", revision]);
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
            branch = (await RequiredAsync(["rev-parse", "--abbrev-ref", "HEAD"])).Trim();
            var refs = await RequiredAsync(["for-each-ref", "--format=%(symref)", "refs/remotes/*/HEAD"]);
            var defaults = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
            defaultBranch = defaults.Length == 1 ? defaults[0] : null;
            var status = await RunAsync(root, ["status", "--porcelain=v2", "--untracked-files=all"], cancellationToken, async (reader, token) =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[4096];
                long total = 0;
                int read;
                while ((read = await reader.BaseStream.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total <= 16 * 1024 * 1024)
                    {
                        hash.AppendData(buffer, 0, read);
                    }
                }

                return new BoundedText(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total > 16 * 1024 * 1024);
            });
            statusDigest = status.IsTruncated ? throw new InvalidDataException("Git status exceeds its metadata bound.") : status.Text;
            var pathPage = await ReadPageAsync(["ls-files", "-z", "--cached", "--others", "--exclude-standard"]);
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
        };
        var content = JsonSerializer.SerializeToElement(inventory).GetRawText();
        return new GitShowResult(revision, GitObjectKind.Tree, content, false, false) { ContentDigest = HashMetadata(content) };

        async Task<(string Text, int Count)> ReadPageAsync(IReadOnlyList<string> arguments)
        {
            var count = 0;
            var output = await RunAsync(root, arguments, cancellationToken, async (reader, token) =>
            {
                var page = new StringBuilder();
                var recordLength = 0;
                var buffer = new char[4096];
                var exceeded = false;
                int read;
                while ((read = await reader.ReadAsync(buffer, token)) > 0)
                {
                    for (var index = 0; index < read; index++)
                    {
                        var character = buffer[index];
                        recordLength++;
                        exceeded |= recordLength > 16384 || count >= 10000;
                        if (!exceeded && count >= request.InventoryOffset && count < (long)request.InventoryOffset + request.InventoryMaximumEntries)
                        {
                            if (page.Length < _limits.MaximumCapturedCharacters)
                            {
                                page.Append(character);
                            }
                            else
                            {
                                exceeded = true;
                            }
                        }

                        if (character == '\0')
                        {
                            count = Math.Min(10001, count + 1);
                            recordLength = 0;
                        }
                    }
                }

                return new BoundedText(page.ToString(), exceeded || recordLength != 0);
            });
            return output.IsTruncated
                ? throw new InvalidDataException("Git inventory exceeds its record or page bound.")
                : (output.Text, count);
        }

        async Task<string> RequiredAsync(IReadOnlyList<string> arguments)
        {
            var output = await RunAsync(root, arguments, cancellationToken);
            return output.IsTruncated ? throw new InvalidDataException("Git inventory exceeds the configured capture bound.") : output.Text;
        }
    }

    private static string HashMetadata(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
