namespace Threadsmith.App;

using System.Net;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;

/// <summary>Composes the ordinary PR tool with host-owned HTTP and credential lifetimes.</summary>
internal static class PrFetchComposition
{
    /// <summary>Creates fixed provider registrations; the caller owns and disposes the returned HTTP client.</summary>
    internal static PrFetchTool? Create(IConfiguration configuration, IConfiguration trustedConfiguration, ISecretResolver secrets, IPromptLoader prompts, out HttpClient? client)
    {
        var options = PrFetchOptions.FromConfiguration(configuration, trustedConfiguration);
        client = null;
        if (options.Providers.Count == 0)
        {
            return null;
        }

        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                PublicIpAddressPolicy.EnsureAllPublic(addresses);
                var selected = addresses.OrderBy(address => address.AddressFamily).First();
                return await PublicIpAddressPolicy.ConnectAsync(selected, context.DnsEndPoint.Port, token);
            },
        }) { Timeout = Timeout.InfiniteTimeSpan };
        var tool = new PrFetchTool(
            [new GitHubPullRequestProvider(client, secrets, options), new BitbucketCloudPullRequestProvider(client, secrets, options)],
            options,
            prompts);
        return options.Providers.Values.Any(provider => provider.Enabled) ? tool : null;
    }
}
