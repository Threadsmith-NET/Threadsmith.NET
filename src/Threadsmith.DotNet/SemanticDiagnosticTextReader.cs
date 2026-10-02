namespace Threadsmith.DotNet;

using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.Text;

/// <summary>Reconciles disk text without materializing another full string for unchanged documents.</summary>
internal static class SemanticDiagnosticTextReader
{
    private const int BufferCharacters = 4096;

    /// <summary>Reads once, retaining the existing text only after comparing every decoded character.</summary>
    internal static async Task<SourceText> ReadAsync(string path, SourceText? existing, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        SemanticLoadMetrics.DiagnosticFileReads.Add(1);
        var buffer = ArrayPool<char>.Shared.Rent(BufferCharacters * 2);
        var position = 0;
        StringBuilder? changed = null;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(0, BufferCharacters), cancellationToken)) > 0)
            {
                if (changed is null)
                {
                    var matches = existing is not null && count <= existing.Length - position;
                    if (matches)
                    {
                        existing?.CopyTo(position, buffer, BufferCharacters, count);
                        matches = buffer.AsSpan(0, count).SequenceEqual(buffer.AsSpan(BufferCharacters, count));
                    }

                    if (!matches)
                    {
                        changed = new StringBuilder();
                        if (position > 0 && existing is not null)
                        {
                            await using var writer = new StringWriter(changed, CultureInfo.InvariantCulture);
                            existing.Write(writer, new TextSpan(0, position), cancellationToken);
                        }
                    }
                }

                changed?.Append(buffer, 0, count);
                position = checked(position + count);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (changed is null && existing is not null && position == existing.Length)
            {
                return existing;
            }

            // EOF can be the first difference (truncation, including an empty file).
            var content = changed?.ToString() ?? existing?.ToString(new TextSpan(0, position)) ?? string.Empty;
            var result = SourceText.From(content, Encoding.UTF8);
            SemanticLoadMetrics.DiagnosticTextsCreated.Add(1);
            return result;
        }
        finally
        {
            SemanticLoadMetrics.DiagnosticCharactersRead.Add(position);
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }
}
