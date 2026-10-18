namespace Threadsmith.Core;

/// <summary>Immutable metadata for an admitted raster image; an ID is a scoped reference, not a digest credential.</summary>
public sealed record ImageArtifact
{
    /// <summary>Opaque reference granted to its owning session.</summary>
    public required string Id { get; init; }

    /// <summary>SHA-256 of the exact encoded bytes.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Validated image/png or image/jpeg media type.</summary>
    public required string MediaType { get; init; }

    /// <summary>Encoded byte count.</summary>
    public required int ByteLength { get; init; }

    /// <summary>Pixel width.</summary>
    public required int Width { get; init; }

    /// <summary>Pixel height.</summary>
    public required int Height { get; init; }

    /// <summary>Bounded source label such as a repository path or clipboard.</summary>
    public required string Source { get; init; }

    /// <summary>Time this reference was admitted; used for unowned reference expiry.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Provider response identity for generated content, when applicable.</summary>
    public string? ResponseId { get; init; }
}

/// <summary>Host resource bounds checked before image decoding or transport.</summary>
public static class ImageArtifactLimits
{
    /// <summary>Maximum encoded bytes per image.</summary>
    public const int MaximumBytes = 5 * 1024 * 1024;

    /// <summary>Maximum pixels in a decoded image.</summary>
    public const int MaximumPixels = 16 * 1024 * 1024;

    /// <summary>Maximum width or height.</summary>
    public const int MaximumDimension = 8192;

    /// <summary>Maximum images in one provider request or draft.</summary>
    public const int MaximumCount = 8;

    /// <summary>Maximum encoded bytes in one provider request.</summary>
    public const int MaximumRequestBytes = 20 * 1024 * 1024;
}

/// <summary>Binary capability of the host artifact store, independent of persistence implementation.</summary>
public interface IImageArtifactStore
{
    /// <summary>Pins a live composer or run's session references against retention until disposed.</summary>
    Task<IDisposable?> PinSessionImagesAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IDisposable?>(null);

    /// <summary>Validates a bounded PNG/JPEG stream, atomically stores it and grants session ownership.</summary>
    Task<ImageArtifact> StoreImageAsync(Stream content, SessionId sessionId, string source, string? responseId = null, long maximumBytes = ImageArtifactLimits.MaximumBytes, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Image artifact storage is unavailable.");

    /// <summary>Resolves an opaque reference only within the specified session.</summary>
    Task<ImageArtifact> GetImageAsync(string artifactId, SessionId sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Image artifact storage is unavailable.");

    /// <summary>Reads bounded bytes after checking the scoped reference and content integrity.</summary>
    Task<ReadOnlyMemory<byte>> ReadImageAsync(ImageArtifact artifact, SessionId sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Image artifact storage is unavailable.");

    /// <summary>Copies only the specified authorized reference to a cloned session.</summary>
    Task GrantImageAsync(string artifactId, SessionId sourceSessionId, SessionId destinationSessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Image artifact storage is unavailable.");

    /// <summary>Releases a reference after its last draft or archive owner is removed.</summary>
    Task ReleaseImageAsync(string artifactId, SessionId sessionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Image artifact storage is unavailable.");
}
