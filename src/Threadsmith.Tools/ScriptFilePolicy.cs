namespace Threadsmith.Tools;

using System.Text;
using Threadsmith.Core;

/// <summary>Projects existing invocation authority into worker file access.</summary>
internal static class ScriptFilePolicy
{
    /// <summary>Gets the installed on-demand scripting reference.</summary>
    internal static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "ThreadsmithDocs", "docs", "operations", "csharp-scripting-execution.md");

    /// <summary>Freezes invocation authority for one worker.</summary>
    internal static ScriptFileScope CreateScope(ToolInvocationContext context, int maximumWriteBytes)
    {
        if (context.SecurityPolicy is { } policy)
        {
            context.PolicyBinding?.Validate(policy);
        }

        var scratchpad = context.Scratchpad is { IsActive: true, RootPath: { } root }
            ? ToolPathRules.NormalizeScratchpadPath(root, context)
            : throw new UnauthorizedAccessException("Enable the session scratchpad before using csharp_script.");
        return new ScriptFileScope
        {
            RepositoryPath = context.RepositoryPath,
            ScratchpadPath = scratchpad,
            ReadRoots = [.. context.ApprovedRoots.Select(path => Path.GetFullPath(path, context.RepositoryPath)),
                .. context.SecurityPolicy?.ReadFolders ?? []],
            ProhibitedPaths = context.ProhibitedPaths,
            ControlPaths = context.SecurityPolicy?.ControlPaths ?? [],
            ControlDirectories = context.SecurityPolicy?.ControlDirectories ?? [],
            MaximumWriteBytes = maximumWriteBytes,
        };
    }

    /// <summary>Validates ordinary file reads and the exact installed reference.</summary>
    internal static string ValidateRead(string path, ToolInvocationContext context)
    {
        if (context.SecurityPolicy is { } policy)
        {
            context.PolicyBinding?.Validate(policy);
        }

        var normalized = Path.GetFullPath(path, context.RepositoryPath);
        if (RepositoryPathPolicy.GetPathComparer(normalized).Equals(normalized, ReferencePath))
        {
            ScopedFilePaths.RejectLinks(normalized, AppContext.BaseDirectory);
            return normalized;
        }

        if (ToolPathRules.IsWithinScratchpad(normalized, context))
        {
            return ToolPathRules.NormalizeScratchpadPath(normalized, context);
        }

        return ScopedFilePaths.ValidateRead(
            path,
            context.RepositoryPath,
            context.RepositoryPath,
            context.ApprovedRoots.Select(root => Path.GetFullPath(root, context.RepositoryPath)).Concat(context.SecurityPolicy?.ReadFolders ?? []),
            context.ProhibitedPaths);
    }

    /// <summary>Resolves source independently of external data-read grants.</summary>
    internal static string ResolveSourcePath(string path, ToolInvocationContext context, ScriptFileScope scope)
    {
        ValidateSourcePathForm(path);
        var bare = !Path.IsPathRooted(path) && path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
        var normalized = Path.GetFullPath(path, bare ? scope.ScratchpadPath : context.RepositoryPath);
        if (!RepositoryPathPolicy.TryNormalizeContainedPath(context.RepositoryPath, normalized, out var contained))
        {
            throw new UnauthorizedAccessException("Script source must stay inside the repository.");
        }

        if (!Path.GetExtension(contained).Equals(".csx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolArgumentValidationException("scriptPath must select a regular .csx file.");
        }

        // Scratchpad access is shared, but inherited prohibitions still apply.
        var roots = ToolPathRules.IsWithinScratchpad(contained, context)
            ? scope.ReadRoots.Append(scope.ScratchpadPath) : scope.ReadRoots;
        return ScopedFilePaths.ValidateRead(contained, context.RepositoryPath, context.RepositoryPath, roots, context.ProhibitedPaths);
    }

    /// <summary>Checks path syntax before any filesystem access, retaining ordinary UNC paths.</summary>
    internal static void ValidateSourcePathForm(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var drivePath = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        var deviceForm = path.Replace('/', '\\');
        if (path.Any(char.IsControl)
            || deviceForm.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || deviceForm.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || (path.Contains(':') && !(OperatingSystem.IsWindows() && drivePath && Path.IsPathFullyQualified(path) && !path[2..].Contains(':')))
            || (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)))
        {
            throw new ToolArgumentValidationException("Use an unambiguous relative or in-repository absolute .csx path.");
        }
    }

    /// <summary>Reads one bounded UTF-8 snapshot while the authorized file handle is held.</summary>
    internal static async Task<PreparedCSharpScriptSource> ReadSourceAsync(
        string path,
        ToolInvocationContext context,
        ScriptFileScope scope,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var normalized = ResolveSourcePath(path, context, scope);
        cancellationToken.ThrowIfCancellationRequested();
        var physicalRepository = ResolveTrustedRepositoryAncestors(context.RepositoryPath);
        var physicalPath = Path.GetFullPath(Path.GetRelativePath(context.RepositoryPath, normalized), physicalRepository);
        await using var stream = JsonFileSecretProvider.OpenValidatedRead(normalized, physicalPath);
        _ = ResolveSourcePath(normalized, context, scope);
        var maximumBytes = (int)Math.Min(scope.MaximumWriteBytes + 3L, ((long)maximumCharacters * 3) + 3);
        if (stream.Length > maximumBytes)
        {
            throw new ToolArgumentValidationException("Script source exceeds the byte limit.");
        }

        await using var content = new MemoryStream();
        var buffer = new byte[Math.Min(4096, maximumBytes + 1)];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - content.Length + 1)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > maximumBytes)
            {
                throw new ToolArgumentValidationException("Script source exceeds the byte limit.");
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.GetBuffer().AsSpan(0, (int)content.Length);
        var offset = bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        if (bytes.Length - offset > scope.MaximumWriteBytes)
        {
            throw new ToolArgumentValidationException("Script source exceeds the file-write byte limit.");
        }

        string code;
        try
        {
            code = new UTF8Encoding(false, true).GetString(bytes[offset..]);
        }
        catch (DecoderFallbackException)
        {
            throw new ToolArgumentValidationException("Script source must use valid UTF-8 encoding.");
        }

        if (code.Length > maximumCharacters)
        {
            throw new ToolArgumentValidationException($"code is limited to {maximumCharacters} characters.");
        }

        return new PreparedCSharpScriptSource(code, normalized);
    }

    /// <summary>Sets child-process temporary storage beneath the scratchpad.</summary>
    internal static IReadOnlyDictionary<string, string> TemporaryEnvironment(string scratchpad, string repository)
    {
        var path = Path.Combine(scratchpad, "tmp");
        ScopedFilePaths.RejectLinks(path, repository);
        Directory.CreateDirectory(path);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TMP"] = path,
            ["TEMP"] = path,
            ["TMPDIR"] = path,
        };
    }

    private static string ResolveTrustedRepositoryAncestors(string repository)
    {
        var fullPath = Path.GetFullPath(repository);
        var physical = Path.GetPathRoot(fullPath) ?? throw new UnauthorizedAccessException("Repository root is unavailable.");
        foreach (var segment in fullPath[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(physical, segment));
            physical = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }

        return physical;
    }
}
