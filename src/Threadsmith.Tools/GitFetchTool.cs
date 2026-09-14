namespace Threadsmith.Tools;

using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>Fetches only branch tips into a new host-owned cache, without checkout or repository mutation.</summary>
public sealed record GitFetchRequest
{
    /// <summary>Credential-free HTTPS/SSH URL or a configured remote name.</summary>
    public required string Repository { get; init; }

    /// <summary>Literal source branch name.</summary>
    public required string Branch { get; init; }

    /// <summary>Optional literal comparison branch name, fetched in the same request.</summary>
    public string? BaseBranch { get; init; }
}

/// <summary>Local cache and refs for subsequent ordinary Git inspection tools.</summary>
public sealed record GitFetchResult(string Root, string Repository, string TargetRef, string? BaseRef);

/// <summary>Acquires shallow branch snapshots through the ordinary tool policy and process boundaries.</summary>
public sealed partial class GitFetchTool : Tool<GitFetchRequest, GitFetchResult>
{
    private readonly IProcessManager _processes;
    private readonly string _cacheRoot;

    /// <summary>Initializes a new instance of the <see cref="GitFetchTool"/> class.</summary>
    public GitFetchTool(IProcessManager processes, string cacheRoot, IPromptLoader prompts)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _cacheRoot = Path.GetFullPath(cacheRoot);
        Definition = RepositoryInventoryToolDefinitions.Create<GitFetchRequest, GitFetchResult>(
            "git_fetch", prompts, PromptFileNames.ToolGitFetchDescription) with
        {
            Timeout = TimeSpan.FromMinutes(2),
            Category = ToolCategory.ExternalSearch,
            Idempotency = ToolIdempotency.NonIdempotent,
            RequiresHostBinding = true,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<GitFetchResult>> ExecuteAsync(
        GitFetchRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var authority = context.Invocation;
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, authority);
        ValidateInput(input);
        var repository = input.Repository ?? throw new InvalidDataException("A repository is required.");
        if (!TryRemoteUri(repository, out _))
        {
            if (!RemoteNameRegex().IsMatch(repository))
            {
                throw new InvalidDataException("Unsupported remote repository URL or name.");
            }

            repository = (await GitAsync(authority.RepositoryPath, context, ["remote", "get-url", "--", repository], cancellationToken)).Trim();
        }

        if (!TryRemoteUri(repository, out var uri) || uri is null || uri.Scheme is not ("https" or "ssh")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(
                uri.Fragment)
            || uri.UserInfo.Contains(':') || (uri.Scheme == "https" && !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new InvalidDataException("Remote fetches require a credential-free HTTPS or SSH URL; use configured credential handling.");
        }

        if (!authority.AllowedNetworkHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Remote repository host is outside current network authority.");
        }

        if (!authority.AllowedExecutables.Contains(
            "git",
            StringComparer.OrdinalIgnoreCase)
            && !authority.AllowedExecutables.Contains("git.exe", StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Remote retrieval requires the existing Git executable grant.");
        }

        GitReferenceRules.ValidateLiteralReference(input.Branch ?? string.Empty);
        if (input.BaseBranch is not null)
        {
            GitReferenceRules.ValidateLiteralReference(input.BaseBranch);
        }

        Directory.CreateDirectory(_cacheRoot);
        var root = Path.Combine(_cacheRoot, context.ToolInvocationId.Value.ToString("N"));
        if (Directory.Exists(root))
        {
            throw new InvalidDataException("A previous acquisition exists; start a new fetch attempt.");
        }

        Directory.CreateDirectory(root);
        _ = await GitAsync(root, context, ["init", "--template=", "."], cancellationToken);
        var effectiveUrl = (await GitAsync(root, context, ["ls-remote", "--get-url", "--", repository], cancellationToken)).Trim();
        if (!string.Equals(effectiveUrl, repository, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Git URL rewriting changes the selected repository; use its exact authorized URL.");
        }

        var refs = new List<string>
        {
            "fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", "--", repository,
            $"refs/heads/{input.Branch}:refs/heads/review-target",
        };
        if (input.BaseBranch is not null)
        {
            refs.Add($"refs/heads/{input.BaseBranch}:refs/heads/review-base");
        }

        _ = await GitAsync(root, context, refs, cancellationToken);
        var safeUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return new(
            new GitFetchResult(root, safeUri.Uri.AbsoluteUri, "refs/heads/review-target", input.BaseBranch is null ? null : "refs/heads/review-base"),
            [new ToolProvenanceSource("git-remote", safeUri.Uri.AbsoluteUri, input.Branch)]);
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitFetchRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Repository);
        if (input.Repository.Length > 2048 || input.Repository.Any(char.IsControl))
        {
            throw new ArgumentException("Repository must be a bounded URL or remote name.");
        }

        GitReferenceRules.ValidateLiteralReference(input.Branch);
        if (input.BaseBranch is not null)
        {
            GitReferenceRules.ValidateLiteralReference(input.BaseBranch);
        }
    }

    /// <inheritdoc />
    protected override string DescribeActivity(GitFetchRequest input) =>
        $"{input.Repository} · {input.Branch}{(input.BaseBranch is null ? string.Empty : " ↔ " + input.BaseBranch)} · depth 1";

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(GitFetchRequest input, ToolInvocationContext context) => [context.RepositoryPath];

    /// <inheritdoc />
    protected override string? GetExecutable(GitFetchRequest input) => "git";

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetNetworkHosts(GitFetchRequest input) =>
        TryRemoteUri(input.Repository, out var uri) && uri is not null ? [uri.Host] : [];

    private static bool TryRemoteUri(string repository, out Uri? uri)
    {
        if (repository.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(repository, UriKind.Absolute, out uri);
        }

        var match = ScpRemoteRegex().Match(repository);
        return Uri.TryCreate(
            match.Success
                ? $"ssh://{match.Groups["authority"].Value}/{match.Groups["path"].Value.TrimStart('/')}"
                : string.Empty,
            UriKind.Absolute,
            out uri);
    }

    [GeneratedRegex(@"^(?<authority>(?:[A-Za-z0-9][A-Za-z0-9_.-]*@)?(?:[A-Za-z0-9][A-Za-z0-9.-]*|\[[0-9A-Fa-f:]+\])):(?<path>[^\s?#\\]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpRemoteRegex();

    private Task<string> GitAsync(
        string root,
        ToolExecutionContext context,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunGitAsync(root, context, arguments, ProcessStandardOutputFormat.ReviewText, cancellationToken);

    private async Task<string> RunGitAsync(
        string root,
        ToolExecutionContext context,
        IReadOnlyList<string> arguments,
        ProcessStandardOutputFormat format,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        var result = await _processes.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = context.ToolInvocationId,
                RunId = context.RunId,
                FileName = "git",
                WorkingDirectory = root,
                Arguments = ["-c", "core.hooksPath=", "-c", "core.fsmonitor=false", "-c", "protocol.ext.allow=never",
                "-c", "protocol.file.allow=never", "-c", "http.followRedirects=false", "-c", "submodule.recurse=false", "-c", "diff.external=", .. arguments],
                EnvironmentVariables = new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_LFS_SKIP_SMUDGE"] = "1" },
                Origin = ProcessRequestOrigin.Host,
                Timeout = TimeSpan.FromMinutes(2),
                MaximumOutputCharacters = 2 * 1024 * 1024,
                StandardOutputFormat = format,
                StandardInput = standardInput,
            },
            cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut || result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw new InvalidOperationException("Git fetch failed: " + result.StandardError[..Math.Min(result.StandardError.Length, 2048)]);
        }

        return result.StandardOutput;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteNameRegex();
}
