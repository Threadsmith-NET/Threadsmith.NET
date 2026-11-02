namespace Threadsmith.Core;

/// <summary>Publishes bounded file content after revalidating its destination.</summary>
public static class AtomicFileWriter
{
    /// <summary>Writes through a sibling temporary file, preserving existing content on failure.</summary>
    public static async Task WriteAsync(string destination, ReadOnlyMemory<byte> bytes, bool overwrite, Action validate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validate);
        validate();
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("The destination needs a parent folder.");
        Directory.CreateDirectory(parent);
        validate();
        var temporary = Path.Combine(parent, $".threadsmith-write-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            validate();
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
