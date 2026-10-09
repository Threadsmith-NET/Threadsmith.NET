namespace Threadsmith.Workspaces;

using System.Text;

/// <summary>Adds byte-limited metadata capture to the existing Git process owner.</summary>
public sealed partial class GitQueryService
{
    private async Task<BoundedText> ReadMetadataAsync(StreamReader reader, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes < 0)
        {
            return new BoundedText(string.Empty, true, StopAfterPage: true);
        }

        // A byte ceiling also conservatively bounds decoded characters; requests cannot enlarge host capture.
        maximumBytes = Math.Min(maximumBytes, _limits.MaximumCapturedCharacters);

        // The extra byte distinguishes EOF from a limit; the request reserves it in advance.
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await reader.BaseStream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        var truncated = count > maximumBytes;
        var characters = new char[Math.Min(count, maximumBytes)];
        var decoder = StrictUtf8.GetDecoder();
        decoder.Convert(bytes.AsSpan(0, Math.Min(count, maximumBytes)), characters, !truncated, out _, out var used, out _);
        return new BoundedText(new string(characters, 0, used), truncated, StopAfterPage: truncated) { AcquiredBytes = count };
    }

    private static string CompleteChangedPaths(string text)
    {
        var offset = 0;
        var complete = 0;
        while (offset < text.Length)
        {
            var end = text.IndexOf('\0', offset);
            if (end < 0)
            {
                break;
            }

            var rename = text[offset] is 'R' or 'C';
            offset = end + 1;
            var paths = rename ? 2 : 1;
            for (var index = 0; index < paths; index++)
            {
                end = text.IndexOf('\0', offset);
                if (end < 0)
                {
                    return text[..complete];
                }

                offset = end + 1;
            }

            complete = offset;
        }

        return text[..complete];
    }
}
