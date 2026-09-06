namespace Threadsmith.Tools;

using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Source route authorized for a governed web fetch.</summary>
public enum WebFetchSourceKind
{
    /// <summary>A current host-issued search result reference.</summary>
    SearchResult,

    /// <summary>A legacy exact one-shot URL grant created by a user action.</summary>
    DirectUrl,

    /// <summary>An exact URL authored in the current top-level user message.</summary>
    CurrentUserMessage,

    /// <summary>An exact direct group created through an explicit host authorization surface.</summary>
    ExplicitDirectGroup,

    /// <summary>An exact model-proposed URL approved for one pending invocation.</summary>
    ModelProposedApproved,
}

/// <summary>Closed fetch failure classifications safe for projection.</summary>
public enum WebFetchFailureKind
{
    /// <summary>The request or authorization was malformed or stale.</summary>
    InvalidRequest,

    /// <summary>An exact direct URL requires an explicit interactive or pre-existing host grant.</summary>
    DirectAuthorizationRequired,

    /// <summary>An interactive direct-URL approval was denied or cancelled.</summary>
    DirectAuthorizationDenied,

    /// <summary>The destination was not a public HTTPS endpoint.</summary>
    UnsafeDestination,

    /// <summary>A redirect was not authorized.</summary>
    RedirectDenied,

    /// <summary>The response media type is unsupported.</summary>
    UnsupportedContent,

    /// <summary>A transport, decoded, parser, or output bound was exceeded.</summary>
    LimitExceeded,

    /// <summary>The operation exceeded its deadline.</summary>
    Timeout,

    /// <summary>The remote endpoint failed.</summary>
    TransportFailure,
}

/// <summary>Stage at which fetched content was truncated.</summary>
public enum WebFetchTruncationStage
{
    /// <summary>No truncation occurred.</summary>
    None,

    /// <summary>Decoded source reached its configured bound.</summary>
    DecodedSource,

    /// <summary>Extracted readable text reached its configured bound.</summary>
    ExtractedText,
}

/// <summary>Bounded fetch request whose host-classified reference selects one authorized route.</summary>
public sealed record WebFetchRequest
{
    /// <summary>Opaque search/user URL reference or exact authorized public URL.</summary>
    public required string Reference { get; init; }
}

/// <summary>One sanitized redirect hop containing no query data.</summary>
public sealed record WebFetchRedirect(string From, string To, bool SameOrigin);

/// <summary>Sanitized, query-free fetch provenance.</summary>
public sealed record WebFetchProvenance
{
    /// <summary>Activation route used.</summary>
    public WebFetchSourceKind SourceKind { get; init; }

    /// <summary>Sanitized requested public URL.</summary>
    public required string RequestedUrl { get; init; }

    /// <summary>Sanitized final public URL.</summary>
    public required string FinalUrl { get; init; }

    /// <summary>Digest of the exact requested transport URL.</summary>
    public required string RequestedUrlDigest { get; init; }

    /// <summary>Digest of the exact final transport URL.</summary>
    public required string FinalUrlDigest { get; init; }

    /// <summary>Bounded sanitized redirect chain.</summary>
    public IReadOnlyList<WebFetchRedirect> Redirects { get; init; } = [];

    /// <summary>UTC retrieval time.</summary>
    public DateTimeOffset RetrievedAt { get; init; }
}

/// <summary>Explicit truncation provenance.</summary>
public sealed record WebFetchTruncation(
    WebFetchTruncationStage Stage,
    string? Reason);

/// <summary>Bounded readable response framed as untrusted external evidence.</summary>
public sealed record WebFetchResponse
{
    /// <summary>Sanitized retrieval provenance.</summary>
    public required WebFetchProvenance Provenance { get; init; }

    /// <summary>Declared/effective allowlisted media type.</summary>
    public required string MediaType { get; init; }

    /// <summary>Validated character encoding.</summary>
    public required string CharacterEncoding { get; init; }

    /// <summary>Optional bounded extracted title.</summary>
    public string? Title { get; init; }

    /// <summary>Bounded readable content.</summary>
    public required string Text { get; init; }

    /// <summary>SHA-256 digest of exact bounded decoded source.</summary>
    public required string SourceDigest { get; init; }

    /// <summary>SHA-256 digest of extracted text.</summary>
    public required string TextDigest { get; init; }

    /// <summary>Compressed bytes received.</summary>
    public long CompressedBytes { get; init; }

    /// <summary>Decoded source byte count.</summary>
    public long DecodedBytes { get; init; }

    /// <summary>Extracted character count.</summary>
    public int ExtractedCharacters { get; init; }

    /// <summary>Stable extractor implementation identity.</summary>
    public string ExtractionMethod { get; init; } = WebReadableTextExtractor.Version;

    /// <summary>Truncation details.</summary>
    public WebFetchTruncation Truncation { get; init; } = new(WebFetchTruncationStage.None, null);

    /// <summary>Mandatory immutable trust boundary.</summary>
    public required string TrustBoundary { get; init; }
}

/// <summary>Sanitized fetch exception with a closed failure kind.</summary>
public sealed class WebFetchException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WebFetchException"/> class.</summary>
    public WebFetchException()
        : this(WebFetchFailureKind.TransportFailure, "The web fetch failed.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebFetchException"/> class.</summary>
    public WebFetchException(string message)
        : this(WebFetchFailureKind.TransportFailure, message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebFetchException"/> class.</summary>
    public WebFetchException(string message, Exception innerException)
        : this(WebFetchFailureKind.TransportFailure, message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebFetchException"/> class with a closed failure kind.</summary>
    public WebFetchException(WebFetchFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Failure classification.</summary>
    public WebFetchFailureKind Kind { get; }
}

/// <summary>Validated security and resource bounds for web fetch.</summary>
public sealed record WebFetchOptions
{
    /// <summary>Maximum URL characters.</summary>
    public int MaximumUrlCharacters { get; init; } = 2048;

    /// <summary>Maximum manual redirect hops.</summary>
    public int MaximumRedirects { get; init; } = 3;

    /// <summary>Whole-operation deadline.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum compressed bytes read.</summary>
    public int MaximumCompressedBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum decompressed bytes retained.</summary>
    public int MaximumDecodedBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Maximum extracted characters.</summary>
    public int MaximumExtractedCharacters { get; init; } = 128 * 1024;

    /// <summary>Maximum HTML tokens processed.</summary>
    public int MaximumHtmlTokens { get; init; } = 100_000;

    /// <summary>Maximum HTML nesting depth.</summary>
    public int MaximumHtmlDepth { get; init; } = 128;

    /// <summary>Maximum JSON tokens processed.</summary>
    public int MaximumJsonTokens { get; init; } = 100_000;

    /// <summary>Maximum JSON nesting depth.</summary>
    public int MaximumJsonDepth { get; init; } = 64;

    /// <summary>Maximum individual decoded string length.</summary>
    public int MaximumStringCharacters { get; init; } = 128 * 1024;

    /// <summary>Search reference lifetime.</summary>
    public TimeSpan ReferenceLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Builds effective options while allowing repository-inclusive configuration only to narrow trusted ceilings.</summary>
    public static WebFetchOptions FromConfiguration(
        IConfiguration configuration,
        IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        var defaults = new WebFetchOptions();
        var options = new WebFetchOptions
        {
            MaximumUrlCharacters = NarrowInt("webFetch:maximumUrlCharacters", defaults.MaximumUrlCharacters),
            MaximumRedirects = NarrowInt("webFetch:maximumRedirects", defaults.MaximumRedirects),
            Timeout = TimeSpan.FromSeconds(NarrowInt("webFetch:timeoutSeconds", (int)defaults.Timeout.TotalSeconds)),
            MaximumCompressedBytes = NarrowInt("webFetch:maximumCompressedBytes", defaults.MaximumCompressedBytes),
            MaximumDecodedBytes = NarrowInt("webFetch:maximumDecodedBytes", defaults.MaximumDecodedBytes),
            MaximumExtractedCharacters = NarrowInt("webFetch:maximumExtractedCharacters", defaults.MaximumExtractedCharacters),
        };
        options.Validate();
        return options;

        int NarrowInt(string key, int defaultValue)
        {
            var trustedCeiling = trustedConfiguration.GetValue(key, defaultValue);
            var requestedValue = configuration.GetValue(key, trustedCeiling);
            return Math.Min(requestedValue, trustedCeiling);
        }
    }

    /// <summary>Validates compiled/narrowed limits.</summary>
    public void Validate()
    {
        if (MaximumUrlCharacters is < 1 or > 8192
            || MaximumRedirects is < 0 or > 5
            || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(60)
            || MaximumCompressedBytes is < 1024 or > 4 * 1024 * 1024
            || MaximumDecodedBytes is < 1024 or > 8 * 1024 * 1024
            || MaximumExtractedCharacters is < 1024 or > 512 * 1024
            || MaximumHtmlTokens is < 100 or > 500_000
            || MaximumHtmlDepth is < 8 or > 256
            || MaximumJsonTokens is < 100 or > 500_000
            || MaximumJsonDepth is < 8 or > 128
            || MaximumStringCharacters is < 1024 or > 512 * 1024
            || ReferenceLifetime <= TimeSpan.Zero || ReferenceLifetime > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException("Web-fetch limits are outside compiled security bounds.");
        }
    }
}

/// <summary>Thread-safe effective fetch limits that can follow the active repository.</summary>
public sealed class WebFetchOptionsState
{
    private readonly IConfiguration? _trustedConfiguration;
    private WebFetchOptions _current;

    /// <summary>Initializes a new instance of the <see cref="WebFetchOptionsState"/> class with fixed effective limits.</summary>
    public WebFetchOptionsState(WebFetchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        TrustedCeiling = options;
        _current = options;
    }

    /// <summary>Initializes a new instance of the <see cref="WebFetchOptionsState"/> class from layered configuration.</summary>
    public WebFetchOptionsState(IConfiguration configuration, IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        _trustedConfiguration = trustedConfiguration;
        TrustedCeiling = WebFetchOptions.FromConfiguration(trustedConfiguration, trustedConfiguration);
        _current = WebFetchOptions.FromConfiguration(configuration, trustedConfiguration);
    }

    /// <summary>Gets the stable repository-excluding ceiling used for immutable enforcement metadata.</summary>
    public WebFetchOptions TrustedCeiling { get; }

    /// <summary>Gets one immutable effective snapshot for an authorization or retrieval operation.</summary>
    public WebFetchOptions Current => Volatile.Read(ref _current);

    /// <summary>Rebinds effective narrowing values to the specified repository configuration.</summary>
    public Task BindRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        if (_trustedConfiguration is null)
        {
            throw new InvalidOperationException("Fixed web-fetch options cannot be rebound to another repository.");
        }

        var configurationPath = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            ".threadsmith",
            "config.json");
        var repositoryConfiguration = new ConfigurationBuilder()
            .AddJsonFile(configurationPath, optional: true)
            .Build();
        var next = WebFetchOptions.FromConfiguration(
            repositoryConfiguration,
            _trustedConfiguration);
        Volatile.Write(ref _current, next);
        return Task.CompletedTask;
    }
}

/// <summary>One transient protected transport response.</summary>
public sealed record WebFetchTransportResponse(
    Uri RequestedUri,
    Uri FinalUri,
    HttpStatusCode StatusCode,
    string MediaType,
    string? CharacterSet,
    string? ContentEncoding,
    byte[] Body,
    long CompressedBytes,
    IReadOnlyList<(Uri From, Uri To)> Redirects);

/// <summary>Credential-free bounded fetch transport boundary.</summary>
public interface IWebContentTransport
{
    /// <summary>Retrieves one authorized HTTPS document with per-hop endpoint enforcement.</summary>
    Task<WebFetchTransportResponse> GetAsync(
        Uri uri,
        WebFetchSourceKind sourceKind,
        IReadOnlySet<string> authorizedDirectUrlDigests,
        WebFetchOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider-neutral readable web-content boundary.</summary>
public interface IWebContentFetcher
{
    /// <summary>Fetches and extracts one authorized document.</summary>
    Task<WebFetchResponse> FetchAsync(
        Uri uri,
        WebFetchSourceKind sourceKind,
        IReadOnlySet<string> authorizedDirectUrlDigests,
        CancellationToken cancellationToken = default);
}

/// <summary>Strict public-HTTPS URL normalization and safe provenance projection.</summary>
public static class WebFetchUrlPolicy
{
    /// <summary>Parses and canonicalizes one exact transport URL.</summary>
    public static Uri Normalize(string value, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maximumCharacters || value.Any(char.IsControl))
        {
            throw new WebFetchException(WebFetchFailureKind.InvalidRequest, "The fetch URL violates host bounds.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port != 443
            || string.IsNullOrWhiteSpace(uri.IdnHost)
            || uri.IdnHost.Length > 253)
        {
            throw new WebFetchException(WebFetchFailureKind.InvalidRequest, "Only absolute public HTTPS URLs without credentials or non-default ports are supported.");
        }

        try
        {
            _ = new IdnMapping().GetAscii(uri.IdnHost);
        }
        catch (ArgumentException exception)
        {
            throw new WebFetchException(WebFetchFailureKind.InvalidRequest, "The fetch hostname is invalid.", exception);
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri;
    }

    /// <summary>Returns a query-free URL suitable for logs, evidence, and model projection.</summary>
    public static string Sanitize(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty, UserName = string.Empty, Password = string.Empty }
            .Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }

    /// <summary>Computes a non-reversible exact URL identity.</summary>
    public static string Digest(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
    }

    /// <summary>Returns whether two URLs share an exact HTTPS origin.</summary>
    public static bool SameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
                && left.Port == right.Port;
    }
}

/// <summary>Fail-closed classifier for public Internet addresses.</summary>
public static class PublicIpAddressPolicy
{
    /// <summary>Rejects non-public, reserved, metadata, mapped-unsafe, and ambiguous addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return !Ipv4Denied.Any(range => (value & range.Mask) == range.Network);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        return HasPrefix(bytes, [0x20], 3)
            && !HasPrefix(bytes, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 96)
            && !HasPrefix(bytes, [0xfc], 7)
            && !HasPrefix(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01], 48)
            && !HasPrefix(bytes, [0x00, 0x64, 0xff, 0x9b, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 96)
            && !HasPrefix(bytes, [0x20, 0x02], 16)
            && !HasPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32)
            && !HasPrefix(bytes, [0x20, 0x01, 0x00], 23)
            && !HasPrefix(bytes, [0x20, 0x01, 0x00, 0x02], 48)
            && !HasPrefix(bytes, [0x20, 0x01, 0x00, 0x10], 28)
            && !HasPrefix(bytes, [0x3f, 0xfe], 16)
            && !HasPrefix(bytes, [0x01, 0x00], 8);
    }

    /// <summary>Requires a non-empty homogeneous set of public addresses.</summary>
    public static void EnsureAllPublic(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0 || addresses.Any(address => !IsPublic(address)))
        {
            throw new WebFetchException(WebFetchFailureKind.UnsafeDestination, "The destination did not resolve exclusively to public addresses.");
        }
    }

    private static readonly (uint Network, uint Mask)[] Ipv4Denied =
    [
        Cidr(0, 0, 0, 0, 8), Cidr(10, 0, 0, 0, 8), Cidr(100, 64, 0, 0, 10),
        Cidr(127, 0, 0, 0, 8), Cidr(169, 254, 0, 0, 16), Cidr(172, 16, 0, 0, 12),
        Cidr(192, 0, 0, 0, 24), Cidr(192, 0, 2, 0, 24), Cidr(192, 88, 99, 0, 24),
        Cidr(192, 168, 0, 0, 16), Cidr(198, 18, 0, 0, 15), Cidr(198, 51, 100, 0, 24),
        Cidr(203, 0, 113, 0, 24), Cidr(224, 0, 0, 0, 4), Cidr(240, 0, 0, 0, 4),
        Cidr(255, 255, 255, 255, 32), Cidr(169, 254, 169, 254, 32),
    ];

    private static (uint Network, uint Mask) Cidr(byte a, byte b, byte c, byte d, int prefix)
    {
        var value = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        return (value & mask, mask);
    }

    private static bool HasPrefix(byte[] address, byte[] prefix, int bits)
    {
        for (var bit = 0; bit < bits; bit++)
        {
            if (((address[bit / 8] >> (7 - (bit % 8))) & 1) != ((prefix[bit / 8] >> (7 - (bit % 8))) & 1))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Credential-free HTTPS transport with manual redirects and connection-time IP pinning.</summary>
public sealed class PublicHttpsWebContentTransport : IWebContentTransport
{
    /// <inheritdoc />
    public async Task<WebFetchTransportResponse> GetAsync(
        Uri uri,
        WebFetchSourceKind sourceKind,
        IReadOnlySet<string> authorizedDirectUrlDigests,
        WebFetchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(authorizedDirectUrlDigests);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var current = uri;
        var redirects = new List<(Uri From, Uri To)>();
        for (var hop = 0; ; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceKind is WebFetchSourceKind.DirectUrl
                    or WebFetchSourceKind.CurrentUserMessage
                    or WebFetchSourceKind.ExplicitDirectGroup
                    or WebFetchSourceKind.ModelProposedApproved
                && !authorizedDirectUrlDigests.Contains(WebFetchUrlPolicy.Digest(current)))
            {
                throw new WebFetchException(WebFetchFailureKind.RedirectDenied, "The direct fetch target was not explicitly authorized.");
            }

            using var client = await CreatePinnedClientAsync(current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Threadsmith.NET/1.0");
            request.Headers.Accept.ParseAdd("text/html, text/plain, text/markdown, application/xhtml+xml, application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (hop >= options.MaximumRedirects || response.Headers.Location is null)
                {
                    throw new WebFetchException(WebFetchFailureKind.RedirectDenied, "The redirect chain was missing, malformed, or exceeded its bound.");
                }

                var target = WebFetchUrlPolicy.Normalize(new Uri(current, response.Headers.Location).AbsoluteUri, options.MaximumUrlCharacters);
                if (sourceKind == WebFetchSourceKind.SearchResult && !WebFetchUrlPolicy.SameOrigin(current, target))
                {
                    throw new WebFetchException(WebFetchFailureKind.RedirectDenied, "Cross-origin search-result redirects are denied.");
                }

                if (redirects.Any(item => item.From == target || item.To == target))
                {
                    throw new WebFetchException(WebFetchFailureKind.RedirectDenied, "A redirect loop was detected.");
                }

                redirects.Add((current, target));
                current = target;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new WebFetchException(WebFetchFailureKind.TransportFailure, $"The public endpoint returned HTTP {(int)response.StatusCode}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()
                ?? throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "A declared textual media type is required.");
            ValidateMediaType(mediaType);
            var compressed = await ReadBoundedAsync(response.Content, options.MaximumCompressedBytes, cancellationToken);
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant();
            var decoded = await DecodeContentAsync(compressed, encoding, options.MaximumDecodedBytes, cancellationToken);
            return new WebFetchTransportResponse(
                uri,
                current,
                response.StatusCode,
                mediaType,
                response.Content.Headers.ContentType?.CharSet,
                encoding,
                decoded,
                compressed.LongLength,
                redirects);
        }
    }

    private static async Task<HttpClient> CreatePinnedClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken);
        PublicIpAddressPolicy.EnsureAllPublic(addresses);
        var selected = addresses.OrderBy(address => address.AddressFamily).ThenBy(address => address.ToString(), StringComparer.Ordinal).First();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(selected, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "The compressed response exceeded its bound.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await ReadStreamBoundedAsync(stream, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]> DecodeContentAsync(byte[] input, string? encoding, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var source = new MemoryStream(input, writable: false);
        await using Stream decoded = encoding switch
        {
            null or "identity" => source,
            "gzip" => new GZipStream(source, CompressionMode.Decompress, leaveOpen: false),
            "deflate" => new DeflateStream(source, CompressionMode.Decompress, leaveOpen: false),
            "br" => new BrotliStream(source, CompressionMode.Decompress, leaveOpen: false),
            _ => throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The content encoding is unsupported."),
        };
        return await ReadStreamBoundedAsync(decoded, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadStreamBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "The decoded response exceeded its bound.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateMediaType(string mediaType)
    {
        if (mediaType is not "text/html" and not "text/plain" and not "text/markdown"
            and not "application/xhtml+xml" and not "application/json"
            && !(mediaType.StartsWith("application/", StringComparison.Ordinal)
                && mediaType.EndsWith("+json", StringComparison.Ordinal)))
        {
            throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response media type is not in the textual allowlist.");
        }
    }
}

/// <summary>Deterministic bounded readable-text extraction.</summary>
public static class WebReadableTextExtractor
{
    /// <summary>Stable extractor version included in provenance.</summary>
    public const string Version = "threadsmith-readable-text-v1";

    private static readonly HashSet<string> _activeHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "svg", "form", "iframe", "object", "embed",
    };

    private static readonly HashSet<string> _blockHtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "h1", "h2", "h3", "h4", "h5", "h6", "p", "div", "li", "pre", "code", "tr", "td", "th", "br", "blockquote",
    };

    /// <summary>Extracts allowlisted textual input within parser-specific limits.</summary>
    public static (string Text, string? Title, bool Truncated) Extract(
        string source,
        string mediaType,
        WebFetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        string text;
        string? title = null;
        if (mediaType is "text/html" or "application/xhtml+xml")
        {
            (text, title) = ExtractHtml(source, options, cancellationToken);
        }
        else if (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal))
        {
            text = ExtractJson(source, options, cancellationToken);
        }
        else
        {
            text = source;
        }

        text = Normalize(text, cancellationToken);
        var truncated = text.Length > options.MaximumExtractedCharacters;
        return (truncated ? text[..options.MaximumExtractedCharacters] : text, title, truncated);
    }

    private static readonly HashSet<string> _htmlVoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static string ExtractJson(string source, WebFetchOptions options, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            MaxDepth = options.MaximumJsonDepth,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var output = new StringBuilder(Math.Min(source.Length, options.MaximumExtractedCharacters));
        var tokens = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++tokens > options.MaximumJsonTokens)
            {
                throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "JSON token count exceeded the parser bound.");
            }

            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
            {
                var value = reader.GetString() ?? string.Empty;
                if (value.Length > options.MaximumStringCharacters)
                {
                    throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "A JSON string exceeded the parser bound.");
                }

                output.Append(value).Append(reader.TokenType == JsonTokenType.PropertyName ? ": " : "\n");
            }
            else if (reader.TokenType is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null)
            {
                output.Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append('\n');
            }
        }

        return output.ToString();
    }

    private static (string Text, string? Title) ExtractHtml(
        string source,
        WebFetchOptions options,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(source.Length, options.MaximumExtractedCharacters));
        var title = new StringBuilder(300);
        var elements = new Stack<HtmlElementState>();
        var tokens = 0;
        var index = 0;
        while (index < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tagStart = source.IndexOf('<', index);
            var textEnd = tagStart < 0 ? source.Length : tagStart;
            if (!IsSuppressed(elements) && textEnd > index)
            {
                AppendDecodedHtmlText(
                    source,
                    index,
                    textEnd,
                    output,
                    elements.Any(element => element.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) ? title : null,
                    cancellationToken);
            }

            if (tagStart < 0)
            {
                break;
            }

            if (++tokens > options.MaximumHtmlTokens)
            {
                throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "HTML token count exceeded the parser bound.");
            }

            if (source.AsSpan(tagStart).StartsWith("<!--", StringComparison.Ordinal))
            {
                var commentEnd = source.IndexOf("-->", tagStart + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? source.Length : commentEnd + 3;
                continue;
            }

            var tagEnd = source.IndexOf('>', tagStart + 1);
            if (tagEnd < 0 || tagEnd - tagStart > 8192)
            {
                throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "An HTML token exceeded the parser bound.");
            }

            var token = source.AsSpan(tagStart + 1, tagEnd - tagStart - 1);
            var closing = token.TrimStart().StartsWith("/", StringComparison.Ordinal);
            var declaration = token.TrimStart().StartsWith("!", StringComparison.Ordinal)
                || token.TrimStart().StartsWith("?", StringComparison.Ordinal);
            var name = ReadHtmlElementName(token, closing);
            if (_blockHtmlElements.Contains(name) && !IsSuppressed(elements))
            {
                output.Append('\n');
            }

            if (closing)
            {
                PopThrough(elements, name);
            }
            else if (!declaration && name.Length > 0 && !_htmlVoidElements.Contains(name) && !token.TrimEnd().EndsWith("/", StringComparison.Ordinal))
            {
                var suppressed = IsSuppressed(elements) || _activeHtmlElements.Contains(name) || HasHiddenAttribute(token);
                elements.Push(new HtmlElementState(name, suppressed));
                if (elements.Count > options.MaximumHtmlDepth)
                {
                    throw new WebFetchException(WebFetchFailureKind.LimitExceeded, "HTML nesting exceeded the parser bound.");
                }
            }

            index = tagEnd + 1;
        }

        var normalizedTitle = Normalize(title.ToString(), cancellationToken);
        return (output.ToString(), normalizedTitle.Length == 0 ? null : normalizedTitle);
    }

    private static void AppendDecodedHtmlText(
        string source,
        int start,
        int end,
        StringBuilder output,
        StringBuilder? title,
        CancellationToken cancellationToken)
    {
        const int chunkCharacters = 4096;
        var index = start;
        while (index < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkEnd = Math.Min(index + chunkCharacters, end);
            if (chunkEnd < end)
            {
                var lastAmpersand = source.LastIndexOf('&', chunkEnd - 1, chunkEnd - index);
                var lastSemicolon = source.LastIndexOf(';', chunkEnd - 1, chunkEnd - index);
                if (lastAmpersand > lastSemicolon)
                {
                    var nextSemicolon = source.IndexOf(';', chunkEnd, Math.Min(64, end - chunkEnd));
                    if (nextSemicolon >= 0)
                    {
                        chunkEnd = nextSemicolon + 1;
                    }
                }
            }

            var decoded = WebUtility.HtmlDecode(source[index..chunkEnd]);
            output.Append(decoded);
            title?.Append(decoded);
            index = chunkEnd;
        }
    }

    private static string ReadHtmlElementName(ReadOnlySpan<char> token, bool closing)
    {
        token = token.TrimStart();
        if (closing)
        {
            token = token[1..].TrimStart();
        }

        var length = 0;
        while (length < token.Length && (char.IsAsciiLetterOrDigit(token[length]) || token[length] is '-' or ':'))
        {
            length++;
        }

        return token[..length].ToString();
    }

    private static bool HasHiddenAttribute(ReadOnlySpan<char> token)
    {
        var attributes = token.ToString();
        return ContainsHtmlAttribute(attributes, "hidden")
            || ContainsHtmlAttributeValue(attributes, "aria-hidden", "true")
            || ContainsCssHiddenValue(attributes, "display", "none")
            || ContainsCssHiddenValue(attributes, "visibility", "hidden");
    }

    private static bool ContainsHtmlAttribute(string token, string attribute)
    {
        return token.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Any(part => part.Equals(attribute, StringComparison.OrdinalIgnoreCase)
                    || part.StartsWith(attribute + "=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsHtmlAttributeValue(string token, string attribute, string value)
    {
        var index = token.IndexOf(attribute, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var remainder = token[(index + attribute.Length)..].TrimStart();
        return remainder.StartsWith('=')
            && remainder[1..].TrimStart([' ', '\t', '\r', '\n', '\'', '"'])
                .StartsWith(value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCssHiddenValue(string token, string property, string value)
    {
        var style = token.IndexOf("style", StringComparison.OrdinalIgnoreCase);
        if (style < 0)
        {
            return false;
        }

        var compact = token[style..].Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Contains(property + ":" + value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuppressed(IEnumerable<HtmlElementState> elements)
    {
        return elements.Any(element => element.Suppressed);
    }

    private static void PopThrough(Stack<HtmlElementState> elements, string name)
    {
        if (!elements.Any(element => element.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        while (elements.Count > 0)
        {
            var element = elements.Pop();
            if (element.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static string Normalize(string value, CancellationToken cancellationToken)
    {
        var clean = new StringBuilder(value.Length);
        var pendingSpace = false;
        var pendingNewline = false;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var character = value[index];
            if (character is '\r' or '\n')
            {
                pendingNewline = clean.Length > 0;
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(character))
            {
                pendingSpace = clean.Length > 0 && !pendingNewline;
            }
            else if (!char.IsControl(character))
            {
                if (pendingNewline)
                {
                    clean.Append('\n');
                }
                else if (pendingSpace)
                {
                    clean.Append(' ');
                }

                clean.Append(character);
                pendingNewline = false;
                pendingSpace = false;
            }
        }

        return clean.ToString();
    }

    private sealed record HtmlElementState(string Name, bool Suppressed);
}

/// <summary>Fetches, decodes, extracts, digests, and sanitizes one authorized document.</summary>
public sealed class WebContentFetcher : IWebContentFetcher
{
    private readonly WebFetchOptionsState _options;
    private readonly IPromptLoader _prompts;
    private readonly IWebContentTransport _transport;

    /// <summary>Initializes a new instance of the <see cref="WebContentFetcher"/> class with fixed effective limits.</summary>
    public WebContentFetcher(IWebContentTransport transport, WebFetchOptions options, IPromptLoader prompts)
        : this(transport, new WebFetchOptionsState(options), prompts)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WebContentFetcher"/> class with rebindable effective limits.</summary>
    public WebContentFetcher(IWebContentTransport transport, WebFetchOptionsState options, IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _transport = transport;
        _options = options;
        _prompts = prompts;
    }

    /// <inheritdoc />
    public async Task<WebFetchResponse> FetchAsync(
        Uri uri,
        WebFetchSourceKind sourceKind,
        IReadOnlySet<string> authorizedDirectUrlDigests,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Current;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        WebFetchTransportResponse transport;
        try
        {
            transport = await _transport.GetAsync(uri, sourceKind, authorizedDirectUrlDigests, options, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebFetchException(WebFetchFailureKind.Timeout, "The web fetch exceeded its deadline.", exception);
        }

        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            ValidateContentSignature(transport.Body, transport.CharacterSet);
            var encoding = ResolveEncoding(transport.CharacterSet);
            string source;
            try
            {
                source = encoding.GetString(transport.Body);
            }
            catch (DecoderFallbackException exception)
            {
                throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The textual response contained invalid encoded bytes.", exception);
            }

            deadline.Token.ThrowIfCancellationRequested();
            ValidateDecodedContent(source, transport.MediaType);
            (var text, var title, var truncated) = WebReadableTextExtractor.Extract(
                source,
                transport.MediaType,
                options,
                deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return new WebFetchResponse
            {
                Provenance = new WebFetchProvenance
                {
                    SourceKind = sourceKind,
                    RequestedUrl = WebFetchUrlPolicy.Sanitize(transport.RequestedUri),
                    FinalUrl = WebFetchUrlPolicy.Sanitize(transport.FinalUri),
                    RequestedUrlDigest = WebFetchUrlPolicy.Digest(transport.RequestedUri),
                    FinalUrlDigest = WebFetchUrlPolicy.Digest(transport.FinalUri),
                    RetrievedAt = DateTimeOffset.UtcNow,
                    Redirects = [.. transport.Redirects.Select(hop => new WebFetchRedirect(
                        WebFetchUrlPolicy.Sanitize(hop.From),
                        WebFetchUrlPolicy.Sanitize(hop.To),
                        WebFetchUrlPolicy.SameOrigin(hop.From, hop.To)))],
                },
                MediaType = transport.MediaType,
                CharacterEncoding = encoding.WebName,
                Title = title is { Length: > 300 } ? title[..300] : title,
                Text = text,
                SourceDigest = Digest(transport.Body),
                TextDigest = Digest(Encoding.UTF8.GetBytes(text)),
                CompressedBytes = transport.CompressedBytes,
                DecodedBytes = transport.Body.LongLength,
                ExtractedCharacters = text.Length,
                TrustBoundary = _prompts.Get(PromptFileNames.ToolWebFetchTrustBoundary),
                Truncation = truncated
                    ? new WebFetchTruncation(WebFetchTruncationStage.ExtractedText, "configured readable-text limit")
                    : new WebFetchTruncation(WebFetchTruncationStage.None, null),
            };
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebFetchException(WebFetchFailureKind.Timeout, "The web fetch exceeded its deadline.", exception);
        }
    }

    private static string Digest(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static void ValidateContentSignature(byte[] body, string? charset)
    {
        var normalizedCharset = charset?.Trim(' ', '"').ToLowerInvariant() ?? "utf-8";
        var isUtf16 = normalizedCharset is "utf-16" or "utf-16le" or "utf-16be";
        ReadOnlySpan<byte> bytes = body;
        if (!isUtf16)
        {
            if (bytes.StartsWith((ReadOnlySpan<byte>)[0xef, 0xbb, 0xbf]))
            {
                bytes = bytes[3..];
            }

            while (!bytes.IsEmpty && bytes[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                bytes = bytes[1..];
            }
        }

        var hasBinarySignature = bytes.StartsWith("%PDF-"u8)
            || bytes.StartsWith("PK\u0003\u0004"u8)
            || bytes.StartsWith("PK\u0005\u0006"u8)
            || bytes.StartsWith("PK\u0007\u0008"u8)
            || bytes.StartsWith("GIF87a"u8)
            || bytes.StartsWith("GIF89a"u8)
            || bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0x7f, 0x45, 0x4c, 0x46])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0x4d, 0x5a])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0x1f, 0x8b])
            || bytes.StartsWith("BZh"u8)
            || bytes.StartsWith((ReadOnlySpan<byte>)[0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c])
            || bytes.StartsWith("Rar!"u8)
            || bytes.StartsWith("SQLite format 3\0"u8);
        if (hasBinarySignature)
        {
            throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response body conflicts with its declared textual media type.");
        }

        if (!isUtf16 && body.AsSpan().Contains((byte)0))
        {
            throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response body contains binary content.");
        }
    }

    private static void ValidateDecodedContent(string source, string mediaType)
    {
        if (source.Any(character => char.IsControl(character)
                && character is not '\t' and not '\r' and not '\n'))
        {
            throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response body contains binary-like control data.");
        }

        var content = source.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
        if (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal))
        {
            const string jsonInitialCharacters = "{[\"-0123456789tfn";
            if (content.Length == 0 || !jsonInitialCharacters.Contains(content[0], StringComparison.Ordinal))
            {
                throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response body conflicts with its declared JSON media type.");
            }
        }
        else if (mediaType is "text/html" or "application/xhtml+xml"
            && content.Length > 0
            && content[0] != '<')
        {
            throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response body conflicts with its declared HTML media type.");
        }
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        var normalized = charset?.Trim(' ', '\"').ToLowerInvariant() ?? "utf-8";
        return normalized switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "utf-16" or "utf-16le" => new UnicodeEncoding(false, true, true),
            "utf-16be" => new UnicodeEncoding(true, true, true),
            "us-ascii" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => throw new WebFetchException(WebFetchFailureKind.UnsupportedContent, "The response character encoding is unsupported."),
        };
    }
}
