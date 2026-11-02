namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Projects existing invocation authority into worker file access.</summary>
internal static class ScriptFilePolicy
{
    /// <summary>Gets the installed on-demand scripting reference.</summary>
    internal static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "ThreadsmithDocs", "docs", "operations", "csharp-scripting.md");

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
}
