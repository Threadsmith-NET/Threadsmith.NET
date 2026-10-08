namespace Threadsmith.Workspaces;

using Threadsmith.Core;

/// <summary>Resolves local snapshot metadata through the existing bounded Git process owner.</summary>
public sealed partial class GitQueryService
{
    private async Task<GitShowResult> ShowSnapshotMetadataAsync(
        string repositoryPath,
        GitShowRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Inventory || request.IncludeWorkingTree || request.Paths.Count > 0 || request.Path is not null)
        {
            throw new ArgumentException("Snapshot metadata cannot be combined with file reads.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var checkoutIdentity = RepositoryIdentity.Create(root);

        // A missing Git marker is a bounded local observation, not permission to search parent directories.
        if (!File.Exists(Path.Combine(root, ".git")) && !Directory.Exists(Path.Combine(root, ".git")))
        {
            return Result(new GitSnapshotMetadata(checkoutIdentity, checkoutIdentity, request.Revision, null, null, null, false, false, "No Git worktree marker exists at the opened root."));
        }

        _ = await ValidateRepositoryAsync(root, cancellationToken);
        var common = await ReadAsync(["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        var head = await ReadAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], 1);
        var commit = request.Revision == "HEAD" ? head
            : await ReadAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", request.Revision + "^{commit}"], 1);
        var branch = await ReadAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], 1);
        var shallow = await ReadAsync(["rev-parse", "--is-shallow-repository"]);
        if ((head.Length > 0 && !IsCommit(head)) || (commit.Length > 0 && !IsCommit(commit))
            || shallow is not ("true" or "false") || !Path.IsPathFullyQualified(common))
        {
            throw new InvalidDataException("Git snapshot metadata was malformed.");
        }

        var limitation = commit.Length == 0 ? "Selected commit is unavailable; HEAD may be unborn."
            : shallow == "true" ? "History is shallow; ancestors beyond the local boundary are unavailable." : null;
        return Result(new GitSnapshotMetadata(
            RepositoryIdentity.Create(common),
            checkoutIdentity,
            request.Revision,
            commit.Length == 0 ? null : commit,
            head.Length == 0 ? null : head,
            branch.Length == 0 ? null : branch,
            true,
            shallow == "true",
            limitation));

        async Task<string> ReadAsync(IReadOnlyList<string> arguments, int? allowedExitCode = null)
        {
            var output = await RunAsync(root, arguments, cancellationToken, allowedExitCode: allowedExitCode);
            return output.IsTruncated ? throw new InvalidDataException("Git snapshot metadata exceeds its bound.") : output.Text.Trim();
        }

        GitShowResult Result(GitSnapshotMetadata metadata) => new(
            metadata.Commit ?? string.Empty, GitObjectKind.Commit, string.Empty, false, false) { Snapshot = metadata };
    }

    private static bool IsCommit(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
