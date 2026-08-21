namespace Threadsmith.Models.OpenAiCodex;

using System.Text.Json;
using Threadsmith.Models;

/// <summary>Maintains the bounded user-owned snapshot of models returned after Codex authentication.</summary>
public sealed class OpenAiCodexCatalogCache
{
    private readonly string _path;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCodexCatalogCache"/> class.</summary>
    public OpenAiCodexCatalogCache(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".threadsmith",
            "openai-codex-models.json");
    }

    /// <summary>Loads the last authenticated catalog snapshot, or <see langword="null"/> when absent or malformed.</summary>
    public async Task<OpenAiCodexProviderConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var payload = await JsonSerializer.DeserializeAsync<CachePayload>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (payload is not { SchemaVersion: 1, Models: { Count: > 0 and <= 256 } models })
            {
                return null;
            }

            var configuration = new OpenAiCodexProviderConfiguration
            {
                Id = "openai-codex",
                Name = "OpenAI Codex",
                Enabled = true,
                SecretKeyReference = OpenAiCodexProviderRegistration.OAuthSecretReference,
                Models = models,
            };
            var registration = new OpenAiCodexProviderRegistration();
            _ = new ConfiguredModelCatalog(registration.CreateProfiles(configuration));
            return configuration;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Atomically stores a sanitized model-metadata snapshot without credentials.</summary>
    public async Task SaveAsync(
        OpenAiCodexProviderConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = Path.GetDirectoryName(_path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        OpenAiCodexModelConfiguration[] models =
        [
            .. configuration.Models.Cast<OpenAiCodexModelConfiguration>(),
        ];
        await File.WriteAllBytesAsync(
            temporary,
            JsonSerializer.SerializeToUtf8Bytes(new CachePayload(1, models)),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record CachePayload(int SchemaVersion, IReadOnlyList<OpenAiCodexModelConfiguration>? Models);

    /// <summary>Removes the cached model metadata.</summary>
    public Task ClearAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }
}
