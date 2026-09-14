namespace Threadsmith.Workspaces;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Extends the existing Git object reader with bounded literal-path batches.</summary>
public sealed partial class GitQueryService
{
    private async Task<GitShowResult> ShowFilesAsync(string root, GitShowRequest request, CancellationToken cancellationToken)
    {
        if (request.Path is not null || request.Paths.Count > 64 || request.Paths.Distinct(StringComparer.Ordinal).Count() != request.Paths.Count)
        {
            throw new ArgumentException("Git show accepts one path or up to 64 distinct batch paths.");
        }

        var paths = request.Paths.Select(path => ValidatePath(root, path)
            ?? throw new ArgumentException("Git batch paths must name files.")).ToArray();
        var inventory = await RunAsync(root, ["ls-tree", "-r", "-l", "-z", request.Revision, "--", .. paths.Select(LiteralPathspec)], cancellationToken);
        if (inventory.IsTruncated)
        {
            throw new InvalidDataException("Git batch inventory exceeded its capture bound.");
        }

        var entries = new Dictionary<string, (string ObjectId, long Size)>(StringComparer.Ordinal);
        foreach (var record in inventory.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            var metadata = tab < 0 ? [] : record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 4 || metadata[1] != "blob" || metadata[0] is not ("100644" or "100755")
                || metadata[2].Length is not (40 or 64) || !metadata[2].All(Uri.IsHexDigit)
                || !long.TryParse(metadata[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException("Git batch paths must resolve to ordinary immutable files.");
            }

            entries.Add(record[(tab + 1)..], (metadata[2], size));
        }

        if (entries.Count != paths.Length || paths.Any(path => !entries.ContainsKey(path)))
        {
            throw new FileNotFoundException("A Git batch path did not resolve exactly.");
        }

        var readable = paths.Where(path => entries[path].Size <= _limits.MaximumShowCharacters).ToArray();
        var expectedBytes = readable.Sum(path => entries[path].Size + 100);
        if (expectedBytes > _limits.MaximumCapturedCharacters)
        {
            throw new InvalidDataException("Git batch exceeds its capture bound; request fewer files.");
        }

        var files = new Dictionary<string, GitShowFile>(StringComparer.Ordinal);
        if (readable.Length > 0)
        {
            var output = await RunBytesAsync(
                root,
                ["cat-file", "--batch"],
                cancellationToken,
                string.Join('\n', readable.Select(path => entries[path].ObjectId)) + "\n");
            if (output.IsTruncated)
            {
                throw new InvalidDataException("Git batch content exceeded its capture bound.");
            }

            var offset = 0;
            foreach (var path in readable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[path];
                var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{entry.ObjectId} blob {entry.Size}\n"));
                if (output.Bytes.Length - offset < header.Length + entry.Size + 1
                    || !output.Bytes.AsSpan(offset, header.Length).SequenceEqual(header))
                {
                    throw new InvalidDataException("Git batch identity or byte framing changed.");
                }

                offset += header.Length;
                var length = checked((int)entry.Size);
                string? content = null;
                try
                {
                    var decoded = StrictUtf8.GetString(output.Bytes, offset, length);
                    if (!decoded.Any(character => character == '\0' || (char.IsControl(character) && character is not ('\n' or '\r' or '\t'))))
                    {
                        content = decoded;
                    }
                }
                catch (DecoderFallbackException)
                {
                    // Byte framing preserves the next file even when this one is binary.
                }

                var digest = Convert.ToHexString(SHA256.HashData(output.Bytes.AsSpan(offset, length))).ToLowerInvariant();
                offset += length;
                if (output.Bytes[offset++] != (byte)'\n')
                {
                    throw new InvalidDataException("Git batch delimiter changed.");
                }

                files.Add(path, new GitShowFile(path, entry.ObjectId, content, digest, content is null, false));
            }

            if (offset != output.Bytes.Length)
            {
                throw new InvalidDataException("Git batch contained unexpected trailing data.");
            }
        }

        return new GitShowResult(request.Revision, GitObjectKind.Blob, string.Empty, false, false)
        {
            Files = paths.Select(path => files.TryGetValue(path, out var file)
                ? file : new GitShowFile(path, entries[path].ObjectId, null, null, false, true)).ToArray(),
        };
    }
}
