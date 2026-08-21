namespace Threadsmith.Tools;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Persists repository-bound user approval for exact MCP tool schema identities outside repository control.</summary>
internal sealed class McpToolApprovalStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumApprovals = 4096;
    private const int MaximumFileBytes = 1024 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _path;
    private HashSet<string> _approvals;

    /// <summary>Initializes a new instance of the <see cref="McpToolApprovalStore"/> class.</summary>
    internal McpToolApprovalStore(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        try
        {
            _approvals = Load(_path);
        }
        catch (Exception exception) when (IsRecoverableLoadFailure(exception))
        {
            _approvals = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Checks exact repository, profile-qualified capability, and schema approval.</summary>
    internal bool HasApproval(string repositoryRoot, string preferenceId)
    {
        return _approvals.Contains(CreateApprovalKey(repositoryRoot, preferenceId));
    }

    /// <summary>Grants exact approval after an explicit host-owned user action.</summary>
    internal async Task GrantAsync(
        string repositoryRoot,
        string preferenceId,
        CancellationToken cancellationToken)
    {
        var path = _path
            ?? throw new InvalidOperationException("MCP tool approval requires a user-owned approval store.");
        var approvalKey = CreateApprovalKey(repositoryRoot, preferenceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var approvals = new HashSet<string>(_approvals, StringComparer.Ordinal);
            approvals.RemoveWhere(value => HasSameToolIdentity(value, approvalKey));
            approvals.Add(approvalKey);
            await PersistAsync(path, approvals, cancellationToken);
            _approvals = approvals;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Revokes every schema variant for the tool in the current repository.</summary>
    internal async Task RevokeAsync(
        string repositoryRoot,
        string preferenceId,
        CancellationToken cancellationToken)
    {
        if (_path is not { } path)
        {
            return;
        }

        var approvalKey = CreateApprovalKey(repositoryRoot, preferenceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var approvals = new HashSet<string>(_approvals, StringComparer.Ordinal);
            approvals.RemoveWhere(value => HasSameToolIdentity(value, approvalKey));
            await PersistAsync(path, approvals, cancellationToken);
            _approvals = approvals;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CreateApprovalKey(string repositoryRoot, string preferenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferenceId);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (OperatingSystem.IsWindows())
        {
            normalizedRoot = normalizedRoot.ToUpperInvariant();
        }

        var repositoryDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
        return $"{repositoryDigest}:{preferenceId}";
    }

    private static bool HasSameToolIdentity(string existing, string requested)
    {
        var requestedVersion = requested.IndexOf("@mcp-", StringComparison.OrdinalIgnoreCase);
        var requestedIdentity = requestedVersion < 0 ? requested : requested[..requestedVersion];
        var existingVersion = existing.IndexOf("@mcp-", StringComparison.OrdinalIgnoreCase);
        var existingIdentity = existingVersion < 0 ? existing : existing[..existingVersion];
        return string.Equals(existingIdentity, requestedIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableLoadFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.Text.Json.JsonException;
    }

    private static HashSet<string> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var file = new FileInfo(path);
        bool permissionsSafe;
        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = file.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            permissionsSafe = HasSafeWindowsPermissions(security);
        }
        else
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            const UnixFileMode unsafeModes = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                | UnixFileMode.OtherExecute;
            permissionsSafe = (mode & unsafeModes) == 0;
        }

        if (!permissionsSafe)
        {
            throw new InvalidOperationException("The MCP tool approval store permissions are unsafe.");
        }

        if (file.Length > MaximumFileBytes)
        {
            throw new InvalidOperationException("The MCP tool approval store exceeds its host-owned size bound.");
        }

        JsonObject root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException("The MCP tool approval store must contain a JSON object.");
        if (root["schemaVersion"]?.GetValue<int>() != CurrentSchemaVersion
            || root["approvals"] is not JsonArray approvalNodes
            || approvalNodes.Count > MaximumApprovals)
        {
            throw new InvalidOperationException("The MCP tool approval store has an unsupported or oversized schema.");
        }

        var approvals = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in approvalNodes)
        {
            var approval = node?.GetValue<string>()
                ?? throw new InvalidOperationException("The MCP tool approval store contains an invalid entry.");
            if (approval.Length is 0 or > 1024 || !approvals.Add(approval))
            {
                throw new InvalidOperationException("The MCP tool approval store contains an invalid or duplicate entry.");
            }
        }

        return approvals;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasSafeWindowsPermissions(FileSecurity security)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? currentUser = identity.User;
        if (currentUser is null
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(currentUser))
        {
            return false;
        }

        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            offset: 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            return false;
        }

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        return rules
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow
                && rule.FileSystemRights != 0)
            .All(rule => rule.IdentityReference is SecurityIdentifier principal
                && IsSafeWindowsPrincipal(principal, currentUser));
    }

    [SupportedOSPlatform("windows")]
    private static bool IsSafeWindowsPrincipal(
        SecurityIdentifier principal,
        SecurityIdentifier currentUser)
    {
        return principal.Equals(currentUser)
                || principal.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || principal.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                || string.Equals(principal.Value, "S-1-3-4", StringComparison.Ordinal);
    }

    private static async Task PersistAsync(
        string path,
        IReadOnlyCollection<string> approvals,
        CancellationToken cancellationToken)
    {
        if (approvals.Count > MaximumApprovals)
        {
            throw new InvalidOperationException("The MCP tool approval store exceeds its host-owned entry bound.");
        }

        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            path,
            async token =>
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException("The MCP tool approval store has no parent directory.");
                }

                Directory.CreateDirectory(directory);
                var root = new JsonObject
                {
                    ["schemaVersion"] = CurrentSchemaVersion,
                    ["approvals"] = new JsonArray([.. approvals
                        .Order(StringComparer.Ordinal)
                        .Select(value => (JsonNode?)JsonValue.Create(value))]),
                };
                byte[] content = Encoding.UTF8.GetBytes(root.ToJsonString() + Environment.NewLine);
                var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (FileStream stream = CreatePrivateFile(temporaryPath))
                    {
                        await stream.WriteAsync(content, token);
                        await stream.FlushAsync(token);
                    }

                    File.Move(temporaryPath, path, overwrite: true);
                    if (OperatingSystem.IsWindows())
                    {
                        FileSecurity security = new FileInfo(path).GetAccessControl(
                            AccessControlSections.Access | AccessControlSections.Owner);
                        if (!HasSafeWindowsPermissions(security))
                        {
                            throw new UnauthorizedAccessException(
                                "The MCP tool approval store permissions are unsafe.");
                        }
                    }
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            },
            cancellationToken);
    }

    private static FileStream CreatePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).Create(
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous,
                CreatePrivateWindowsSecurity());
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreatePrivateWindowsSecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier currentUser = identity.User
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
