namespace Threadsmith.Tools.PullRequests;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Shared authenticated, bounded transport for compiled PR adapters.</summary>
public abstract class PullRequestProvider : IPullRequestProvider
{
    private readonly HttpClient _http;
    private readonly ISecretResolver _secrets;

    /// <summary>Initializes a new instance of the <see cref="PullRequestProvider"/> class with shared transport.</summary>
    protected PullRequestProvider(HttpClient http, ISecretResolver secrets, PrFetchOptions limits)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(limits);
        _http = http;
        _secrets = secrets;
        Limits = limits;
    }

    /// <inheritdoc />
    public abstract string Type { get; }

    /// <inheritdoc />
    public abstract string WebHost { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<string> DefaultUrlPatterns { get; }

    /// <inheritdoc />
    public abstract string ApiHost { get; }

    /// <summary>Shared configured output and acquisition ceilings.</summary>
    protected PrFetchOptions Limits { get; }

    /// <summary>Whether the compiled provider accepts basic account/token authentication.</summary>
    protected virtual bool SupportsBasicAuthentication => false;

    /// <inheritdoc />
    public void ValidateConfiguration(PullRequestProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var pattern in options.UrlPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 2048
                || pattern.Any(char.IsWhiteSpace) || pattern.Contains('\\', StringComparison.Ordinal)
                || pattern.Contains('?', StringComparison.Ordinal)
                || !Uri.TryCreate(pattern.Replace("*", "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.IdnHost != WebHost || !uri.IsDefaultPort
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new InvalidOperationException($"PR URL patterns must be HTTPS URLs on {WebHost}, with optional '*' wildcards in the path and no query or fragment.");
            }
        }

        var auth = options.Authentication;
        if (auth.Mode is not ("none" or "bearer" or "basic") || (!SupportsBasicAuthentication && auth.Mode == "basic"))
        {
            throw new InvalidOperationException($"Unsupported PR authentication mode for {Type}.");
        }

        if (auth.Mode == "none")
        {
            if (auth.SecretReference is not null || auth.Username is not null)
            {
                throw new InvalidOperationException("Unauthenticated PR providers cannot contain authentication fields.");
            }
        }
        else if (!SecretReference.TryParse(auth.SecretReference, out _)
            || (auth.Mode == "basic" && (string.IsNullOrWhiteSpace(auth.Username) || auth.Username.Contains(':', StringComparison.Ordinal)))
            || auth.Username?.Any(char.IsControl) == true)
        {
            throw new InvalidOperationException("PR authentication requires a valid logical secret reference and, for basic authentication, a username.");
        }
    }

    /// <inheritdoc />
    public abstract PullRequestTarget ParseUrl(string url);

    /// <inheritdoc />
    public abstract Task<PullRequestMetadata> GetMetadataAsync(PullRequestTarget target, PullRequestProviderOptions options, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract IAsyncEnumerable<PullRequestPage> ReadPagesAsync(PullRequestTarget target, PullRequestProviderOptions options, PrFetchKind kind, CancellationToken cancellationToken = default);

    /// <summary>Validates the common owner/repository/PR URL shape using adapter-owned route segments.</summary>
    protected PullRequestTarget ParseUrl(string url, string segment, string apiPrefix, string apiSegment)
    {
        if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.IdnHost != WebHost || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0)
        {
            throw new ArgumentException($"Expected an HTTPS pull request URL on {WebHost}, without credentials or query parameters.", nameof(url));
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is < 4 or > 5 || parts[2] != segment
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0
            || parts.Take(2).Any(part => part.Length == 0 || part is "." or ".." || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            || (parts.Length == 5 && parts[4] is not ("files" or "diff" or "overview" or "commits")))
        {
            throw new ArgumentException("The URL does not identify a supported pull request.", nameof(url));
        }

        var repository = $"{parts[0]}/{parts[1]}";
        var id = number.ToString(CultureInfo.InvariantCulture);
        var api = $"{apiPrefix}{repository}/{apiSegment}/{id}";
        return new PullRequestTarget($"https://{WebHost}/{repository}/{segment}/{id}", api, repository, id);
    }

    /// <summary>Groups a provider page into model-sized file inventories without one call per file.</summary>
    protected IEnumerable<PullRequestPage> FilePages(IEnumerable<PullRequestFile> files, IReadOnlyList<string> limitations)
    {
        var batch = new List<PullRequestFile>();
        var size = 0;
        foreach (var file in files)
        {
            var length = file.Path.Length + (file.PreviousPath?.Length ?? 0) + file.Status.Length + (file.Limitation?.Length ?? 0);
            if (file.Path.Length == 0 || length > Limits.PageCharacters)
            {
                throw new InvalidDataException("PR file identity is missing or exceeds the result page bound.");
            }

            if (size + length > Limits.PageCharacters && batch.Count > 0)
            {
                yield return new PullRequestPage("files", batch.ToArray(), string.Empty, limitations);
                batch.Clear();
                size = 0;
            }

            batch.Add(file);
            size += length;
        }

        if (batch.Count > 0)
        {
            yield return new PullRequestPage("files", batch.ToArray(), string.Empty, limitations);
        }
    }

    /// <summary>Gets bounded text from a required JSON path.</summary>
    protected static string Text(JsonElement root, params string[] path)
    {
        foreach (var name in path)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root))
            {
                return string.Empty;
            }
        }

        return root.ValueKind == JsonValueKind.String ? root.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>Captures a bounded model-facing metadata projection and an exact metadata revision digest.</summary>
    protected static PullRequestMetadata Metadata(PullRequestTarget target, JsonElement root, string title, string description, string state, string sourceRepository, string sourceCommit, string destinationRepository, string destinationCommit, int? expectedFiles)
    {
        if (sourceCommit.Length == 0 || destinationCommit.Length == 0 || sourceRepository.Length == 0 || destinationRepository.Length == 0)
        {
            throw new InvalidDataException("PR metadata lacks repository or commit identity.");
        }

        // Identity uses stable review-relevant fields, not volatile comment or mergeability counters.
        var revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            target.Url, title, description, state, sourceRepository, sourceCommit, destinationRepository, destinationCommit,
            Text(root, "updated_at"), Text(root, "updated_on"),
        })));
        return new PullRequestMetadata(target.Url, target.Repository, target.Number, Bound(title, 512), Bound(description, 8192), state, sourceRepository, sourceCommit, destinationRepository, destinationCommit, revision, expectedFiles);

        static string Bound(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "\n[Metadata text truncated]";
    }

    /// <summary>Loads one bounded API JSON page, with credentials confined to transport.</summary>
    protected async Task<JsonDocument> GetJsonAsync(PullRequestTarget target, string path, PullRequestProviderOptions options, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(target, path, "application/json", options, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (Limits.MaximumResponseBytes > 0 && buffer.Length + count > Limits.MaximumResponseBytes)
            {
                throw new InvalidDataException("PR API response exceeded tools.prFetch.maximumResponseBytes; coverage is incomplete.");
            }

            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }

        return JsonDocument.Parse(buffer.ToArray());
    }

    /// <summary>Streams raw provider PR diff text into bounded pages without reading the entire diff first.</summary>
    protected async IAsyncEnumerable<PullRequestPage> ReadDiffAsync(PullRequestTarget target, string path, string accept, PullRequestProviderOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(target, path, accept, options, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType is "application/json" or "text/html")
        {
            throw new InvalidDataException("PR provider returned a document instead of a raw diff.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var characters = new char[Limits.PageCharacters];
        long bytes = 0;
        var carry = string.Empty;
        int count;
        while ((count = await reader.ReadAsync(characters, cancellationToken)) > 0)
        {
            var chunk = carry + new string(characters, 0, count);
            carry = char.IsHighSurrogate(chunk[^1]) ? chunk[^1..] : string.Empty;
            if (carry.Length > 0)
            {
                chunk = chunk[..^1];
            }

            bytes += Encoding.UTF8.GetByteCount(chunk);
            if (Limits.MaximumResponseBytes > 0 && bytes > Limits.MaximumResponseBytes)
            {
                throw new InvalidDataException("PR diff exceeded tools.prFetch.maximumResponseBytes; coverage is incomplete.");
            }

            yield return new PullRequestPage("diff", [], chunk, []);
        }

        if (carry.Length > 0)
        {
            throw new InvalidDataException("PR diff contains an incomplete Unicode character.");
        }
    }

    /// <summary>Enforces acquisition paging bounds before requesting another provider page.</summary>
    protected void CheckPageLimit(int page)
    {
        if (Limits.MaximumFilePages > 0 && page > Limits.MaximumFilePages)
        {
            throw new InvalidDataException("PR file inventory exceeded tools.prFetch.maximumFilePages; coverage is incomplete.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(PullRequestTarget target, string path, string accept, PullRequestProviderOptions options, CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri($"https://{ApiHost}"), path);
        var routeEnd = target.ApiPath.LastIndexOf('/');
        var prefix = target.ApiPath[..(target.ApiPath.LastIndexOf('/', routeEnd - 1) + 1)];
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.IdnHost != ApiHost || !uri.IsDefaultPort
                || uri.UserInfo.Length > 0 || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || redirects > 3)
            {
                throw new InvalidDataException("PR provider returned an untrusted API destination.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Accept.ParseAdd(accept);
            message.Headers.UserAgent.ParseAdd("Threadsmith.NET/1.0");
            var auth = options.Authentication;
            if (auth.Mode != "none")
            {
                var request = new SecretResolutionRequest
                {
                    Reference = SecretReference.Parse(auth.SecretReference ?? throw new InvalidOperationException("PR credential reference is missing.")),
                    ComponentId = $"pr-fetch:{Type}",
                    Purpose = "read a provider-hosted pull request",
                    MinimumTrust = SecretProviderTrust.UserOwned,
                };
                var secret = (await _secrets.ResolveAsync(request, cancellationToken)).RequireValue(request);
                message.Headers.Authorization = auth.Mode == "basic"
                    ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + secret)))
                    : new AuthenticationHeaderValue("Bearer", secret);
            }

            var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                uri = location is null ? throw new InvalidDataException("PR API redirect omitted its destination.") : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"PR API returned HTTP {(int)status}. Check the configured account, PR access, or provider rate limits.", null, status);
            }

            return response;
        }
    }
}
