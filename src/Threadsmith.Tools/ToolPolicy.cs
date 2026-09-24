namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>A policy decision made before tool execution.</summary>
public sealed record ToolPolicyDecision(
    bool IsAllowed,
    ApprovalLevel RequiredApproval,
    string Reason);

/// <summary>Evaluates repository, path, command, and network policy plus secret-reference validity.</summary>
public interface IPolicyEngine
{
    /// <summary>Evaluates a validated tool request.</summary>
    ToolPolicyDecision Evaluate(
        ITool tool,
        object input,
        ToolInvocationContext context);
}

/// <summary>Default least-privilege policy for built-in tools.</summary>
public sealed class DefaultPolicyEngine : IPolicyEngine
{
    /// <inheritdoc />
    public ToolPolicyDecision Evaluate(
        ITool tool,
        object input,
        ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var requiredApproval = tool.Definition.RequiredApproval == ApprovalLevel.User
            || context.RequireApprovalToolIds.Contains(
                tool.Definition.Id,
                StringComparer.OrdinalIgnoreCase)
                ? ApprovalLevel.User
                : ApprovalLevel.None;
        if (context.DenyAllTools
            || context.DeniedToolIds.Contains(
                tool.Definition.Id,
                StringComparer.OrdinalIgnoreCase)
            || (context.AllowedToolIds.Count > 0
                && !context.AllowedToolIds.Contains(
                    tool.Definition.Id,
                    StringComparer.OrdinalIgnoreCase)))
        {
            var reason = context.DenyAllTools
                ? "The effective policy denies all tools."
                : "Repository configuration denies this tool.";
            return new ToolPolicyDecision(
                false,
                requiredApproval,
                reason);
        }

        var scratchpadAccess = IsEligibleScratchpadAccess(tool, input, context);
        if (!scratchpadAccess && ResourcesTargetScratchpad(tool, input, context))
        {
            return new ToolPolicyDecision(
                false,
                ApprovalLevel.None,
                "Only the built-in search, read_file, and write_file tools may access the active scratchpad.");
        }

        if (context.TrustLevel < tool.Definition.RequiredTrust && !scratchpadAccess)
        {
            var reason = $"{tool.Definition.Id} requires {tool.Definition.RequiredTrust}; "
                + $"current trust is {context.TrustLevel}.";
            return new ToolPolicyDecision(
                false,
                requiredApproval,
                reason);
        }

        foreach (var resourcePath in tool.GetResourcePaths(input, context))
        {
            try
            {
                // Only the compiled direct-write capability can use its separate folder grant.
                _ = ConfiguredTool.Unwrap(tool) is WriteFileTool writeFile
                    ? writeFile.ValidatePath(resourcePath, context)
                    : ToolPathRules.NormalizeAndValidateForTool(resourcePath, context, tool.Definition.Id);
            }
            catch (UnauthorizedAccessException exception)
            {
                return new ToolPolicyDecision(false, requiredApproval, exception.Message);
            }
        }

        if (tool.GetExecutable(input, context) is { } executable)
        {
            if (Path.IsPathFullyQualified(executable)
                || executable.Contains('/')
                || executable.Contains('\\'))
            {
                return new ToolPolicyDecision(
                    false,
                    requiredApproval,
                    "Executable values must be bare allow-listed names without a path.");
            }

            var basename = Path.GetFileNameWithoutExtension(executable);
            if (!context.AllowedExecutables.Contains(basename, StringComparer.OrdinalIgnoreCase))
            {
                return new ToolPolicyDecision(
                    false,
                    requiredApproval,
                    $"Executable '{basename}' is not allow-listed.");
            }
        }

        foreach (var secretReference in tool.GetSecretReferences(input))
        {
            if (!SecretReference.TryParse(secretReference, out _))
            {
                return new ToolPolicyDecision(
                    false,
                    requiredApproval,
                    "The tool declared an invalid secret reference.");
            }
        }

        var networkHosts = tool.GetNetworkHosts(input);
        if (networkHosts.Count > 0
            && context.AllowedNetworkToolIds is { } allowedNetworkToolIds
            && !allowedNetworkToolIds.Contains(tool.Definition.Id, StringComparer.OrdinalIgnoreCase))
        {
            return new ToolPolicyDecision(
                false,
                requiredApproval,
                "The tool is not allow-listed for network access in this execution context.");
        }

        foreach (var networkHost in networkHosts)
        {
            var configuredHost = context.AllowedNetworkHosts.Contains(
                networkHost,
                StringComparer.OrdinalIgnoreCase);
            var hostAuthorized = ConfiguredTool.Unwrap(tool) is IHostAuthorizedNetworkClaims scopedClaims
                && scopedClaims.IsNetworkHostAuthorized(input, context, networkHost);
            if (!configuredHost && !hostAuthorized)
            {
                return new ToolPolicyDecision(
                    false,
                    requiredApproval,
                    "The requested network host is not allow-listed.");
            }
        }

        return new ToolPolicyDecision(
            true,
            scratchpadAccess ? ApprovalLevel.None : requiredApproval,
            "Allowed by repository trust and tool policy.");
    }

    private static bool IsEligibleScratchpadAccess(ITool tool, object input, ToolInvocationContext context)
    {
        var implementation = ConfiguredTool.Unwrap(tool);
        if (implementation is not (ReadFileTool or SearchTextTool or WriteFileTool)
            || !context.Scratchpad.IsActive)
        {
            return false;
        }

        var path = implementation switch
        {
            ReadFileTool when input is ReadFileInput read => read.Path,
            SearchTextTool when input is SearchTextInput search => search.Path ?? ".",
            WriteFileTool when input is WriteFileInput write => write.Path,
            _ => null,
        };
        return path is not null && ToolPathRules.IsWithinScratchpad(path, context);
    }

    private static bool ResourcesTargetScratchpad(ITool tool, object input, ToolInvocationContext context)
    {
        return tool.GetResourcePaths(input, context).Any(path =>
        {
            try
            {
                return ToolPathRules.IsWithinScratchpad(path, context);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                return false;
            }
        });
    }
}

/// <summary>Approval policy that denies every user-approval request.</summary>
public sealed class DenyApprovalPolicy : IApprovalPolicy
{
    /// <inheritdoc />
    public Task<bool> IsApprovedAsync(
        string action,
        ApprovalLevel requiredLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(requiredLevel == ApprovalLevel.None);
    }
}

/// <summary>Approval policy for deterministic tests and explicitly automated hosts.</summary>
public sealed class AllowApprovalPolicy : IApprovalPolicy
{
    /// <inheritdoc />
    public Task<bool> IsApprovedAsync(
        string action,
        ApprovalLevel requiredLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

/// <summary>Shared repository-relative prohibited-path matching.</summary>
internal static class ToolPathRules
{
    /// <summary>Normalizes a path under the exact built-in tool's repository or scratchpad authority.</summary>
    internal static string NormalizeAndValidateForTool(
        string candidatePath,
        ToolInvocationContext context,
        string toolId,
        bool inspectFileSystem = true)
    {
        if ((toolId.Equals("search", StringComparison.Ordinal)
                || toolId.Equals("read_file", StringComparison.Ordinal)
                || toolId.Equals("write_file", StringComparison.Ordinal))
            && IsWithinScratchpad(candidatePath, context))
        {
            return NormalizeScratchpadPath(candidatePath, context, inspectFileSystem);
        }

        return NormalizeAndValidate(candidatePath, context, inspectFileSystem);
    }

    /// <summary>Returns whether a candidate is within the active scratchpad.</summary>
    internal static bool IsWithinScratchpad(string candidatePath, ToolInvocationContext context)
    {
        if (!context.Scratchpad.IsActive || context.Scratchpad.RootPath is not { } root)
        {
            return false;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, context.RepositoryPath));
        return IsSameOrChild(normalized, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
    }

    /// <summary>Normalizes and confines a candidate beneath the active scratchpad root.</summary>
    internal static string NormalizeScratchpadPath(
        string candidatePath,
        ToolInvocationContext context,
        bool inspectFileSystem = true)
    {
        if (!context.Scratchpad.IsActive || context.Scratchpad.RootPath is not { } root)
        {
            throw new UnauthorizedAccessException("The session scratchpad is unavailable.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, context.RepositoryPath));
        if (!IsSameOrChild(normalized, normalizedRoot))
        {
            throw new UnauthorizedAccessException("Tool path escapes the session scratchpad.");
        }

        if (inspectFileSystem)
        {
            RejectReparseTraversal(normalizedRoot, normalized);
        }

        return normalized;
    }

    /// <summary>Normalizes and confines a tool path using host filesystem semantics.</summary>
    internal static string NormalizeAndValidate(
        string candidatePath,
        ToolInvocationContext context,
        bool inspectFileSystem = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(context);
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, repositoryRoot));
        if (!IsSameOrChild(normalized, repositoryRoot))
        {
            throw new UnauthorizedAccessException("Tool path escapes the repository root.");
        }

        var approved = context.ApprovedRoots.Any(root =>
        {
            var approvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot));
            return IsSameOrChild(normalized, approvedRoot);
        });
        if (!approved)
        {
            throw new UnauthorizedAccessException("Tool path is outside the configured approved roots.");
        }

        var relative = Path.GetRelativePath(repositoryRoot, normalized).Replace('\\', '/');
        if (RepositoryPathPolicy.IsProhibited(relative, context.ProhibitedPaths))
        {
            throw new UnauthorizedAccessException("Tool path matches a prohibited repository pattern.");
        }

        if (!inspectFileSystem)
        {
            return normalized;
        }

        var current = repositoryRoot;
        foreach (var segment in Path.GetRelativePath(repositoryRoot, normalized)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Tool paths cannot traverse symbolic links or junctions.");
            }
        }

        return normalized;
    }

    private static void RejectReparseTraversal(string root, string normalized)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Tool paths cannot traverse symbolic links or junctions.");
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, normalized).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Tool paths cannot traverse symbolic links or junctions.");
            }
        }
    }

    /// <summary>Returns whether a normalized candidate equals or descends from a normalized root.</summary>
    internal static bool IsSameOrChild(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Returns whether a relative path contains a reserved Windows device-name segment.</summary>
    internal static bool ContainsReservedWindowsDeviceName(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsReservedWindowsDeviceName);
    }

    /// <summary>Returns whether a normalized relative path matches any configured glob.</summary>
    internal static bool IsProhibited(
        string relativePath,
        IReadOnlyList<string> prohibitedPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(prohibitedPatterns);
        return RepositoryPathPolicy.IsProhibited(relativePath, prohibitedPatterns);
    }

    private static bool IsReservedWindowsDeviceName(string segment)
    {
        var normalized = segment.TrimEnd(' ', '.');
        var extensionSeparator = normalized.IndexOf('.');
        var baseName = extensionSeparator >= 0 ? normalized[..extensionSeparator] : normalized;
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4
            && (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && baseName[3] is (>= '1' and <= '9') or '¹' or '²' or '³';
    }
}
