namespace Threadsmith.Mcp;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Normalizes HTTPS authorization-server metadata proxies to their declared canonical issuer.
/// </summary>
/// <remarks>
/// Some deployed MCP servers advertise an HTTPS metadata proxy as their authorization server. The proxy document
/// then declares the canonical issuer and may advertise a proxy-owned dynamic-registration endpoint. Strict OAuth
/// clients reject that location/issuer mismatch. This handler resolves one bounded HTTPS metadata hop, substitutes
/// the declared issuer in protected-resource metadata, and serves the same validated metadata document at the
/// canonical well-known location. It never follows a second metadata delegation and never records request bodies,
/// tokens, authorization codes, or client credentials.
/// </remarks>
internal sealed class McpOAuthMetadataCompatibilityHandler : DelegatingHandler
{
    private const int MaximumAuthorizationServers = 4;
    private const int MaximumMetadataBytes = 64 * 1024;
    private readonly ConcurrentDictionary<string, byte[]> _metadataByCanonicalLocation =
        new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="McpOAuthMetadataCompatibilityHandler"/> class.</summary>
    internal McpOAuthMetadataCompatibilityHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get
            && request.RequestUri is { } requestedUri
            && _metadataByCanonicalLocation.TryGetValue(requestedUri.AbsoluteUri, out var cachedMetadata))
        {
            return CreateMetadataResponse(request, cachedMetadata);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode
            || request.Method != HttpMethod.Get
            || request.RequestUri is not { } requestUri
            || !IsProtectedResourceMetadataPath(requestUri.AbsolutePath))
        {
            return response;
        }

        return await NormalizeProtectedResourceMetadataAsync(
            request,
            response,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> NormalizeProtectedResourceMetadataAsync(
        HttpRequestMessage sourceRequest,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var protectedResourceMetadata = await ReadMetadataAsync(response.Content, cancellationToken);
        if (protectedResourceMetadata["authorization_servers"] is not JsonArray authorizationServers
            || authorizationServers.Count == 0)
        {
            return response;
        }

        if (authorizationServers.Count > MaximumAuthorizationServers)
        {
            throw new InvalidDataException(
                $"MCP protected-resource metadata advertises more than {MaximumAuthorizationServers} authorization servers.");
        }

        var canonicalServers = new JsonArray();
        bool changed = false;
        foreach (JsonNode? value in authorizationServers)
        {
            if (value is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var advertisedValue)
                || !TryCreateSecureIssuer(advertisedValue, out var advertisedServer))
            {
                throw new InvalidDataException(
                    "MCP protected-resource metadata contains an invalid authorization-server issuer.");
            }

            var metadata = await ResolveAuthorizationServerMetadataAsync(
                sourceRequest,
                advertisedServer,
                cancellationToken);
            if (metadata is null)
            {
                canonicalServers.Add(advertisedServer.AbsoluteUri);
                continue;
            }

            var (canonicalIssuer, content) = metadata.Value;
            canonicalServers.Add(canonicalIssuer.AbsoluteUri);
            changed |= !string.Equals(
                advertisedServer.AbsoluteUri,
                canonicalIssuer.AbsoluteUri,
                StringComparison.Ordinal);
            _metadataByCanonicalLocation[BuildOAuthMetadataUri(canonicalIssuer).AbsoluteUri] = content;
        }

        if (changed)
        {
            protectedResourceMetadata["authorization_servers"] = canonicalServers;
            ReplaceContent(response, JsonSerializer.SerializeToUtf8Bytes(protectedResourceMetadata));
        }

        return response;
    }

    private async Task<(Uri Issuer, byte[] Content)?> ResolveAuthorizationServerMetadataAsync(
        HttpRequestMessage sourceRequest,
        Uri advertisedServer,
        CancellationToken cancellationToken)
    {
        foreach (Uri metadataUri in BuildMetadataCandidates(advertisedServer))
        {
            using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUri);
            foreach (ProductInfoHeaderValue product in sourceRequest.Headers.UserAgent)
            {
                metadataRequest.Headers.UserAgent.Add(product);
            }

            metadataRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var metadataResponse = await base.SendAsync(metadataRequest, cancellationToken);
            if (!metadataResponse.IsSuccessStatusCode)
            {
                continue;
            }

            var metadata = await ReadMetadataAsync(metadataResponse.Content, cancellationToken);
            if (!TryCreateSecureIssuer(metadata["issuer"]?.GetValue<string>(), out var canonicalIssuer))
            {
                throw new InvalidDataException(
                    "MCP authorization-server metadata contains an invalid canonical issuer.");
            }

            return (canonicalIssuer, JsonSerializer.SerializeToUtf8Bytes(metadata));
        }

        return null;
    }

    private static IEnumerable<Uri> BuildMetadataCandidates(Uri authorizationServer)
    {
        var authority = authorizationServer.GetLeftPart(UriPartial.Authority);
        var trimmedPath = authorizationServer.AbsolutePath.Trim('/');
        if (trimmedPath.Length == 0)
        {
            yield return new Uri($"{authority}/.well-known/oauth-authorization-server", UriKind.Absolute);
            yield return new Uri($"{authority}/.well-known/openid-configuration", UriKind.Absolute);
            yield break;
        }

        yield return new Uri(
            $"{authority}/.well-known/oauth-authorization-server/{trimmedPath}",
            UriKind.Absolute);
        yield return new Uri(
            $"{authority}/.well-known/openid-configuration/{trimmedPath}",
            UriKind.Absolute);
        yield return new Uri(
            $"{authority}/{trimmedPath}/.well-known/openid-configuration",
            UriKind.Absolute);
    }

    private static Uri BuildOAuthMetadataUri(Uri issuer)
    {
        return BuildMetadataCandidates(issuer).First();
    }

    private static bool IsProtectedResourceMetadataPath(string absolutePath)
    {
        return absolutePath.StartsWith(
            "/.well-known/oauth-protected-resource",
            StringComparison.Ordinal);
    }

    private static async Task<JsonObject> ReadMetadataAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await content.LoadIntoBufferAsync(MaximumMetadataBytes, cancellationToken);
        return JsonNode.Parse(await content.ReadAsByteArrayAsync(cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("MCP OAuth metadata is not a JSON object.");
    }

    private static HttpResponseMessage CreateMetadataResponse(
        HttpRequestMessage request,
        byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = CreateJsonContent(content),
        };
        return response;
    }

    private static void ReplaceContent(HttpResponseMessage response, byte[] content)
    {
        var previous = response.Content;
        response.Content = CreateJsonContent(content);
        previous.Dispose();
    }

    private static ByteArrayContent CreateJsonContent(byte[] content)
    {
        var replacement = new ByteArrayContent(content);
        replacement.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return replacement;
    }

    private static bool TryCreateSecureIssuer(
        string? value,
        [NotNullWhen(true)] out Uri? issuer)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out issuer)
            && issuer.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(issuer.UserInfo)
            && string.IsNullOrEmpty(issuer.Query)
            && string.IsNullOrEmpty(issuer.Fragment);
    }
}
