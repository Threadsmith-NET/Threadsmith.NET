namespace Threadsmith.Models.Anthropic;

using global::Anthropic;
using global::Anthropic.Core;

/// <summary>Creates request-scoped SDK clients without transferring ownership of the host HTTP pool.</summary>
internal static class AnthropicSdkClientFactory
{
    /// <summary>Fixed compiled direct API authority.</summary>
    internal static readonly Uri ApiBaseUri = new("https://api.anthropic.com/");

    /// <summary>Creates an SDK client with explicit, host-authorized connection settings.</summary>
    internal static AnthropicClient Create(
        HttpClient sharedHttpClient,
        string apiKey,
        TimeSpan timeout,
        long maximumResponseBytes = 1048576,
        Action<long>? responseBytesObserved = null,
        Action? submissionObserver = null,
        long maximumSseFrameBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(sharedHttpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var client = new HttpClient(new AnthropicBorrowedHttpHandler(sharedHttpClient, maximumResponseBytes, responseBytesObserved, submissionObserver, maximumSseFrameBytes), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            BaseAddress = ApiBaseUri,
        };
        var options = new ClientOptions
        {
            ApiKey = apiKey,
            AuthToken = null,
            Credentials = null,
            BaseUrl = ApiBaseUri.AbsoluteUri,
            ExtraHeaders = new Dictionary<string, string>(),
            HttpClient = client,
            MaxRetries = 0,
            Timeout = timeout == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : timeout,
        };
        return new AnthropicClient(options);
    }
}
