namespace Threadsmith.Tools;

using System.ComponentModel;
using System.Text;
using Threadsmith.Core;

/// <summary>Creates a text artifact from new content or the previous archived assistant answer.</summary>
public sealed record WriteFileInput(
    [property: Description("Repository-relative or absolute destination inside an allowed folder.")] string Path,
    [property: Description("Exact text to write. Omit when useLastResponse is true.")] string? Content = null,
    [property: Description("Copy the latest archived assistant response from this session without regenerating it.")] bool UseLastResponse = false,
    [property: Description("Replace an existing file only when the user requested replacement. Defaults to false.")] bool Overwrite = false,
    [property: Description("Opaque image artifact reference from this session. Mutually exclusive with content and useLastResponse.")] string? ArtifactId = null);

/// <summary>Confirms the destination and UTF-8 byte count without echoing file contents.</summary>
public sealed record WriteFileOutput(string Path, int BytesWritten, ConversationMessageId? SourceMessageId)
{
    /// <summary>Image source metadata, including digest and provider response identity.</summary>
    public ImageArtifact? Artifact { get; init; }
}

/// <summary>Writes bounded UTF-8 artifacts directly inside configured folders.</summary>
public sealed class WriteFileTool : Tool<WriteFileInput, WriteFileOutput>
{
    private readonly ToolLimits _limits;
    private readonly IImageArtifactStore? _artifacts;
    private static readonly UTF8Encoding _encoding = new(false, true);
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".yaml", ".yml", ".xml", ".log", ".rst",
    };

    private readonly WriteFileConfiguration _configuration;
    private readonly IConversationStore _conversations;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="WriteFileTool"/> class.</summary>
    public WriteFileTool(WriteFileConfiguration configuration, IConversationStore conversations, IPromptLoader promptLoader, ToolLimits? limits = null, IImageArtifactStore? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(promptLoader);
        _limits = limits ?? ToolLimits.Default;
        _artifacts = artifacts;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.WriteFileMaximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.WriteFileMaximumPathCharacters);
        _configuration = configuration;
        _conversations = conversations;
        _definition = ToolDefinitionFactory.Create<WriteFileInput, WriteFileOutput>(
            "write_file",
            () => promptLoader.Render(PromptFileNames.ToolWriteFileDescription, new Dictionary<string, string>
            {
                ["MaximumContentBytes"] = _limits.WriteFileMaximumContentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            ToolCategory.FileWrite,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.WritesFiles,
            TimeSpan.FromSeconds(30),
            8192) with
        {
            DisplayName = "Write File",
            Idempotency = ToolIdempotency.NonIdempotent,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<WriteFileOutput>> ExecuteAsync(
        WriteFileInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = ValidatePath(input.Path, context.Invocation);
        if (!input.Overwrite && File.Exists(destination))
        {
            throw new IOException("The destination already exists. Set overwrite=true only for a requested replacement.");
        }

        var content = input.Content;
        ConversationMessageId? sourceMessageId = null;
        if (input.UseLastResponse)
        {
            var snapshot = await _conversations.GetSnapshotAsync(context.SessionId, includeBodies: true, cancellationToken: cancellationToken);
            var message = snapshot.Messages
                .Where(message => message.SessionId == context.SessionId && message.Role == ConversationRole.Assistant && message.RunId != context.RunId)
                .MaxBy(message => message.Sequence);
            content = message?.Content ?? throw new InvalidOperationException("The latest assistant response is unavailable in this session's archive.");
            sourceMessageId = message?.Id;
        }

        ImageArtifact? artifact = null;
        ReadOnlyMemory<byte> bytes;
        if (input.ArtifactId is { } artifactId)
        {
            if (context.Invocation.IsChildAgent && !context.Invocation.AdmittedImageIds.Contains(artifactId))
            {
                throw new UnauthorizedAccessException("The image reference was not admitted to this child assignment. Read the source within the assigned scope first.");
            }

            var artifacts = _artifacts ?? throw new InvalidOperationException("Image artifact storage is unavailable.");
            artifact = await artifacts.GetImageAsync(artifactId, context.SessionId, cancellationToken);
            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (artifact.MediaType == "image/png" ? extension != ".png" : extension is not (".jpg" or ".jpeg"))
            {
                throw new ToolArgumentValidationException("Destination extension must match the validated image MIME type.");
            }

            bytes = await artifacts.ReadImageAsync(artifact, context.SessionId, cancellationToken);
        }
        else
        {
            bytes = _encoding.GetBytes(content ?? throw new InvalidOperationException("No file content was supplied."));
        }

        if (artifact is null && bytes.Length > _limits.WriteFileMaximumContentBytes)
        {
            throw new ToolArgumentValidationException($"write_file content exceeds the configured {_limits.WriteFileMaximumContentBytes} UTF-8 byte limit.");
        }

        await AtomicFileWriter.WriteAsync(destination, bytes, input.Overwrite, () => ValidatePath(destination, context.Invocation), cancellationToken);

        return new ToolExecution<WriteFileOutput>(new WriteFileOutput(destination, bytes.Length, sourceMessageId) { Artifact = artifact }, [], false);
    }

    /// <summary>Checks the compiled write capability's allowlist before pipeline admission and immediately before I/O.</summary>
    internal string ValidatePath(string path, ToolInvocationContext context)
    {
        var repository = Path.GetFullPath(context.RepositoryPath);
        var destination = Path.GetFullPath(path, repository);
        var caseContext = new RepositoryPathCaseContext(repository);
        var withinRepository = false;
        if (RepositoryPathPolicy.TryNormalizeContainedPath(repository, destination, out var containedDestination, caseContext: caseContext))
        {
            destination = containedDestination;
            withinRepository = true;
        }

        if (!context.AllowDirectWrites)
        {
            throw new UnauthorizedAccessException("The child assignment does not permit direct artifact writes.");
        }

        if (context.SecurityPolicy is { } policy)
        {
            context.PolicyBinding?.Validate(policy);
        }

        AuthorityControlPathGuard.Validate(destination, repository, context.SecurityPolicy?.ControlPaths, context.SecurityPolicy?.ControlDirectories);
        if (context.IsChildAgent && !context.ApprovedRoots.Any(root =>
                RepositoryPathPolicy.ContainsPath(Path.GetFullPath(root, repository), destination, caseContext: caseContext)))
        {
            throw new UnauthorizedAccessException("The direct write is outside the child assignment scope.");
        }

        var scratchpadDestination = ToolPathRules.IsWithinScratchpad(destination, context, caseContext: caseContext);
        if (Path.GetExtension(destination).ToLowerInvariant() is ".cs" or ".csx" && !scratchpadDestination)
        {
            throw new UnauthorizedAccessException("Temporary C# files must be written inside the active scratchpad.");
        }

        IReadOnlyList<string> folders = context.SecurityPolicy is { } effective ? effective.WriteFolders : _configuration.GetAllowedFolders(repository);
        var grantingRoot = scratchpadDestination ? null : folders.Select(folder => Path.GetFullPath(folder, repository))
            .FirstOrDefault(root => RepositoryPathPolicy.ContainsPath(root, destination, caseContext: caseContext)
                && !RepositoryPathPolicy.PathsEqual(root, destination, caseContext: caseContext));
        if (!scratchpadDestination && grantingRoot is null)
        {
            throw new UnauthorizedAccessException($"write_file destination is outside {WriteFileConfiguration.AllowedFoldersKey} (default: .inbox). No file was written.");
        }

        var relative = Path.GetRelativePath(repository, destination).Replace('\\', '/');
        if (!scratchpadDestination && RepositoryPathPolicy.IsProhibited(relative, context.ProhibitedPaths, repository, caseContext))
        {
            throw new UnauthorizedAccessException("write_file destination matches a prohibited path.");
        }

        var segments = destination.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (!scratchpadDestination && (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            || Path.GetFileName(destination).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
            )
        {
            throw new UnauthorizedAccessException("write_file cannot change Git metadata, Threadsmith settings, or AGENTS.md instructions.");
        }

        if (ToolPathRules.ContainsReservedWindowsDeviceName(relative)
            || (OperatingSystem.IsWindows() && (segments.Skip(1).Any(segment => segment.Contains(':')) || segments.Any(segment => segment.EndsWith(' ') || segment.EndsWith('.')))))
        {
            throw new UnauthorizedAccessException("write_file destination contains an ambiguous or reserved Windows path.");
        }

        if (scratchpadDestination)
        {
            return ToolPathRules.NormalizeScratchpadPath(destination, context, caseContext: caseContext);
        }

        var authorityRoot = withinRepository
            ? repository
            : grantingRoot ?? throw new UnauthorizedAccessException("No folder grants this write.");
        ScopedFilePaths.RejectLinks(destination, authorityRoot, allowMissing: true, caseContext);

        return destination;
    }

    /// <inheritdoc />
    protected override void ValidateInput(WriteFileInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Path) || input.Path.Length > _limits.WriteFileMaximumPathCharacters || input.Path.Any(char.IsControl))
        {
            throw new ToolArgumentValidationException($"path must be a nonblank file path of at most {_limits.WriteFileMaximumPathCharacters} characters.");
        }

        if (Path.IsPathRooted(input.Path) && !Path.IsPathFullyQualified(input.Path))
        {
            throw new ToolArgumentValidationException("Use a repository-relative or fully qualified path.");
        }

        if (input.ArtifactId is null && !_extensions.Contains(Path.GetExtension(input.Path)) && Path.GetExtension(input.Path).ToLowerInvariant() is not (".cs" or ".csx"))
        {
            throw new ToolArgumentValidationException("write_file supports text artifacts: .txt, .md, .markdown, .json, .csv, .tsv, .yaml, .yml, .xml, .log, and .rst. Use the mutation workflow for source code.");
        }

        if ((input.UseLastResponse ? 1 : 0) + (input.Content is not null ? 1 : 0) + (input.ArtifactId is not null ? 1 : 0) != 1)
        {
            throw new ToolArgumentValidationException("Supply exactly one of content, useLastResponse=true, or artifactId.");
        }

        if (input.ArtifactId is not null && (!Guid.TryParseExact(input.ArtifactId, "N", out _)
            || Path.GetExtension(input.Path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")))
        {
            throw new ToolArgumentValidationException("Image export requires an opaque artifact ID and a .png, .jpg or .jpeg destination.");
        }

        if (input.Content is { } content && (content.Length > _limits.WriteFileMaximumContentBytes || _encoding.GetByteCount(content) > _limits.WriteFileMaximumContentBytes))
        {
            throw new ToolArgumentValidationException($"write_file content exceeds the configured {_limits.WriteFileMaximumContentBytes} UTF-8 byte limit.");
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(WriteFileInput input)
    {
        return input.Path;
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(WriteFileInput input, ToolInvocationContext context)
    {
        return [input.Path];
    }
}
