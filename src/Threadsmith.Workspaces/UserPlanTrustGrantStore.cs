namespace Threadsmith.Workspaces;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Reads and writes user-owned exact repository trust grants for plan approval policy.</summary>
internal interface IUserPlanTrustGrantStore
{
    /// <summary>Determines whether the exact repository identity is trusted by user-owned state.</summary>
    /// <param name="repositoryIdentity">Exact repository identity.</param>
    /// <returns><see langword="true" /> when the identity has a user-owned grant.</returns>
    bool IsGranted(string repositoryIdentity);

    /// <summary>Adds a user-owned trust grant for one repository identity.</summary>
    /// <param name="repositoryIdentity">Exact repository identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task GrantAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a user-owned trust grant for one repository identity.</summary>
    /// <param name="repositoryIdentity">Exact repository identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RevokeAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists user-owned plan trust grants outside repository-controlled configuration.</summary>
internal sealed class UserPlanTrustGrantStore : IUserPlanTrustGrantStore
{
    private const long MaximumTrustStoreBytes = 1024 * 1024;
    private const int MaximumTrustedRepositories = 4096;
    private static readonly JsonNodeOptions JsonNodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string? _trustStorePath;

    /// <summary>Initializes a new instance of the <see cref="UserPlanTrustGrantStore"/> class.</summary>
    /// <param name="trustStorePath">User-owned trust store path, or <see langword="null" /> to disable persistence.</param>
    public UserPlanTrustGrantStore(string? trustStorePath = null)
    {
        if (!string.IsNullOrWhiteSpace(trustStorePath))
        {
            _trustStorePath = Path.GetFullPath(trustStorePath);
        }
    }

    /// <inheritdoc />
    public bool IsGranted(string repositoryIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        if (_trustStorePath is null || !File.Exists(_trustStorePath))
        {
            return false;
        }

        var root = ReadRoot(_trustStorePath);
        var repositories = ReadRepositories(root, required: false);
        if (repositories is null)
        {
            return false;
        }

        foreach (var grant in repositories)
        {
            if (string.Equals(grant.Key, repositoryIdentity, StringComparison.Ordinal))
            {
                return ReadGrantValue(grant.Value, repositoryIdentity);
            }
        }

        return false;
    }

    /// <inheritdoc />
    public Task GrantAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        return SetGrantAsync(repositoryIdentity, isGranted: true, cancellationToken);
    }

    /// <inheritdoc />
    public Task RevokeAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        return SetGrantAsync(repositoryIdentity, isGranted: false, cancellationToken);
    }

    private async Task SetGrantAsync(
        string repositoryIdentity,
        bool isGranted,
        CancellationToken cancellationToken)
    {
        if (_trustStorePath is null
            || (!isGranted && !File.Exists(_trustStorePath)))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_trustStorePath)
            ?? throw new InvalidOperationException("The user plan-policy trust path has no parent directory.");
        Directory.CreateDirectory(directory);
        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            _trustStorePath,
            async token => await SetGrantCoreAsync(repositoryIdentity, isGranted, token),
            cancellationToken);
    }

    private async Task SetGrantCoreAsync(
        string repositoryIdentity,
        bool isGranted,
        CancellationToken cancellationToken)
    {
        var trustStorePath = _trustStorePath
            ?? throw new InvalidOperationException("A user plan-policy trust path is required.");
        var root = File.Exists(trustStorePath)
            ? await ReadRootAsync(trustStorePath, cancellationToken)
            : [];
        var repositories = ReadRepositories(root, required: false) ?? [];
        root["trustedRepositories"] = repositories;
        if (isGranted)
        {
            if (repositories.Count >= MaximumTrustedRepositories
                && !repositories.TryGetPropertyValue(repositoryIdentity, out _))
            {
                throw new InvalidOperationException("User plan-policy trust store contains too many repository grants.");
            }

            repositories[repositoryIdentity] = true;
        }
        else
        {
            repositories.Remove(repositoryIdentity);
            if (repositories.Count == 0)
            {
                root.Remove("trustedRepositories");
            }
        }

        await WriteAtomicAsync(trustStorePath, root, cancellationToken);
    }

    private static async Task<JsonObject> ReadRootAsync(
        string trustStorePath,
        CancellationToken cancellationToken)
    {
        EnsureTrustStoreSize(trustStorePath);
        var root = JsonNode.Parse(
            await File.ReadAllTextAsync(trustStorePath, cancellationToken),
            JsonNodeOptions,
            documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("User plan-policy trust store must contain a JSON object.");
        _ = ReadRepositories(root, required: false);
        return root;
    }

    private static JsonObject ReadRoot(string trustStorePath)
    {
        EnsureTrustStoreSize(trustStorePath);
        var root = JsonNode.Parse(
            File.ReadAllText(trustStorePath),
            JsonNodeOptions,
            documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("User plan-policy trust store must contain a JSON object.");
        _ = ReadRepositories(root, required: false);
        return root;
    }

    private static JsonObject? ReadRepositories(JsonObject root, bool required)
    {
        if (!root.TryGetPropertyValue("trustedRepositories", out var repositoriesNode)
            || repositoriesNode is null)
        {
            return required
                ? throw new InvalidOperationException(
                    "User plan-policy trust store must contain trusted repository grants.")
                : null;
        }

        if (repositoriesNode is not JsonObject repositories)
        {
            throw new InvalidOperationException(
                "User plan-policy trust store trustedRepositories must contain a JSON object.");
        }

        if (repositories.Count > MaximumTrustedRepositories)
        {
            throw new InvalidOperationException(
                "User plan-policy trust store contains too many repository grants.");
        }

        var exactRepositories = new JsonObject();
        foreach (var grant in repositories)
        {
            _ = ReadGrantValue(grant.Value, grant.Key);
            exactRepositories[grant.Key] = grant.Value?.DeepClone();
        }

        return exactRepositories;
    }

    private static bool ReadGrantValue(JsonNode? node, string repositoryIdentity)
    {
        if (node is null)
        {
            throw new InvalidOperationException(
                $"User plan-policy trust grant for '{repositoryIdentity}' must be a Boolean.");
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"User plan-policy trust grant for '{repositoryIdentity}' must be a Boolean.",
                exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"User plan-policy trust grant for '{repositoryIdentity}' must be a Boolean.",
                exception);
        }
    }

    private static async Task WriteAtomicAsync(
        string trustStorePath,
        JsonObject root,
        CancellationToken cancellationToken)
    {
        var temporaryPath = trustStorePath + $".{Guid.NewGuid():N}.tmp";
        Exception? primaryException = null;
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(JsonOptions) + Environment.NewLine,
                cancellationToken);
            File.Move(temporaryPath, trustStorePath, overwrite: true);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch when (primaryException is not null)
                {
                }
            }
        }
    }

    private static void EnsureTrustStoreSize(string trustStorePath)
    {
        var fileInfo = new FileInfo(trustStorePath);
        if (fileInfo.Length > MaximumTrustStoreBytes)
        {
            throw new InvalidOperationException("User plan-policy trust store is too large.");
        }
    }
}
