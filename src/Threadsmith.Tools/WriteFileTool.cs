namespace Threadsmith.Tools;

using System.ComponentModel;
using System.Text;
using Threadsmith.Core;

/// <summary>Creates a text artifact from new content or the previous archived assistant answer.</summary>
public sealed record WriteFileInput(
    [property: Description("Repository-relative or absolute destination inside an allowed folder.")] string Path,
    [property: Description("Exact text to write. Omit when useLastResponse is true.")] string? Content = null,
    [property: Description("Copy the latest archived assistant response from this session without regenerating it.")] bool UseLastResponse = false,
    [property: Description("Replace an existing file only when the user requested replacement. Defaults to false.")] bool Overwrite = false);

/// <summary>Confirms the destination and UTF-8 byte count without echoing file contents.</summary>
public sealed record WriteFileOutput(string Path, int BytesWritten, ConversationMessageId? SourceMessageId);

/// <summary>Writes bounded UTF-8 artifacts directly inside configured folders.</summary>
public sealed class WriteFileTool : Tool<WriteFileInput, WriteFileOutput>
{
    private readonly ToolLimits _limits;
    private static readonly UTF8Encoding _encoding = new(false, true);
    private static readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".yaml", ".yml", ".xml", ".log", ".rst",
    };

    private readonly WriteFileConfiguration _configuration;
    private readonly IConversationStore _conversations;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="WriteFileTool"/> class.</summary>
    public WriteFileTool(WriteFileConfiguration configuration, IConversationStore conversations, IPromptLoader promptLoader, ToolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(promptLoader);
        _limits = limits ?? ToolLimits.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.WriteFileMaximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.WriteFileMaximumPathCharacters);
        _configuration = configuration;
        _conversations = conversations;
        _definition = ToolDefinitionFactory.Create<WriteFileInput, WriteFileOutput>(
            "write_file",
            promptLoader.Get(PromptFileNames.ToolWriteFileDescription),
            ToolCategory.FileWrite,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.WritesFiles,
            TimeSpan.FromSeconds(30),
            8192) with
        {
            DisplayName = "Write File",
            ConversationAvailable = true,
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
            var snapshot = await _conversations.GetSnapshotAsync(context.SessionId, includeBodies: true, cancellationToken);
            var message = snapshot.Messages
                .Where(message => message.SessionId == context.SessionId && message.Role == ConversationRole.Assistant && message.RunId != context.RunId)
                .MaxBy(message => message.Sequence);
            content = message?.Content ?? throw new InvalidOperationException("The latest assistant response is unavailable in this session's archive.");
            sourceMessageId = message?.Id;
        }

        var bytes = _encoding.GetBytes(content ?? throw new InvalidOperationException("No file content was supplied."));
        if (bytes.Length > _limits.WriteFileMaximumContentBytes)
        {
            throw new ToolArgumentValidationException($"write_file content exceeds the configured {_limits.WriteFileMaximumContentBytes} UTF-8 byte limit.");
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("The destination needs a parent folder.");
        _ = ValidatePath(destination, context.Invocation);
        Directory.CreateDirectory(parent);
        _ = ValidatePath(destination, context.Invocation);
        var temporary = Path.Combine(parent, $".threadsmith-write-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = ValidatePath(destination, context.Invocation);
            File.Move(temporary, destination, input.Overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }

        return new ToolExecution<WriteFileOutput>(new WriteFileOutput(destination, bytes.Length, sourceMessageId), [], false);
    }

    /// <summary>Checks the compiled write capability's allowlist before pipeline admission and immediately before I/O.</summary>
    internal string ValidatePath(string path, ToolInvocationContext context)
    {
        var repository = Path.GetFullPath(context.RepositoryPath);
        var destination = Path.GetFullPath(path, repository);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var allowed = _configuration.GetAllowedFolders(repository).Any(folder =>
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder, repository));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return destination.StartsWith(prefix, comparison);
        });
        if (!allowed)
        {
            throw new UnauthorizedAccessException($"write_file destination is outside {WriteFileConfiguration.AllowedFoldersKey} (default: .inbox). No file was written.");
        }

        var relative = Path.GetRelativePath(repository, destination).Replace('\\', '/');
        if (RepositoryPathPolicy.IsProhibited(relative, context.ProhibitedPaths))
        {
            throw new UnauthorizedAccessException("write_file destination matches a prohibited path.");
        }

        var segments = destination.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals(".threadsmith", StringComparison.OrdinalIgnoreCase))
            || Path.GetFileName(destination).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("write_file cannot change Git metadata, Threadsmith settings, or AGENTS.md instructions.");
        }

        if (ToolPathRules.ContainsReservedWindowsDeviceName(relative)
            || (OperatingSystem.IsWindows() && (segments.Skip(1).Any(segment => segment.Contains(':')) || segments.Any(segment => segment.EndsWith(' ') || segment.EndsWith('.')))))
        {
            throw new UnauthorizedAccessException("write_file destination contains an ambiguous or reserved Windows path.");
        }

        // Reject linked ancestors, including the allowlisted folder itself and dangling links.
        for (var current = destination; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("write_file cannot traverse symbolic links or junctions.");
            }
        }

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

        if (!_extensions.Contains(Path.GetExtension(input.Path)))
        {
            throw new ToolArgumentValidationException("write_file supports text artifacts: .txt, .md, .markdown, .json, .csv, .tsv, .yaml, .yml, .xml, .log, and .rst. Use the mutation workflow for source code.");
        }

        if (input.UseLastResponse == (input.Content is not null))
        {
            throw new ToolArgumentValidationException("Supply exactly one of content or useLastResponse=true.");
        }

        if (input.Content is { } content && (content.Length > _limits.WriteFileMaximumContentBytes || _encoding.GetByteCount(content) > _limits.WriteFileMaximumContentBytes))
        {
            throw new ToolArgumentValidationException($"write_file content exceeds the configured {_limits.WriteFileMaximumContentBytes} UTF-8 byte limit.");
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(WriteFileInput input) => input.Path;

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(WriteFileInput input, ToolInvocationContext context) => [input.Path];
}
