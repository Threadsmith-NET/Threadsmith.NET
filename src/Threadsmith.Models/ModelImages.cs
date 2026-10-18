namespace Threadsmith.Models;

using Threadsmith.Core;

/// <summary>Shared multimodal admission and deferred byte resolution for compiled adapters.</summary>
public static class ModelImages
{
    /// <summary>Rejects unsupported content flags at the existing registration boundary.</summary>
    public static void ValidateConfiguration(ModelProviderConfiguration provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        foreach (var model in provider.Models)
        {
            ModelContentTypesJsonConverter.Validate(model.SupportedInputContentTypes);
            ModelContentTypesJsonConverter.Validate(model.SupportedOutputContentTypes);
            if (model.SupportedOutputContentTypes != ModelContentTypes.Text)
            {
                throw new InvalidOperationException("This endpoint has no verified native image output. SupportedOutputContentTypes must be Text.");
            }
        }
    }

    /// <summary>Conservative high-detail allowance, independent of base64 transport size.</summary>
    public static int EstimateTokens(ImageArtifact image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Includes the largest documented tile schedule (4o-mini: 2833 base,
        // 5667/tile), before any provider downscaling, and patch-based models.
        var tiles = checked(2833 + (((image.Width + 511) / 512) * ((image.Height + 511) / 512) * 5667));
        var patches = checked(2048 + (((image.Width + 31) / 32) * ((image.Height + 31) / 32) * 3));
        return Math.Max(tiles, patches);
    }

    /// <summary>Checks capabilities, role, privacy and aggregate transport bounds before reading bytes.</summary>
    public static void Validate(ModelStreamRequest request, ModelProfile profile)
    {
        var parts = request.Messages.SelectMany(message => message.Content.Where(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Image)
            .Select(part => (message.Role, Part: part))).ToArray();
        if (parts.Length == 0)
        {
            return;
        }

        if ((profile.SupportedInputContentTypes & ModelContentTypes.Image) == 0)
        {
            throw new ModelProviderException($"Model '{profile.Name}' does not enable Image input. Select a verified image-capable model or remove the attachment.");
        }

        if (profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            throw new ModelProviderException("Images cannot be text-redacted. The selected model must allow sensitive content.");
        }

        long bytes = 0;
        foreach (var (role, part) in parts)
        {
            if (role is not (ModelMessageRole.User or ModelMessageRole.Tool) || part.Image is not { } image
                || image.MediaType is not ("image/png" or "image/jpeg") || image.ByteLength is <= 0 or > ImageArtifactLimits.MaximumBytes
                || image.Width is <= 0 or > ImageArtifactLimits.MaximumDimension || image.Height is <= 0 or > ImageArtifactLimits.MaximumDimension
                || (long)image.Width * image.Height > ImageArtifactLimits.MaximumPixels || part.Content.Length != 0)
            {
                throw new ModelProviderException("Image content has invalid metadata or an unsupported message role.");
            }

            bytes += image.ByteLength;
        }

        if (parts.Length > ImageArtifactLimits.MaximumCount || bytes > ImageArtifactLimits.MaximumRequestBytes)
        {
            throw new ModelProviderException("Image request exceeds 8 images or 20 MiB of encoded content. Remove attachments or start a new session.");
        }
    }

    /// <summary>Resolves only admitted references at the adapter boundary, after capacity checks.</summary>
    public static async Task<ModelStreamRequest> ResolveAsync(ModelStreamRequest request, ModelProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(request, profile);
        var images = request.Messages.SelectMany(message => message.Content)
            .Where(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Image)
            .Select(part => part.Image ?? throw new ModelProviderException("Missing image descriptor.")).ToArray();
        var data = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (!data.ContainsKey(image.Id))
            {
                var resolve = request.ResolveImageAsync ?? throw new ModelProviderException("Image artifact resolution is unavailable. Read or paste the image again.");
                data.Add(image.Id, await resolve(image, cancellationToken));
            }
        }

        return request with { ImageData = data };
    }

    /// <summary>Returns transport base64, or a digest placeholder for credential-free preparation.</summary>
    public static string GetBase64(ModelStreamRequest request, ImageArtifact image)
    {
        return request.ImageData.TryGetValue(image.Id, out var bytes)
            ? Convert.ToBase64String(bytes.Span)
            : "sha256:" + image.ContentHash;
    }

    /// <summary>Appends ordered image references without encoding them into text.</summary>
    public static IReadOnlyList<ModelContentPart> Append(IReadOnlyList<ModelContentPart> content, IReadOnlyList<ImageArtifact>? images)
        => images is null or { Count: 0 } ? content : [.. content, .. images.Select(image => new ModelContentPart { Kind = ModelContentPartKind.Image, Image = image })];
}
