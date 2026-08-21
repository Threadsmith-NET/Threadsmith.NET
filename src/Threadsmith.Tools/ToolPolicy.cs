namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>A policy decision made before tool execution.</summary>
public sealed record ToolPolicyDecision(
    bool IsAllowed,
    ApprovalLevel RequiredApproval,
    string Reason);

/// <summary>Evaluates repository, path, command, network, and secret policy.</summary>
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

        if (context.TrustLevel < tool.Definition.RequiredTrust)
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
                _ = ToolPathRules.NormalizeAndValidate(resourcePath, context);
            }
            catch (UnauthorizedAccessException exception)
            {
                return new ToolPolicyDecision(false, requiredApproval, exception.Message);
            }
        }

        if (tool.GetExecutable(input) is { } executable)
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
            if (!secretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
                || !context.AllowedSecretReferences.Contains(
                    secretReference,
                    StringComparer.OrdinalIgnoreCase))
            {
                return new ToolPolicyDecision(
                    false,
                    requiredApproval,
                    "The requested secret scope is not permitted.");
            }
        }

        foreach (var networkHost in tool.GetNetworkHosts(input))
        {
            var configuredHost = context.AllowedNetworkHosts.Contains(
                networkHost,
                StringComparer.OrdinalIgnoreCase);
            var hostAuthorized = tool is IHostAuthorizedNetworkClaims scopedClaims
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
    /// <summary>Normalizes and confines a tool path using host filesystem semantics.</summary>
    internal static string NormalizeAndValidate(
        string candidatePath,
        ToolInvocationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentNullException.ThrowIfNull(context);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath, repositoryRoot));
        if (!normalized.Equals(repositoryRoot, comparison)
            && !normalized.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new UnauthorizedAccessException("Tool path escapes the repository root.");
        }

        var approved = context.ApprovedRoots.Any(root =>
        {
            var approvedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot));
            return normalized.Equals(approvedRoot, comparison)
                || normalized.StartsWith(approvedRoot + Path.DirectorySeparatorChar, comparison);
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
