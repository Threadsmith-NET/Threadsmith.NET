namespace Threadsmith.App;

using System.Net;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Tools.Jira;

/// <summary>Composes the ordinary Jira tool with host-owned HTTP and credential lifetimes.</summary>
internal static class JiraComposition
{
    /// <summary>Creates a fixed Jira registration; the caller owns and disposes the returned HTTP client.</summary>
    internal static JiraTool? Create(
        IConfiguration configuration,
        IConfiguration trustedConfiguration,
        ISecretResolver secrets,
        IPromptLoader prompts,
        out HttpClient? client)
    {
        var options = JiraOptions.FromConfiguration(configuration, trustedConfiguration);
        client = null;
        if (!options.Providers.Values.Any(provider => provider.Enabled))
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
                return await PublicIpAddressPolicy.ConnectAsync(
                    selected,
                    context.DnsEndPoint.Port,
                    token);
            },
        }) { Timeout = Timeout.InfiniteTimeSpan };
        return new JiraTool(new JiraCloudClient(client, secrets, options), options, prompts);
    }
}
