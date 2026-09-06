namespace Threadsmith.Tools;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

/// <summary>Classifies the authority of a secret source.</summary>
public enum SecretProviderTrust
{
    /// <summary>The value is controlled by the active repository.</summary>
    RepositoryOwned = 0,

    /// <summary>The value is controlled by the current operating-system user or process.</summary>
    UserOwned = 1,

    /// <summary>The value is controlled by trusted machine or organization policy.</summary>
    Managed = 2,
}

/// <summary>Classifies the storage mechanism used by a secret provider.</summary>
public enum SecretProviderSourceKind
{
    /// <summary>A process environment variable.</summary>
    Environment,

    /// <summary>The active repository's confined secret store.</summary>
    RepositoryFile,

    /// <summary>The current user's secret store.</summary>
    UserFile,

    /// <summary>A future host-registered external provider.</summary>
    External,
}

/// <summary>Stable failure classifications safe for user-facing diagnostics.</summary>
public enum SecretResolutionFailure
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>The logical reference is malformed.</summary>
    MalformedReference,

    /// <summary>No registered provider is eligible for the request.</summary>
    NoEligibleProvider,

    /// <summary>Eligible providers did not contain the reference.</summary>
    NotFound,

    /// <summary>A provider is unavailable.</summary>
    ProviderUnavailable,

    /// <summary>Host trust or provider policy rejected the source.</summary>
    PolicyRejected,

    /// <summary>A store path or permission boundary is unsafe.</summary>
    UnsafeStore,

    /// <summary>A store is malformed or exceeds its bounds.</summary>
    MalformedStore,

    /// <summary>A repository store is tracked, staged, or not effectively ignored.</summary>
    RepositoryGitProofFailed,

    /// <summary>Resolution would recursively bootstrap the same provider/reference pair.</summary>
    BootstrapCycle,

    /// <summary>A provider failed without exposing its underlying error.</summary>
    ProviderFailed,
}

/// <summary>A validated canonical logical secret name.</summary>
public sealed record SecretReference
{
    private const int MaximumLength = 256;

    private SecretReference(string canonicalName)
    {
        CanonicalName = canonicalName;
    }

    /// <summary>Gets the canonical logical name.</summary>
    public string CanonicalName { get; }

    /// <summary>Parses and validates a <c>secrets:</c> logical reference.</summary>
    public static SecretReference Parse(string value)
    {
        if (!TryParse(value, out var reference))
        {
            throw new ArgumentException("Secret references must be bounded canonical secrets: names.", nameof(value));
        }

        return reference ?? throw new InvalidOperationException("Validated secret reference was unexpectedly absent.");
    }

    /// <summary>Attempts to parse a logical secret reference.</summary>
    public static bool TryParse(string? value, out SecretReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumLength
            || !value.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
            || value.Any(char.IsControl)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split(':');
        if (segments.Length < 2
            || segments.Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))))
        {
            return false;
        }

        reference = new SecretReference("secrets:" + string.Join(':', segments.Skip(1)));
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return CanonicalName;
    }
}

/// <summary>One privileged final-boundary secret resolution request.</summary>
public sealed record SecretResolutionRequest
{
    /// <summary>Gets the maximum time allowed for one provider attempt.</summary>
    public static TimeSpan DefaultProviderTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the validated logical reference.</summary>
    public required SecretReference Reference { get; init; }

    /// <summary>Gets the stable requesting component identifier.</summary>
    public required string ComponentId { get; init; }

    /// <summary>Gets the non-secret reason for requesting the value.</summary>
    public required string Purpose { get; init; }

    /// <summary>Gets the active repository root when repository lookup is eligible.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Gets the minimum source authority accepted by the component.</summary>
    public SecretProviderTrust MinimumTrust { get; init; } = SecretProviderTrust.UserOwned;

    /// <summary>Gets an optional host-owned provider allowlist.</summary>
    public IReadOnlySet<string>? AllowedProviderIds { get; init; }

    /// <summary>Gets the bounded timeout for each provider attempt.</summary>
    public TimeSpan ProviderTimeout { get; init; } = DefaultProviderTimeout;

    /// <summary>Creates a diagnostic-safe stable component ID from an accepted configured identifier.</summary>
    /// <param name="category">A bounded host-owned diagnostic category.</param>
    /// <param name="configuredIdentifier">The configured identifier whose existing syntax remains authoritative.</param>
    /// <returns>A safe component ID that does not disclose the configured identifier.</returns>
    public static string CreateConfiguredComponentId(string category, string configuredIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredIdentifier);
        if (category.Length > 31
            || category.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.')))
        {
            throw new ArgumentException("Component categories must be bounded diagnostic tokens.", nameof(category));
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuredIdentifier)));
        return category + ":" + digest;
    }
}

/// <summary>A narrow resolved value whose string representation never discloses the secret.</summary>
public sealed class SecretValue
{
    private readonly string _value;

    /// <summary>Initializes a new instance of the <see cref="SecretValue"/> class.</summary>
    public SecretValue(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (value.Length > 65_536)
        {
            throw new ArgumentException("Secret values cannot exceed 65,536 characters.", nameof(value));
        }

        _value = value;
    }

    /// <summary>Returns the value for immediate use at the authorized transport boundary.</summary>
    public string Reveal()
    {
        return _value;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "[secret]";
    }
}

/// <summary>One provider's closed resolution outcome.</summary>
public sealed record SecretProviderResult
{
    /// <summary>Gets the resolved value, when found.</summary>
    public SecretValue? Value { get; init; }

    /// <summary>Gets a stable sanitized failure classification.</summary>
    public SecretResolutionFailure Failure { get; init; }

    /// <summary>Gets a bounded non-secret diagnostic code.</summary>
    public required string DiagnosticCode { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static SecretProviderResult Found(string value)
    {
        return new()
        {
            Value = new SecretValue(value),
            Failure = SecretResolutionFailure.None,
            DiagnosticCode = "found",
        };
    }

    /// <summary>Creates a safe unsuccessful result.</summary>
    public static SecretProviderResult NotFound(string code = "not-found")
    {
        return new()
        {
            Failure = SecretResolutionFailure.NotFound,
            DiagnosticCode = code,
        };
    }
}

/// <summary>Aggregate provider-neutral resolution result.</summary>
public sealed record SecretResolutionResult
{
    /// <summary>Gets the resolved value, when successful.</summary>
    public SecretValue? Value { get; init; }

    /// <summary>Gets the provider that supplied the value.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Gets the aggregate failure classification.</summary>
    public SecretResolutionFailure Failure { get; init; }

    /// <summary>Gets bounded provider attempt diagnostics without values.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Gets whether resolution succeeded.</summary>
    public bool Succeeded => Value is not null;

    /// <summary>Returns the resolved value or throws one sanitized actionable exception.</summary>
    public string RequireValue(SecretResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Value?.Reveal() ?? throw new SecretResolutionException(request, this);
    }
}

/// <summary>A sanitized resolution failure suitable for ordinary consumer boundaries.</summary>
public sealed class SecretResolutionException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SecretResolutionException"/> class.</summary>
    public SecretResolutionException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretResolutionException"/> class.</summary>
    public SecretResolutionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretResolutionException"/> class.</summary>
    public SecretResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretResolutionException"/> class.</summary>
    public SecretResolutionException(SecretResolutionRequest request, SecretResolutionResult result)
        : base(CreateMessage(request, result))
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        Failure = result.Failure;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public SecretResolutionFailure Failure { get; }

    private static string CreateMessage(SecretResolutionRequest request, SecretResolutionResult result)
    {
        return $"Secret '{request.Reference}' for component '{request.ComponentId}' could not be resolved "
                + $"({result.Failure}; {string.Join(", ", result.Diagnostics)}). "
                + "Set it in ~/.threadsmith/secrets/config.json, an eligible ignored repository store, "
                + "or the matching THREADSMITH_secrets__ environment variable.";
    }
}

/// <summary>A host-registered source of static secret values.</summary>
public interface ISecretProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string Id { get; }

    /// <summary>Gets the fixed host-owned precedence; higher values are attempted first.</summary>
    int Priority { get; }

    /// <summary>Gets the authority of values returned by this provider.</summary>
    SecretProviderTrust Trust { get; }

    /// <summary>Gets the source mechanism.</summary>
    SecretProviderSourceKind SourceKind { get; }

    /// <summary>Gets the canonical reference prefixes this provider can resolve.</summary>
    IReadOnlyList<string> SupportedPrefixes { get; }

    /// <summary>Attempts to resolve one exact reference.</summary>
    Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>The only ordinary component boundary for static secret discovery.</summary>
public interface ISecretResolver
{
    /// <summary>Resolves one validated request through eligible host-owned providers.</summary>
    Task<SecretResolutionResult> ResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministic host-owned aggregate secret resolver.</summary>
public sealed class SecretResolver : ISecretResolver
{
    private static readonly AsyncLocal<HashSet<string>?> ActiveRequests = new();
    private readonly IReadOnlyList<ISecretProvider> _providers;

    /// <summary>Initializes a new instance of the <see cref="SecretResolver"/> class.</summary>
    public SecretResolver(IEnumerable<ISecretProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ISecretProvider[] ordered = [.. providers.OrderByDescending(provider => provider.Priority).ThenBy(provider => provider.Id, StringComparer.Ordinal)];
        if (ordered.Length == 0
            || ordered.Any(provider => !IsSafeDiagnosticToken(provider.Id))
            || ordered.Select(provider => provider.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length
            || ordered.Select(provider => provider.Priority).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Secret providers require unique stable IDs and priorities.", nameof(providers));
        }

        _providers = ordered;
    }

    /// <inheritdoc />
    public async Task<SecretResolutionResult> ResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ComponentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        if (!IsSafeDiagnosticToken(request.ComponentId)
            || request.Purpose.Length > 256
            || request.Purpose.Any(char.IsControl)
            || request.ProviderTimeout <= TimeSpan.Zero
            || request.ProviderTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentException("Secret requests require bounded safe component, purpose, and timeout values.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var cycleKey = request.ComponentId + "\n" + request.Reference.CanonicalName;
        var active = ActiveRequests.Value ??= new(StringComparer.OrdinalIgnoreCase);
        if (!active.Add(cycleKey))
        {
            return new SecretResolutionResult
            {
                Failure = SecretResolutionFailure.BootstrapCycle,
                Diagnostics = ["bootstrap-cycle"],
            };
        }

        try
        {
            var diagnostics = new List<string>();
            var eligible = false;
            var aggregateFailure = SecretResolutionFailure.NotFound;
            foreach (var provider in _providers)
            {
                if (provider.Trust < request.MinimumTrust
                    || (request.AllowedProviderIds is not null && !request.AllowedProviderIds.Contains(provider.Id))
                    || !provider.SupportedPrefixes.Any(prefix => request.Reference.CanonicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    diagnostics.Add(provider.Id + ":policy-skipped");
                    continue;
                }

                eligible = true;
                SecretProviderResult result;
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCancellation.CancelAfter(request.ProviderTimeout);
                try
                {
                    result = await provider.TryResolveAsync(request, attemptCancellation.Token)
                        .WaitAsync(attemptCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
                {
                    result = new SecretProviderResult
                    {
                        Failure = SecretResolutionFailure.ProviderUnavailable,
                        DiagnosticCode = "provider-timeout",
                    };
                }
                catch (Exception)
                {
                    result = new SecretProviderResult
                    {
                        Failure = SecretResolutionFailure.ProviderFailed,
                        DiagnosticCode = "provider-failed",
                    };
                }

                var diagnosticCode = IsSafeDiagnosticToken(result.DiagnosticCode)
                    ? result.DiagnosticCode
                    : "invalid-provider-diagnostic";
                diagnostics.Add(provider.Id + ":" + diagnosticCode);
                if (result.Value is not null)
                {
                    return new SecretResolutionResult
                    {
                        Value = result.Value,
                        ProviderId = provider.Id,
                        Failure = SecretResolutionFailure.None,
                        Diagnostics = diagnostics,
                    };
                }

                if (result.Failure != SecretResolutionFailure.NotFound)
                {
                    aggregateFailure = result.Failure;
                }
            }

            return new SecretResolutionResult
            {
                Failure = eligible ? aggregateFailure : SecretResolutionFailure.NoEligibleProvider,
                Diagnostics = diagnostics,
            };
        }
        finally
        {
            active.Remove(cycleKey);
            if (active.Count == 0)
            {
                ActiveRequests.Value = null;
            }
        }
    }

    private static bool IsSafeDiagnosticToken(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 96
                && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':' or '.');
    }
}

/// <summary>Resolves the exact compatible <c>THREADSMITH_</c> environment variable.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    /// <inheritdoc />
    public string Id => "environment";

    /// <inheritdoc />
    public int Priority => 300;

    /// <inheritdoc />
    public SecretProviderTrust Trust => SecretProviderTrust.UserOwned;

    /// <inheritdoc />
    public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.Environment;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    /// <inheritdoc />
    public Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var variable = "THREADSMITH_" + request.Reference.CanonicalName.Replace(":", "__", StringComparison.Ordinal);
        var value = Environment.GetEnvironmentVariable(variable);
        return Task.FromResult(string.IsNullOrEmpty(value)
            ? SecretProviderResult.NotFound(value is null ? "not-found" : "empty-rejected")
            : SecretProviderResult.Found(value));
    }
}

/// <summary>Reads a bounded strict JSON secret store outside repository control.</summary>
public sealed class UserFileSecretProvider : JsonFileSecretProvider
{
    /// <summary>Initializes a new instance of the <see cref="UserFileSecretProvider"/> class.</summary>
    public UserFileSecretProvider(string path)
        : base(path)
    {
    }

    /// <inheritdoc />
    public override string Id => "user-file";

    /// <inheritdoc />
    public override int Priority => 100;

    /// <inheritdoc />
    public override SecretProviderTrust Trust => SecretProviderTrust.UserOwned;

    /// <inheritdoc />
    public override SecretProviderSourceKind SourceKind => SecretProviderSourceKind.UserFile;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    /// <inheritdoc />
    protected override Task<SecretProviderResult> ValidateStoreAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(StorePath))
        {
            bool safePermissions;
            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(StorePath).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
                safePermissions = HasSafeWindowsPermissions(security);
            }
            else
            {
                var mode = File.GetUnixFileMode(StorePath);
                const UnixFileMode unsafeModes = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                safePermissions = (mode & unsafeModes) == 0;
            }

            if (!safePermissions)
            {
                return Task.FromResult(Failure(SecretResolutionFailure.UnsafeStore, "unsafe-permissions"));
            }
        }

        return Task.FromResult(SecretProviderResult.NotFound("store-safe"));
    }

    /// <inheritdoc />
    protected override void ValidateOpenedStore(FileStream stream)
    {
        if (OperatingSystem.IsWindows() && !HasSafeWindowsPermissions(stream.GetAccessControl()))
        {
            throw new UnauthorizedAccessException("Secret store permissions are unsafe.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasSafeWindowsPermissions(FileSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User;
        if (currentUser is null
            || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(currentUser))
        {
            return false;
        }

        var descriptorBytes = security.GetSecurityDescriptorBinaryForm();
        var descriptor = new RawSecurityDescriptor(descriptorBytes, offset: 0);
        if (descriptor.DiscretionaryAcl is null)
        {
            return false;
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (var rule in rules.OfType<FileSystemAccessRule>())
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier principal
                || !IsSafeWindowsPrincipal(principal, currentUser))
            {
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsSafeWindowsPrincipal(SecurityIdentifier principal, SecurityIdentifier currentUser)
    {
        return principal.Equals(currentUser)
                || principal.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || principal.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || principal.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                || string.Equals(principal.Value, "S-1-3-4", StringComparison.Ordinal);
    }
}

/// <summary>Rebinds repository secret lookup atomically when the active repository changes.</summary>
public sealed class RepositorySecretProvider : ISecretProvider
{
    private readonly Lock _gate = new();
    private RepositoryFileSecretProvider _current;

    /// <summary>Initializes a new instance of the <see cref="RepositorySecretProvider"/> class.</summary>
    public RepositorySecretProvider(string repositoryRoot)
    {
        _current = new RepositoryFileSecretProvider(repositoryRoot);
    }

    /// <inheritdoc />
    public string Id => "repository-file";

    /// <inheritdoc />
    public int Priority => 200;

    /// <inheritdoc />
    public SecretProviderTrust Trust => SecretProviderTrust.RepositoryOwned;

    /// <inheritdoc />
    public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.RepositoryFile;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    /// <summary>Rebinds future lookups to the exact active repository.</summary>
    public void BindRepository(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var next = new RepositoryFileSecretProvider(repositoryRoot);
        lock (_gate)
        {
            _current = next;
        }
    }

    /// <inheritdoc />
    public Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        RepositoryFileSecretProvider provider;
        lock (_gate)
        {
            provider = _current;
        }

        return provider.TryResolveAsync(
            request with { RepositoryRoot = provider.RepositoryRoot },
            cancellationToken);
    }
}

/// <summary>Reads a repository store only after confinement and Git index/ignore proof.</summary>
public sealed class RepositoryFileSecretProvider : JsonFileSecretProvider
{
    /// <summary>Initializes a new instance of the <see cref="RepositoryFileSecretProvider"/> class.</summary>
    public RepositoryFileSecretProvider(string repositoryRoot)
        : base(Path.Combine(Path.GetFullPath(repositoryRoot), ".threadsmith", "secrets", "config.json"))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        RepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
    }

    /// <summary>Gets the confined repository root.</summary>
    public string RepositoryRoot { get; }

    /// <inheritdoc />
    public override string Id => "repository-file";

    /// <inheritdoc />
    public override int Priority => 200;

    /// <inheritdoc />
    public override SecretProviderTrust Trust => SecretProviderTrust.RepositoryOwned;

    /// <inheritdoc />
    public override SecretProviderSourceKind SourceKind => SecretProviderSourceKind.RepositoryFile;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    /// <inheritdoc />
    protected override async Task<SecretProviderResult> ValidateStoreAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken)
    {
        if ((request.RepositoryRoot is not null
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryRoot)),
                    RepositoryRoot,
                    PathComparison))
            || !IsConfinedPath(RepositoryRoot, StorePath)
            || HasReparsePoint(RepositoryRoot, StorePath))
        {
            return Failure(SecretResolutionFailure.UnsafeStore, "repository-path-rejected");
        }

        using var validationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        validationCancellation.CancelAfter(request.ProviderTimeout);
        var relative = Path.GetRelativePath(RepositoryRoot, StorePath).Replace('\\', '/');
        foreach (var indexPath in GetApplicableIndexPaths(relative))
        {
            var indexed = await RunGitAsync(
                [
                    "ls-files",
                    "--stage",
                    "--",
                    ":(literal)" + indexPath,
                    ":(exclude,glob)" + indexPath + "/**",
                ],
                validationCancellation.Token).ConfigureAwait(false);
            if (indexed.ExitCode != 0)
            {
                return Failure(SecretResolutionFailure.RepositoryGitProofFailed, "index-state-indeterminate");
            }

            if (!string.IsNullOrWhiteSpace(indexed.StandardOutput))
            {
                return Failure(SecretResolutionFailure.RepositoryGitProofFailed, "tracked-or-staged");
            }
        }

        var ignored = await RunGitAsync(
            ["check-ignore", "--no-index", "-q", "--", relative],
            validationCancellation.Token).ConfigureAwait(false);
        if (ignored.ExitCode != 0)
        {
            return Failure(
                SecretResolutionFailure.RepositoryGitProofFailed,
                ignored.ExitCode == 1 ? "not-effectively-ignored" : "ignore-state-indeterminate");
        }

        return SecretProviderResult.NotFound("store-safe");
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IEnumerable<string> GetApplicableIndexPaths(string relative)
    {
        var current = relative;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            var separator = current.LastIndexOf('/');
            current = separator > 0 ? current[..separator] : null;
        }
    }

    private async Task<ProcessResult> RunGitAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.quotepath=false");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(RepositoryRoot);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await standardError.ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, await standardOutput.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Cancellation remains authoritative if the process exits concurrently or tree kill is unavailable.
        }
    }

    private static bool IsConfinedPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.GetDirectoryName(path);
        while (current is not null && IsConfinedPath(root, current))
        {
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), root, PathComparison))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput);
}

/// <summary>Shared strict, bounded, read-only JSON provider implementation.</summary>
public abstract class JsonFileSecretProvider : ISecretProvider
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumProperties = 512;

    /// <summary>Initializes a new instance of the <see cref="JsonFileSecretProvider"/> class.</summary>
    protected JsonFileSecretProvider(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        StorePath = Path.GetFullPath(storePath);
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract int Priority { get; }

    /// <inheritdoc />
    public abstract SecretProviderTrust Trust { get; }

    /// <inheritdoc />
    public abstract SecretProviderSourceKind SourceKind { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> SupportedPrefixes { get; }

    /// <inheritdoc />
    public async Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(StorePath))
        {
            return SecretProviderResult.NotFound();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var validation = await ValidateStoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (validation.Failure != SecretResolutionFailure.NotFound || validation.DiagnosticCode != "store-safe")
        {
            return validation;
        }

        try
        {
            var bytes = await ReadBoundedFileWithoutFollowingLinksAsync(
                StorePath,
                cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return Failure(SecretResolutionFailure.MalformedStore, "store-too-large");
            }

            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var propertyCount = 0;
            if (!ValidateElement(document.RootElement, ref propertyCount))
            {
                return Failure(SecretResolutionFailure.MalformedStore, "invalid-duplicate-or-excessive-content");
            }

            var path = request.Reference.CanonicalName.Split(':');
            var current = document.RootElement;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return SecretProviderResult.NotFound();
                }

                JsonProperty[] properties = [.. current.EnumerateObject()];

                JsonProperty? match = null;
                foreach (var property in properties)
                {
                    if (string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        match = property;
                        break;
                    }
                }

                if (match is null)
                {
                    return SecretProviderResult.NotFound();
                }

                current = match.Value.Value;
            }

            if (current.ValueKind != JsonValueKind.String)
            {
                return Failure(SecretResolutionFailure.MalformedStore, "secret-value-not-string");
            }

            var value = current.GetString();
            return string.IsNullOrEmpty(value)
                ? SecretProviderResult.NotFound("empty-rejected")
                : SecretProviderResult.Found(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return Failure(
                exception is UnauthorizedAccessException ? SecretResolutionFailure.UnsafeStore : SecretResolutionFailure.MalformedStore,
                exception is UnauthorizedAccessException ? "access-denied" : "malformed-store");
        }
    }

    /// <summary>Gets the canonical store path.</summary>
    protected string StorePath { get; }

    /// <summary>Validates source-specific trust and filesystem preconditions before any value read.</summary>
    protected abstract Task<SecretProviderResult> ValidateStoreAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Validates source-specific trust from the exact opened store handle.</summary>
    protected virtual void ValidateOpenedStore(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
    }

    /// <summary>Creates a closed safe provider failure.</summary>
    protected static SecretProviderResult Failure(SecretResolutionFailure failure, string code)
    {
        return new()
        {
            Failure = failure,
            DiagnosticCode = code,
        };
    }

    private async Task<byte[]?> ReadBoundedFileWithoutFollowingLinksAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenFileWithoutFollowingLinks(path);
        if ((File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0
            || !string.Equals(
                Path.GetFullPath(GetOpenedPath(stream.SafeFileHandle)),
                Path.GetFullPath(path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Secret store path could not be safely opened.");
        }

        ValidateOpenedStore(stream);
        if (stream.Length > MaximumBytes)
        {
            return null;
        }

        var buffer = new byte[MaximumBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaximumBytes ? null : buffer[..total];
    }

    private static FileStream OpenFileWithoutFollowingLinks(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileWindows(
                path,
                GenericRead,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                0,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagOverlapped | FileFlagSequentialScan,
                0);
        }
        else
        {
            var flags = UnixOpenReadOnly
                | (OperatingSystem.IsMacOS()
                    ? MacOpenNoFollow | MacOpenCloseOnExec | MacOpenNonBlocking
                    : LinuxOpenNoFollow | LinuxOpenCloseOnExec | LinuxOpenNonBlocking);
            handle = new SafeFileHandle(OpenFileUnix(path, flags), ownsHandle: true);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("Secret store path could not be safely opened.");
        }

        try
        {
            if (!OperatingSystem.IsWindows() && !IsRegularUnixFile(handle))
            {
                throw new UnauthorizedAccessException("Secret store must be a regular file.");
            }

            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: OperatingSystem.IsWindows());
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static bool IsRegularUnixFile(SafeFileHandle handle)
    {
        var fileDescriptor = handle.DangerousGetHandle().ToInt32();
        ushort mode;
        if (OperatingSystem.IsMacOS())
        {
            if (GetFileStatusMac(fileDescriptor, out var status) != 0)
            {
                return false;
            }

            mode = status.Mode;
        }
        else
        {
            if (GetFileStatusLinux(
                    fileDescriptor,
                    string.Empty,
                    LinuxAtEmptyPath,
                    LinuxStatxType,
                    out var status) != 0)
            {
                return false;
            }

            mode = status.Mode;
        }

        return (mode & UnixFileTypeMask) == UnixRegularFile;
    }

    private static string GetOpenedPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = new StringBuilder(32_768);
            var length = GetFinalPathNameByHandleWindows(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity)
            {
                throw new UnauthorizedAccessException("Secret store path could not be safely opened.");
            }

            var openedPath = path.ToString();
            return openedPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)
                ? "\\\\" + openedPath[8..]
                : openedPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
                    ? openedPath[4..]
                    : openedPath;
        }

        if (OperatingSystem.IsMacOS())
        {
            var path = new byte[1024];
            if (GetFilePathMac(handle.DangerousGetHandle().ToInt32(), MacGetPath, path) != 0)
            {
                throw new UnauthorizedAccessException("Secret store path could not be safely opened.");
            }

            var terminator = Array.IndexOf(path, (byte)0);
            return Encoding.UTF8.GetString(path, 0, terminator >= 0 ? terminator : path.Length);
        }

        var descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle()}";
        var target = File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true);
        return target?.FullName
            ?? throw new UnauthorizedAccessException("Secret store path could not be safely opened.");
    }

    private static bool ValidateElement(JsonElement element, ref int propertyCount)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > MaximumProperties
                || !names.Add(property.Name)
                || !ValidateElement(property.Value, ref propertyCount))
            {
                return false;
            }
        }

        return true;
    }

    private const uint GenericRead = 0x80000000;
    private const int OpenExisting = 3;
    private const int FileFlagOpenReparsePoint = 0x00200000;
    private const int FileFlagOverlapped = 0x40000000;
    private const int FileFlagSequentialScan = 0x08000000;
    private const int UnixOpenReadOnly = 0;
    private const int LinuxOpenNoFollow = 0x00020000;
    private const int LinuxOpenCloseOnExec = 0x00080000;
    private const int LinuxOpenNonBlocking = 0x00000800;
    private const int LinuxAtEmptyPath = 0x00001000;
    private const uint LinuxStatxType = 0x00000001;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const int MacOpenNonBlocking = 0x00000004;
    private const int MacGetPath = 50;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFile = 0x8000;

    // Linux statx has a fixed 256-byte ABI; stx_mode is the 16-bit field at byte offset 28.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(28)]
        internal ushort Mode;
    }

    // The supported 64-bit Darwin ABIs expose st_mode at offset 4 in the 144-byte stat structure.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinFileStatus
    {
        [FieldOffset(4)]
        internal ushort Mode;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleWindows(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenFileUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int GetFileStatusLinux(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        out LinuxFileStatus status);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int GetFileStatusMac(
        int fileDescriptor,
        out DarwinFileStatus status);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int GetFilePathMac(int fileDescriptor, int command, byte[] path);
}
