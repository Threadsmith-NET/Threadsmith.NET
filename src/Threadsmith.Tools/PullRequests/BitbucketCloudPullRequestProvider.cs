namespace Threadsmith.Tools.PullRequests;

using System.Runtime.CompilerServices;

/// <summary>Retrieves Bitbucket Cloud PR evidence without reconstructing a branch-tip diff.</summary>
public sealed class BitbucketCloudPullRequestProvider : PullRequestProvider
{
    /// <summary>Initializes a new instance of the <see cref="BitbucketCloudPullRequestProvider"/> class.</summary>
    public BitbucketCloudPullRequestProvider(HttpClient http, ISecretResolver secrets, PrFetchOptions limits)
        : base(http, secrets, limits)
    {
    }

    /// <inheritdoc />
    public override string Type => "bitbucketCloud";

    /// <inheritdoc />
    public override string WebHost => "bitbucket.org";

    /// <inheritdoc />
    public override IReadOnlyList<string> DefaultUrlPatterns => ["https://bitbucket.org/*/*/pull-requests/*"];

    /// <inheritdoc />
    public override string ApiHost => "api.bitbucket.org";

    /// <inheritdoc />
    protected override bool SupportsBasicAuthentication => true;

    /// <inheritdoc />
    public override PullRequestTarget ParseUrl(string url) => ParseUrl(url, "pull-requests", "/2.0/repositories/", "pullrequests");

    /// <inheritdoc />
    public override async Task<PullRequestMetadata> GetMetadataAsync(PullRequestTarget target, PullRequestProviderOptions options, CancellationToken cancellationToken = default)
    {
        using var json = await GetJsonAsync(target, target.ApiPath, options, cancellationToken);
        var root = json.RootElement;
        return Metadata(
            target,
            root,
            Text(root, "title"),
            Text(root, "description"),
            Text(root, "state"),
            Text(root, "source", "repository", "full_name"),
            Text(root, "source", "commit", "hash"),
            Text(root, "destination", "repository", "full_name"),
            Text(root, "destination", "commit", "hash"),
            null);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<PullRequestPage> ReadPagesAsync(PullRequestTarget target, PullRequestProviderOptions options, PrFetchKind kind, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var next = target.ApiPath + "/diffstat?pagelen=20";
        var received = 0;
        int? expected = null;
        for (var page = 1; next.Length > 0; page++)
        {
            CheckPageLimit(page);
            using var json = await GetJsonAsync(target, next, options, cancellationToken);
            if (json.RootElement.TryGetProperty("size", out var size))
            {
                expected = size.GetInt32();
            }

            var files = json.RootElement.GetProperty("values").EnumerateArray().Select(file =>
            {
                var oldPath = Text(file, "old", "path");
                var newPath = Text(file, "new", "path");
                return new PullRequestFile(newPath.Length == 0 ? oldPath : newPath, oldPath.Length == 0 ? null : oldPath, Text(file, "status"));
            });
            foreach (var result in FilePages(files, []))
            {
                received += result.Files.Count;
                yield return result;
            }

            next = Text(json.RootElement, "next");
        }

        if (expected is { } total && total != received)
        {
            throw new InvalidDataException("Bitbucket returned an incomplete PR changed-file list.");
        }

        if (kind == PrFetchKind.Inventory)
        {
            yield break;
        }

        await foreach (var page in ReadDiffAsync(target, target.ApiPath + "/diff", "text/plain", options, cancellationToken))
        {
            yield return page;
        }
    }
}
