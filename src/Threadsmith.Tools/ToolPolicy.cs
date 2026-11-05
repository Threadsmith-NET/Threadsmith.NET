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
        if (context.SecurityPolicy is { } policy)
        {
            if (!context.IsChildAgent && !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath)),
                    policy.RepositoryPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return new ToolPolicyDecision(false, ApprovalLevel.None, "PolicyGenerationStale: the invocation belongs to another repository.");
            }

            try
            {
                context.PolicyBinding?.Validate(policy);
            }
            catch (UnauthorizedAccessException exception)
            {
                return new ToolPolicyDecision(false, ApprovalLevel.None, exception.Message);
            }

            if (!policy.AllowsTool(tool.Definition.Id))
            {
                return new ToolPolicyDecision(false, ApprovalLevel.None, "The effective security policy denies this tool.");
            }
        }

        if ((tool.Definition.Id == "repository_archeology" && (context.IsChildAgent || context.IntelligenceRun is not null))
            || !RepositoryIntelligenceToolPolicy.IsAllowed(tool.Definition, context)
            || (context.IntelligenceRun?.Operation == RepositoryIntelligenceOperation.Archeology
                && input is MemoriesInput { Action: not "list" }))
        {
            return new ToolPolicyDecision(false, ApprovalLevel.None, "Intelligence work permits repository/history reads and host-authorized intelligence memory operations only.");
        }

        var requiredApproval = (tool.Definition.RequiredApproval == ApprovalLevel.User
                && !(tool.Definition.Id == "run_process" && context.SecurityPolicy is not null))
            || (context.SecurityPolicy?.ReviewTools.Contains(tool.Definition.Id, StringComparer.OrdinalIgnoreCase) ?? false)
            || (tool.Definition.Id == "run_process" && context.SecurityPolicy?.RequireShellApproval == true)
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
        if (!scratchpadAccess && ConfiguredTool.Unwrap(tool) is not (RunProcessTool or CSharpScriptTool) && ResourcesTargetScratchpad(tool, input, context))
        {
            return new ToolPolicyDecision(
                false,
                ApprovalLevel.None,
                "Only the built-in search, read_file, and write_file tools may access the active scratchpad.");
        }

        if ((context.TrustLevel < tool.Definition.RequiredTrust
                || (context.SecurityPolicy is { } trustPolicy && trustPolicy.MaximumTrust < tool.Definition.RequiredTrust)) && !scratchpadAccess)
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
                                        : ConfiguredTool.Unwrap(tool) is ReadFileTool
                        ? ScriptFilePolicy.ValidateRead(resourcePath, context)
                        : ConfiguredTool.Unwrap(tool) is RunProcessTool && ToolPathRules.IsWithinScratchpad(resourcePath, context)
                            ? ToolPathRules.NormalizeScratchpadPath(resourcePath, context)
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
            if (!context.AllowedExecutables.Contains(basename, StringComparer.OrdinalIgnoreCase)
                || (context.SecurityPolicy is { } executablePolicy
                    && !executablePolicy.AllowedExecutables.Contains(basename, StringComparer.OrdinalIgnoreCase)))
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
            if (context.SecurityPolicy is { NetworkHostRestriction.IsDefault: false } networkPolicy
                && !networkPolicy.NetworkHostRestriction.Contains(networkHost, StringComparer.OrdinalIgnoreCase))
            {
                return new ToolPolicyDecision(false, requiredApproval, "The mandatory outbound policy denies this host; scoped consent cannot override it.");
            }

            var configuredHost = context.AllowedNetworkHosts.Contains(
                networkHost,
                StringComparer.OrdinalIgnoreCase)
                && (context.SecurityPolicy is not { } staticHostPolicy
                    || staticHostPolicy.AllowedNetworkHosts.Contains(networkHost, StringComparer.OrdinalIgnoreCase));
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
            requiredApproval,
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
    /// <summary>Checks whether the caller's roots or prohibited paths narrow the repository.</summary>
    internal static bool IsRepositoryScopeRestricted(ToolInvocationContext context, bool inspectFileSystem = true)
    {
        if (context.ProhibitedPaths.Count > 0)
        {
            return true;
        }

        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        return !context.ApprovedRoots.Any(root =>
            RepositoryPathPolicy.PathsEqual(repositoryRoot, Path.GetFullPath(root, repositoryRoot), inspectFileSystem ? null : StringComparer.Ordinal));
    }

    /// <summary>Normalizes a path under the exact built-in tool's repository or scratchpad authority.</summary>
    internal static string NormalizeAndValidateForTool(
        string candidatePath,
        ToolInvocationContext context,
        string toolId,
        bool inspectFileSystem = true,
        RepositoryPathCaseContext? caseContext = null)
    {
        caseContext ??= new RepositoryPathCaseContext(context.RepositoryPath);
        if ((toolId.Equals("search", StringComparison.Ordinal)
                || toolId.Equals("read_file", StringComparison.Ordinal)
                || toolId.Equals("write_file", StringComparison.Ordinal))
            && IsWithinScratchpad(candidatePath, context, inspectFileSystem, caseContext))
        {
            return NormalizeScratchpadPath(candidatePath, context, inspectFileSystem, caseContext);
        }

        return NormalizeAndValidate(candidatePath, context, inspectFileSystem, caseContext);
    }

    /// <summary>Returns whether a candidate is within the active scratchpad.</summary>
    internal static bool IsWithinScratchpad(
        string candidatePath,
        ToolInvocationContext context,
        bool inspectFileSystem = true,
        RepositoryPathCaseContext? caseContext = null)
    {
        if (!context.Scratchpad.IsActive || context.Scratchpad.RootPath is not { } root)
        {
            return false;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, context.RepositoryPath));
        return RepositoryPathPolicy.ContainsPath(root, normalized, inspectFileSystem ? null : StringComparer.Ordinal, caseContext);
    }

    /// <summary>Normalizes and confines a candidate beneath the active scratchpad root.</summary>
    internal static string NormalizeScratchpadPath(
        string candidatePath,
        ToolInvocationContext context,
        bool inspectFileSystem = true,
        RepositoryPathCaseContext? caseContext = null)
    {
        if (!context.Scratchpad.IsActive || context.Scratchpad.RootPath is not { } root)
        {
            throw new UnauthorizedAccessException("The session scratchpad is unavailable.");
        }

        caseContext ??= new RepositoryPathCaseContext(context.RepositoryPath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, context.RepositoryPath));
        if (!RepositoryPathPolicy.ContainsPath(normalizedRoot, normalized, inspectFileSystem ? null : StringComparer.Ordinal, caseContext))
        {
            throw new UnauthorizedAccessException("Tool path escapes the session scratchpad.");
        }

        if (inspectFileSystem)
        {
            var authorityRoot = RepositoryPathPolicy.ContainsPath(context.RepositoryPath, normalizedRoot, null, caseContext)
                ? context.RepositoryPath : normalizedRoot;
            ScopedFilePaths.RejectLinks(normalized, authorityRoot, allowMissing: true, caseContext);
        }

        return normalized;
    }

    /// <summary>Normalizes and confines a tool path using host filesystem semantics.</summary>
    internal static string NormalizeAndValidate(
        string candidatePath,
        ToolInvocationContext context,
        bool inspectFileSystem = true,
        RepositoryPathCaseContext? caseContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(context);
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        caseContext ??= new RepositoryPathCaseContext(repositoryRoot);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, repositoryRoot));
        if (!RepositoryPathPolicy.TryNormalizeContainedPath(
                repositoryRoot, normalized, out var containedPath, inspectFileSystem ? null : StringComparer.Ordinal, caseContext))
        {
            throw new UnauthorizedAccessException("Tool path escapes the repository root.");
        }

        normalized = containedPath;
        var approved = context.ApprovedRoots.Any(root =>
        {
            var approvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot));
            return RepositoryPathPolicy.ContainsPath(approvedRoot, normalized, inspectFileSystem ? null : StringComparer.Ordinal, caseContext);
        });
        if (!approved)
        {
            throw new UnauthorizedAccessException("Tool path is outside the configured approved roots.");
        }

        var relative = Path.GetRelativePath(repositoryRoot, normalized).Replace('\\', '/');
        if (RepositoryPathPolicy.IsProhibited(relative, context.ProhibitedPaths, repositoryRoot, caseContext, inspectFileSystem))
        {
            throw new UnauthorizedAccessException("Tool path matches a prohibited repository pattern.");
        }

        if (!inspectFileSystem)
        {
            return normalized;
        }

        ScopedFilePaths.RejectLinks(normalized, repositoryRoot, allowMissing: true, caseContext);
        return normalized;
    }

    /// <summary>Returns whether a relative path contains a reserved Windows device-name segment.</summary>
    internal static bool ContainsReservedWindowsDeviceName(string relativePath) => ScopedFilePaths.ContainsReservedWindowsDeviceName(relativePath);

    /// <summary>Returns whether a normalized relative path matches any configured glob.</summary>
    internal static bool IsProhibited(
        string relativePath,
        IReadOnlyList<string> prohibitedPatterns,
        string repositoryPath,
        RepositoryPathCaseContext? caseContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(prohibitedPatterns);
        return RepositoryPathPolicy.IsProhibited(relativePath, prohibitedPatterns, repositoryPath, caseContext);
    }
}
