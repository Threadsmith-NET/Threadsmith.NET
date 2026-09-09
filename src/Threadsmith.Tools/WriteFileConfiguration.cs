namespace Threadsmith.Tools;

using Microsoft.Extensions.Configuration;

/// <summary>Resolves the direct-write folder allowlist and fences it to the active repository.</summary>
public sealed class WriteFileConfiguration
{
    /// <summary>Ordinary configuration key for the replacing folder list.</summary>
    public const string AllowedFoldersKey = "tools:writeFile:allowedFolders";

    private readonly string _initialRepository;
    private readonly string[] _initialFolders;
    private readonly string[] _fallbackFolders;
    private Snapshot _current;

    /// <summary>Initializes a new instance of the <see cref="WriteFileConfiguration"/> class.</summary>
    public WriteFileConfiguration(IConfiguration configuration, IConfiguration fallbackConfiguration, string repositoryPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fallbackConfiguration);
        _initialRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        _fallbackFolders = LoadAllowedFolders(fallbackConfiguration);
        _initialFolders = LoadAllowedFolders(configuration);
        _current = new Snapshot(_initialRepository, _initialFolders);
    }

    /// <summary>Reads only the highest-precedence list, including an explicitly empty list.</summary>
    public static string[] LoadAllowedFolders(IConfiguration configuration, IReadOnlyList<string>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                var keys = provider.GetChildKeys([], AllowedFoldersKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var hasValue = provider.TryGet(AllowedFoldersKey, out var scalar);
                if (keys.Length == 0 && !hasValue)
                {
                    continue;
                }

                if (scalar is { Length: > 0 } || keys.Any(key => !int.TryParse(key, out var index) || index < 0))
                {
                    throw new InvalidOperationException($"{AllowedFoldersKey} must be an array of folder paths.");
                }

                return
                [
                    .. keys.OrderBy(key => int.Parse(key, System.Globalization.CultureInfo.InvariantCulture))
                        .Select(key => provider.TryGet($"{AllowedFoldersKey}:{key}", out var value) ? ValidateFolder(value) : throw new InvalidOperationException("Missing write folder.")),
                ];
            }

            return fallback?.ToArray() ?? [".inbox"];
        }

        var section = configuration.GetSection(AllowedFoldersKey);
        return section.Exists()
            ? [.. (section.Get<string[]>() ?? []).Select(ValidateFolder)]
            : fallback?.ToArray() ?? [".inbox"];
    }

    /// <summary>Rebinds repository overrides without inheriting another repository's write grants.</summary>
    public void BindRepository(string repositoryPath, IConfiguration repositoryConfiguration)
    {
        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var folders = string.Equals(repository, _initialRepository, PathComparison)
            ? _initialFolders
            : LoadAllowedFolders(repositoryConfiguration, _fallbackFolders);
        Volatile.Write(ref _current, new Snapshot(repository, folders));
    }

    /// <summary>Returns a detached folder list only for the currently bound repository.</summary>
    internal string[] GetAllowedFolders(string repositoryPath)
    {
        var snapshot = Volatile.Read(ref _current);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)), snapshot.Repository, PathComparison))
        {
            throw new UnauthorizedAccessException("write_file configuration belongs to another repository.");
        }

        return [.. snapshot.Folders];
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string ValidateFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.IndexOfAny(['*', '?']) >= 0)
        {
            throw new InvalidOperationException($"{AllowedFoldersKey} entries must be nonblank literal folder paths.");
        }

        if (!Path.IsPathFullyQualified(value) && (Path.IsPathRooted(value) || value.Split(['/', '\\']).Contains("..", StringComparer.Ordinal)))
        {
            throw new InvalidOperationException($"{AllowedFoldersKey} external folders must use fully qualified paths.");
        }

        return value;
    }

    private sealed record Snapshot(string Repository, string[] Folders);
}
