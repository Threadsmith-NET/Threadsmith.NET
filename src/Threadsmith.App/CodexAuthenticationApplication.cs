namespace Threadsmith.App;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Mcp;
using Threadsmith.Models.OpenAiCodex;

/// <summary>Hosts interactive Codex authentication behind provider-neutral commands.</summary>
internal sealed class CodexAuthenticationApplication :
    ICommandHandler<ManageModelProviderAuthenticationCommand, ModelProviderAuthenticationResult>
{
    private readonly string _credentialPath;
    private readonly string _catalogPath;

    /// <summary>Initializes a new instance of the <see cref="CodexAuthenticationApplication"/> class.</summary>
    internal CodexAuthenticationApplication(ConfigurationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var userDirectory = Path.GetDirectoryName(paths.UserConfiguration)
            ?? throw new InvalidOperationException("The user configuration path has no parent directory.");
        _credentialPath = Path.Combine(userDirectory, "credentials", "openai-codex.json");
        _catalogPath = Path.Combine(userDirectory, "openai-codex-models.json");
    }

    /// <inheritdoc />
    public async Task<ModelProviderAuthenticationResult> HandleAsync(
        ManageModelProviderAuthenticationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.ProviderId, "openai-codex", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelProviderAuthenticationResult(
                command.ProviderId,
                false,
                0,
                false,
                "The requested provider does not expose host-owned authentication.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var oauth = new OpenAiCodexOAuthManager(httpClient, _credentialPath);
        var cache = new OpenAiCodexCatalogCache(_catalogPath);
        switch (command.Action)
        {
            case ModelProviderAuthenticationAction.Status:
                var status = await oauth.GetStatusAsync(cancellationToken);
                var statusMessage = status.IsAuthenticated
                    ? $"OpenAI Codex authenticated; token expires {status.ExpiresAt:O}."
                    : "OpenAI Codex is not authenticated.";
                return new ModelProviderAuthenticationResult(
                    "openai-codex",
                    status.IsAuthenticated,
                    0,
                    false,
                    statusMessage);
            case ModelProviderAuthenticationAction.Logout:
                await oauth.LogoutAsync();
                await cache.ClearAsync();
                return new ModelProviderAuthenticationResult(
                    "openai-codex",
                    false,
                    0,
                    true,
                    "OpenAI Codex authentication and cached model metadata were removed. Restart Threadsmith to rebuild the model selector.");
            case ModelProviderAuthenticationAction.Login:
                var listener = new LoopbackOAuthCallbackListener();
                var reservation = listener.ReserveRedirectUri(1455);
                var redirectUri = new Uri($"http://localhost:{reservation.Port}/auth/callback");
                var challenge = OpenAiCodexOAuthManager.CreateBrowserChallenge(redirectUri);
                await new SystemBrowserLauncher().LaunchAsync(challenge.AuthorizationUri, cancellationToken);
                var callback = await listener.WaitForCallbackAsync(reservation, cancellationToken);
                await oauth.CompleteBrowserAsync(challenge, callback, cancellationToken);
                var accessToken = await oauth.GetAccessTokenAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Codex authentication completed without an access token.");
                var catalog = await new OpenAiCodexCatalogClient(httpClient)
                    .DiscoverAsync(accessToken, cancellationToken: cancellationToken);
                await cache.SaveAsync(catalog, cancellationToken);
                return new ModelProviderAuthenticationResult(
                    "openai-codex",
                    true,
                    catalog.Models.Count,
                    true,
                    $"OpenAI Codex authenticated; discovered {catalog.Models.Count} models. Restart Threadsmith to load them into the model selector.");
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Action, "Unknown authentication action.");
        }
    }
}
