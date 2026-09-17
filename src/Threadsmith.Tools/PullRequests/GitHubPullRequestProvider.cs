namespace Threadsmith.Tools.PullRequests;

using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>Retrieves GitHub.com PR evidence using the provider's PR comparison endpoints.</summary>
public sealed class GitHubPullRequestProvider : PullRequestProvider
{
    /// <summary>Initializes a new instance of the <see cref="GitHubPullRequestProvider"/> class.</summary>
    public GitHubPullRequestProvider(HttpClient http, ISecretResolver secrets, PrFetchOptions limits)
        : base(http, secrets, limits)
    {
    }

    /// <inheritdoc />
    public override string Type => "github";

    /// <inheritdoc />
    public override string WebHost => "github.com";

    /// <inheritdoc />
    public override IReadOnlyList<string> DefaultUrlPatterns => ["https://github.com/*/*/pull/*"];

    /// <inheritdoc />
    public override string ApiHost => "api.github.com";

    /// <inheritdoc />
    public override PullRequestTarget ParseUrl(string url) => ParseUrl(url, "pull", "/repos/", "pulls");

    /// <inheritdoc />
    public override async Task<PullRequestMetadata> GetMetadataAsync(PullRequestTarget target, PullRequestProviderOptions options, CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync(target, target.ApiPath, options, cancellationToken);
        var root = json.RootElement;
        return Metadata(
            target,
            root,
            Text(root, "title"),
            Text(root, "body"),
            Text(root, "state"),
            Text(root, "head", "repo", "full_name"),
            Text(root, "head", "sha"),
            Text(root, "base", "repo", "full_name"),
            Text(root, "base", "sha"),
            root.TryGetProperty("changed_files", out var count) ? count.GetInt32() : null);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<PullRequestPage> ReadPagesAsync(PullRequestTarget target, PullRequestProviderOptions options, PrFetchKind kind, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var page = 1; ; page++)
        {
            CheckPageLimit(page);
            using var json = await GetJsonAsync(target, target.ApiPath + "/files?per_page=20&page=" + page.ToString(CultureInfo.InvariantCulture), options, cancellationToken);
            var files = json.RootElement;
            foreach (var result in FilePages(
                files.EnumerateArray().Select(file => new PullRequestFile(
                    Text(file, "filename"),
                    Text(file, "previous_filename"),
                    Text(file, "status"),
                    file.TryGetProperty("patch", out _) ? null : "File-list patch omitted; inspect raw diff for coverage or binary markers.")),
                []))
            {
                yield return result;
            }

            if (files.GetArrayLength() < 20)
            {
                break;
            }
        }

        if (kind == PrFetchKind.Inventory)
        {
            yield break;
        }

        await foreach (var page in ReadDiffAsync(target, target.ApiPath, "application/vnd.github.diff", options, cancellationToken))
        {
            yield return page;
        }
    }
}
