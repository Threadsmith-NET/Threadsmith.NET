namespace Threadsmith.Context;

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Immutable safe metadata for one loaded deployed prompt asset.</summary>
public sealed record LoadedPromptAssetMetadata
{
    /// <summary>Gets the flat deployed filename.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the exact UTF-8 byte count.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the SHA-256 digest prefixed with <c>sha256:</c>.</summary>
    public required string Digest { get; init; }
}

/// <summary>Eagerly loads and strictly renders the required flat deployed prompt catalog.</summary>
public sealed class DeployedPromptLoader : IPromptLoader
{
    /// <summary>Maximum UTF-8 bytes accepted from one prompt asset.</summary>
    public const int MaximumFileBytes = 128 * 1024;

    /// <summary>Maximum aggregate UTF-8 bytes accepted from the required prompt catalog.</summary>
    public const int MaximumCatalogBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyDictionary<string, LoadedPrompt> _prompts;

    private DeployedPromptLoader(
        IReadOnlyDictionary<string, LoadedPrompt> prompts,
        IReadOnlyList<LoadedPromptAssetMetadata> assets,
        long totalBytes,
        string catalogDigest)
    {
        _prompts = prompts;
        Assets = assets;
        TotalBytes = totalBytes;
        CatalogDigest = catalogDigest;
    }

    /// <summary>Gets safe per-file metadata in deterministic filename order.</summary>
    public IReadOnlyList<LoadedPromptAssetMetadata> Assets { get; }

    /// <summary>Gets the aggregate UTF-8 byte count.</summary>
    public long TotalBytes { get; }

    /// <summary>Gets a digest over the ordered filename and content-digest catalog.</summary>
    public string CatalogDigest { get; }

    /// <summary>Loads the complete required catalog from the application base directory.</summary>
    /// <param name="applicationBaseDirectory">Trusted application base directory supplied by the composition root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An immutable loader published only after every required asset succeeds.</returns>
    public static async Task<DeployedPromptLoader> LoadAsync(
        string applicationBaseDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var baseDirectory = Path.GetFullPath(applicationBaseDirectory);
        var promptRoot = Path.GetFullPath(Path.Combine(baseDirectory, "prompts"));
        EnsureDirectChild(baseDirectory, promptRoot);
        EnsureOrdinaryDirectory(promptRoot);
        ValidateCatalogDefinitions();
        ValidatePhysicalNameCollisions(promptRoot);

        var loaded = new Dictionary<string, LoadedPrompt>(StringComparer.Ordinal);
        var metadata = new List<LoadedPromptAssetMetadata>(PromptAssetCatalog.All.Count);
        long totalBytes = 0;
        foreach (var definition in PromptAssetCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(promptRoot, definition.FileName));
            EnsureDirectChild(promptRoot, path);
            var bytes = await ReadPromptBytesAsync(path, definition.FileName, cancellationToken);
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > MaximumCatalogBytes)
            {
                throw CreateInitializationException(definition.FileName, "catalog-size-limit");
            }

            var content = Decode(bytes, definition.FileName);
            ValidateTemplate(definition, content);
            var digest = ComputeDigest(bytes);
            loaded.Add(definition.FileName, new LoadedPrompt(definition, content));
            metadata.Add(new LoadedPromptAssetMetadata
            {
                FileName = definition.FileName,
                Bytes = bytes.Length,
                Digest = digest,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var orderedMetadata = metadata
            .OrderBy(asset => asset.FileName, StringComparer.Ordinal)
            .ToArray();
        var catalogDigest = ComputeCatalogDigest(orderedMetadata);
        return new DeployedPromptLoader(
            new ReadOnlyDictionary<string, LoadedPrompt>(loaded),
            Array.AsReadOnly(orderedMetadata),
            totalBytes,
            catalogDigest);
    }

    /// <inheritdoc />
    public string Get(string promptFileName)
    {
        return Resolve(promptFileName).Content;
    }

    /// <inheritdoc />
    public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var prompt = Resolve(promptFileName);
        var supplied = SnapshotTokens(tokens, prompt.Definition);
        foreach (var requiredToken in prompt.Definition.RequiredTokens)
        {
            if (!supplied.ContainsKey(requiredToken))
            {
                throw new ArgumentException(
                    $"Required prompt token '{requiredToken}' was not supplied for '{promptFileName}'.",
                    nameof(tokens));
            }
        }

        var builder = new StringBuilder(prompt.Content.Length);
        var cursor = 0;
        foreach (var marker in EnumerateMarkers(prompt.Content, promptFileName))
        {
            builder.Append(prompt.Content, cursor, marker.Start - cursor);
            if (supplied.TryGetValue(marker.Name, out var value))
            {
                builder.Append(value);
            }

            cursor = marker.End;
        }

        builder.Append(prompt.Content, cursor, prompt.Content.Length - cursor);
        return builder.ToString();
    }

    private static string ComputeCatalogDigest(IReadOnlyList<LoadedPromptAssetMetadata> metadata)
    {
        var canonical = string.Join(
            "\n",
            metadata.Select(asset => $"{asset.FileName}\0{asset.Bytes}\0{asset.Digest}"));
        return ComputeDigest(Encoding.UTF8.GetBytes(canonical));
    }

    private static string ComputeDigest(ReadOnlySpan<byte> bytes)
    {
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static InvalidOperationException CreateInitializationException(
        string fileName,
        string category,
        Exception? innerException = null)
    {
        return new InvalidOperationException(
            $"Prompt asset initialization failed for '{fileName}' ({category}).",
            innerException);
    }

    private static string Decode(byte[] bytes, string fileName)
    {
        try
        {
            var content = StrictUtf8.GetString(bytes);
            if (content.IndexOf('\0', StringComparison.Ordinal) >= 0)
            {
                throw CreateInitializationException(fileName, "nul-content");
            }

            return content;
        }
        catch (DecoderFallbackException exception)
        {
            throw CreateInitializationException(fileName, "invalid-utf8", exception);
        }
    }

    private static IEnumerable<TemplateMarker> EnumerateMarkers(string template, string fileName)
    {
        var cursor = 0;
        while (cursor < template.Length)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            var close = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw CreateInitializationException(fileName, "invalid-token-marker");
            }

            var name = template[(start + 2)..close];
            if (!IsValidTokenName(name))
            {
                throw CreateInitializationException(fileName, "invalid-token-marker");
            }

            yield return new TemplateMarker(start, close + 2, name);
            cursor = close + 2;
        }
    }

    private static void EnsureDirectChild(string parentDirectory, string childPath)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        var child = Path.GetFullPath(childPath);
        var expectedPrefix = parent + Path.DirectorySeparatorChar;
        if (!child.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || child.AsSpan(expectedPrefix.Length).Contains(Path.DirectorySeparatorChar)
            || child.AsSpan(expectedPrefix.Length).Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("The deployed prompt path is not a confined flat child path.");
        }
    }

    private static void EnsureOrdinaryDirectory(string promptRoot)
    {
        if (!Directory.Exists(promptRoot))
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is missing.");
        }

        try
        {
            if (File.GetAttributes(promptRoot).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The deployed prompt catalog directory cannot be a reparse point.");
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
        catch (System.Security.SecurityException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
    }

    private static bool IsValidFileName(string fileName)
    {
        if (!fileName.EndsWith(".md", StringComparison.Ordinal)
            || fileName.Length <= 3)
        {
            return false;
        }

        return fileName[..^3].All(character =>
            character is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '-' or '_');
    }

    private static bool IsValidTokenName(string name)
    {
        return name.Length > 0
            && name[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
            && name.All(character =>
                character is (>= 'A' and <= 'Z')
                    or (>= 'a' and <= 'z')
                    or (>= '0' and <= '9'));
    }

    private static async Task<byte[]> ReadPromptBytesAsync(
        string path,
        string fileName,
        CancellationToken cancellationToken)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw CreateInitializationException(fileName, "non-ordinary-file");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes)
            {
                throw CreateInitializationException(fileName, "file-size-limit");
            }

            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.Length != bytes.Length)
            {
                throw CreateInitializationException(fileName, "changed-during-read");
            }

            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw CreateInitializationException(fileName, "missing");
        }
        catch (DirectoryNotFoundException)
        {
            throw CreateInitializationException(fileName, "missing");
        }
        catch (UnauthorizedAccessException)
        {
            throw CreateInitializationException(fileName, "unreadable");
        }
        catch (System.Security.SecurityException)
        {
            throw CreateInitializationException(fileName, "unreadable");
        }
        catch (IOException)
        {
            throw CreateInitializationException(fileName, "unreadable");
        }
    }

    private static Dictionary<string, string> SnapshotTokens(
        IReadOnlyDictionary<string, string> tokens,
        PromptAssetDefinition definition)
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (!supplied.TryAdd(
                token.Key,
                token.Value ?? throw new ArgumentException("Prompt token values cannot be null.", nameof(tokens))))
            {
                throw new ArgumentException("Duplicate prompt token names are not allowed.", nameof(tokens));
            }

            if (!definition.RequiredTokens.Contains(token.Key)
                && !definition.OptionalTokens.Contains(token.Key))
            {
                throw new ArgumentException(
                    $"Prompt token '{token.Key}' is not declared for '{definition.FileName}'.",
                    nameof(tokens));
            }
        }

        return supplied;
    }

    private static void ValidateCatalogDefinitions()
    {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in PromptAssetCatalog.All)
        {
            if (!IsValidFileName(definition.FileName)
                || !fileNames.Add(definition.FileName)
                || definition.RequiredTokens.Overlaps(definition.OptionalTokens)
                || definition.RequiredTokens.Concat(definition.OptionalTokens).Any(token => !IsValidTokenName(token)))
            {
                throw new InvalidOperationException("The code-owned prompt catalog is invalid.");
            }
        }
    }

    private static void ValidatePhysicalNameCollisions(string promptRoot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(promptRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    && !names.Add(fileName))
                {
                    throw new InvalidOperationException(
                        "The deployed prompt catalog contains a case-insensitive filename collision.");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
        catch (System.Security.SecurityException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The deployed prompt catalog directory is unreadable.");
        }
    }

    private static void ValidateTemplate(PromptAssetDefinition definition, string content)
    {
        var markers = EnumerateMarkers(content, definition.FileName)
            .Select(marker => marker.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (markers.Any(marker =>
                !definition.RequiredTokens.Contains(marker)
                && !definition.OptionalTokens.Contains(marker))
            || definition.RequiredTokens.Any(required => !markers.Contains(required))
            || definition.OptionalTokens.Any(optional => !markers.Contains(optional)))
        {
            throw CreateInitializationException(definition.FileName, "token-contract");
        }
    }

    private LoadedPrompt Resolve(string promptFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptFileName);
        return _prompts.TryGetValue(promptFileName, out var prompt)
            ? prompt
            : throw new ArgumentException("The prompt filename is not declared by the host catalog.", nameof(promptFileName));
    }

    private sealed record LoadedPrompt(PromptAssetDefinition Definition, string Content);

    private sealed record TemplateMarker(int Start, int End, string Name);
}
