namespace Threadsmith.App;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCodex;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Tools;

/// <summary>Composes model catalogs, provider dispatch, startup selection, and the shared HTTP transport.</summary>
internal static class ModelComposition
{
    private static readonly TimeSpan CodexStartupTokenTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Creates model services for legacy composition tests using the former store boundary.</summary>
    internal static Task<ModelServices> CreateAsync(
        IConfiguration configuration,
        ConfigurationPaths paths,
        ConfigurationSecretStore secretStore,
        ILoggerFactory loggerFactory,
        string? rawModelLogPath = null)
    {
        return CreateAsync(configuration, paths, new LegacySecretStoreResolver(secretStore), loggerFactory, rawModelLogPath);
    }

    /// <summary>Creates either configured compiled providers or the deterministic offline provider.</summary>
    internal static async Task<ModelServices> CreateAsync(
        IConfiguration configuration,
        ConfigurationPaths paths,
        ISecretResolver secretResolver,
        ILoggerFactory loggerFactory,
        string? rawModelLogPath = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var validatedRawModelLogPath = await ValidateRawModelLogPathAsync(
            paths.RepositoryRoot,
            rawModelLogPath,
            CancellationToken.None).ConfigureAwait(false);

        // One application-lifetime pool follows Microsoft's HttpClient guidance. Profile cancellation owns
        // request deadlines, while bounded handler settings refresh DNS and constrain connection resources.
        var transportOptions = ModelHttpTransportOptions.Load(configuration);
        var httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = transportOptions.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = transportOptions.PooledConnectionIdleTimeout,
            ConnectTimeout = transportOptions.ConnectTimeout,
            MaxConnectionsPerServer = transportOptions.MaxConnectionsPerServer,
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        OpenAiCodexOAuthManager? codexOAuth = null;
        try
        {
            var openAiRegistration = new OpenAiCompatibleProviderRegistration();
            var codexRegistration = new OpenAiCodexProviderRegistration();
            var registry = new ModelProviderRegistry([openAiRegistration, codexRegistration]);
            var effectiveCatalog = LoadEffectiveCatalog(
                configuration,
                paths,
                openAiRegistration,
                registry,
                loggerFactory);
            if (effectiveCatalog?.Configuration.Providers.Any(
                provider => provider is OpenAiCodexProviderConfiguration) == true)
            {
                throw new InvalidOperationException(
                    "The openai-codex provider is host-owned and cannot be declared by user or repository catalogs.");
            }

            var userDirectory = Path.GetDirectoryName(paths.UserConfiguration)
                ?? throw new InvalidOperationException("The user configuration path has no parent directory.");
            codexOAuth = new OpenAiCodexOAuthManager(
                httpClient,
                Path.Combine(userDirectory, "credentials", "openai-codex.json"));
            var codexCache = new OpenAiCodexCatalogCache(
                Path.Combine(userDirectory, "openai-codex-models.json"));
            using CancellationTokenSource codexStartupToken = new(CodexStartupTokenTimeout);
            var codexAccessToken = await GetOptionalCodexAccessTokenAsync(
                codexOAuth.GetAccessTokenAsync,
                loggerFactory.CreateLogger("Threadsmith.Models.OpenAiCodex.Startup"),
                codexStartupToken.Token).ConfigureAwait(false);
            var codexConfiguration = codexAccessToken is null
                ? null
                : await codexCache.LoadAsync(codexStartupToken.Token).ConfigureAwait(false);
            if (codexConfiguration is not null)
            {
                var baseConfiguration = effectiveCatalog?.Configuration
                    ?? new ModelProviderCatalogConfiguration();
                if (baseConfiguration.Providers.Any(provider => string.Equals(
                    provider.Id,
                    codexConfiguration.Id,
                    StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "The authenticated Codex provider is host-owned and may not be overridden by provider catalogs.");
                }

                ModelProviderConfiguration[] providers = [.. baseConfiguration.Providers, codexConfiguration];
                effectiveCatalog = new EffectiveModelProviderCatalog(
                    baseConfiguration with
                    {
                        Providers = providers,
                        DefaultProviderId = baseConfiguration.DefaultProviderId ?? codexConfiguration.Id,
                        DefaultModelId = baseConfiguration.DefaultModelId ?? codexConfiguration.Models[0].Id,
                    },
                    registry,
                    configuration.GetValue("model:enforceModelEndpointHttps", true));
            }

            var catalog = effectiveCatalog?.ModelCatalog
                ?? ModelProfileConfigurationLoader.Load(configuration);

            // A configured catalog chooses a startup profile through the same host policy used at runtime.
            // An empty catalog deliberately retains the deterministic offline flow for local demonstrations.
            var preferredProfileId = effectiveCatalog?.DefaultModelId;
            var startupProfile = ResolveStartupProfile(configuration, catalog, effectiveCatalog?.DefaultModelId);
            if (startupProfile is not null)
            {
                var isNamedDefault = string.Equals(
                    startupProfile.Name,
                    configuration["model:defaultProfile"],
                    StringComparison.OrdinalIgnoreCase);

                // A policy fallback is display state only; explicit defaults are persistent request preferences.
                preferredProfileId = effectiveCatalog?.DefaultModelId is not null || isNamedDefault
                    ? startupProfile.Id
                    : null;
                var configuredProviders = effectiveCatalog
                    ?? throw new InvalidOperationException("Selectable models require a compiled provider catalog.");
                var preferences = new SessionModelPreferences(
                    startupProfile.Id,
                    startupProfile.DefaultReasoningLevel);
                var activeModels = new ActiveModelSelectionService(
                    configuredProviders,
                    preferences,
                    paths.RepositoryConfiguration);
                return new ModelServices(
                    httpClient,
                    catalog,
                    CreateOptionalLoggingProvider(
                        new ConfiguredModelProvider(
                            httpClient,
                            configuredProviders,
                            async (secretReference, cancellationToken) => string.Equals(
                                secretReference,
                                OpenAiCodexProviderRegistration.OAuthSecretReference,
                                StringComparison.Ordinal)
                                    ? await codexOAuth.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
                                    : await ResolveModelSecretAsync(secretResolver, secretReference, cancellationToken).ConfigureAwait(false),
                            () => preferences.CurrentProfileId,
                            async (secretReference, rejectedSecret, cancellationToken) => string.Equals(
                                secretReference,
                                OpenAiCodexProviderRegistration.OAuthSecretReference,
                                StringComparison.Ordinal)
                                    ? await codexOAuth.RefreshAccessTokenAsync(
                                        rejectedSecret,
                                        cancellationToken).ConfigureAwait(false)
                                    : null),
                        validatedRawModelLogPath),
                    activeModels.Current.Profile,
                    effectiveCatalog?.DefaultModelId,
                    activeModels.Current.Profile.Name,
                    preferences,
                    activeModels,
                    codexOAuth);
            }

            var script = new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn { Text = "Inspecting the request." },
                    new ScriptedTurn
                    {
                        ToolName = "list_files",
                        ArgumentsJson = "{\"path\":\".\",\"maximumEntries\":20}",
                    },
                    new ScriptedTurn
                    {
                        Text = "Scripted session complete.",
                        Usage = new ModelUsage(8, 6),
                    },
                ],
            };
            return new ModelServices(
                httpClient,
                catalog,
                CreateOptionalLoggingProvider(
                    new FakeModelProvider(script, TimeSpan.FromMilliseconds(25)),
                    validatedRawModelLogPath),
                startupProfile: null,
                preferredProfileId: null,
                "Scripted demo (offline)",
                new SessionModelPreferences(),
                activeModels: null);
        }
        catch
        {
            // Ownership transfers only with a successful ModelServices result.
            codexOAuth?.Dispose();
            httpClient.Dispose();
            throw;
        }
    }

    /// <summary>Validates an explicit raw model exchange log path before any request content can be persisted.</summary>
    internal static async Task<string?> ValidateRawModelLogPathAsync(
        string repositoryRoot,
        string? rawModelLogPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawModelLogPath))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var fullRepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var fullLogPath = Path.GetFullPath(rawModelLogPath);
        if (!IsPathInsideDirectory(fullRepositoryRoot, fullLogPath))
        {
            return fullLogPath;
        }

        var relative = Path.GetRelativePath(fullRepositoryRoot, fullLogPath).Replace('\\', '/');
        foreach (var indexPath in GetApplicableIndexPaths(relative))
        {
            var indexed = await RunGitAsync(
                fullRepositoryRoot,
                [
                    "ls-files",
                    "--stage",
                    "--",
                    ":(literal)" + indexPath,
                    ":(exclude,glob)" + indexPath + "/**",
                ],
                cancellationToken).ConfigureAwait(false);
            if (indexed.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Raw model exchange logging was requested for a repository path, "
                    + "but Git index state could not be verified.");
            }

            if (!string.IsNullOrWhiteSpace(indexed.StandardOutput))
            {
                throw new InvalidOperationException(
                    "Raw model exchange logging was requested for a repository path that is tracked or staged. "
                    + "Choose an untracked Git-ignored path such as .inbox/model-exchange.jsonl.");
            }
        }

        var ignored = await RunGitAsync(
            fullRepositoryRoot,
            ["check-ignore", "--no-index", "-q", "--", relative],
            cancellationToken).ConfigureAwait(false);
        if (ignored.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Raw model exchange logging was requested for a repository path that is not effectively Git-ignored. "
                + "Choose an ignored path such as .inbox/model-exchange.jsonl or update .gitignore first.");
        }

        return fullLogPath;
    }

    /// <summary>Recovers failures from the optional bounded Codex startup authentication attempt.</summary>
    internal static async Task<string?> GetOptionalCodexAccessTokenAsync(
        Func<CancellationToken, Task<string?>> getAccessTokenAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(getAccessTokenAsync);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return await getAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or InvalidDataException
            or JsonException
            or OperationCanceledException)
        {
            logger.LogWarning(
                "Optional Codex authentication is unavailable during startup ({FailureType}); "
                + "continuing without Codex models.",
                exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Returns whether the path is inside the directory, including equality.</summary>
    private static bool IsPathInsideDirectory(string directory, string path)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedDirectory, normalizedPath, PathComparison)
            || normalizedPath.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                PathComparison)
            || normalizedPath.StartsWith(
                normalizedDirectory + Path.AltDirectorySeparatorChar,
                PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IEnumerable<string> GetApplicableIndexPaths(string relative)
    {
        var current = relative;
        while (!string.IsNullOrEmpty(current))
        {
            yield return current;
            var separator = current.LastIndexOf('/');
            current = separator > 0 ? current[..separator] : null;
        }
    }

    private static async Task<RawModelLogGitProcessResult> RunGitAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.quotepath=false");
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return new RawModelLogGitProcessResult(-1, string.Empty);
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await standardError.ConfigureAwait(false);
                return new RawModelLogGitProcessResult(
                    process.ExitCode,
                    await standardOutput.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new RawModelLogGitProcessResult(-1, string.Empty);
        }
        catch (InvalidOperationException)
        {
            return new RawModelLogGitProcessResult(-1, string.Empty);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record RawModelLogGitProcessResult(int ExitCode, string StandardOutput);

    /// <summary>Wraps the provider with explicit model exchange diagnostics when requested for this process.</summary>
    private static IModelProvider CreateOptionalLoggingProvider(IModelProvider provider, string? rawModelLogPath)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return string.IsNullOrWhiteSpace(rawModelLogPath)
            ? provider
            : new LoggingModelProvider(provider, new JsonlModelExchangeLog(rawModelLogPath));
    }

    /// <summary>Resolves a typed model credential only at the outbound provider boundary.</summary>
    private static async Task<string?> ResolveModelSecretAsync(
        ISecretResolver secretResolver,
        string secretReference,
        CancellationToken cancellationToken)
    {
        var request = new SecretResolutionRequest
        {
            Reference = SecretReference.Parse(secretReference),
            ComponentId = "models:provider",
            Purpose = "authenticate an outbound configured model request",
            MinimumTrust = SecretProviderTrust.RepositoryOwned,
        };
        var result = await secretResolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Value?.Reveal();
    }

    /// <summary>Loads dedicated catalogs or adapts legacy profiles without allowing ambiguous mixed schemas.</summary>
    private static EffectiveModelProviderCatalog? LoadEffectiveCatalog(
        IConfiguration configuration,
        ConfigurationPaths paths,
        OpenAiCompatibleProviderRegistration openAiRegistration,
        ModelProviderRegistry registry,
        ILoggerFactory loggerFactory)
    {
        var hasProviderCatalog = File.Exists(paths.UserProviderCatalog)
            || File.Exists(paths.RepositoryProviderCatalog);
        var hasLegacyProfiles = configuration.GetSection("model:profiles").GetChildren().Any();
        OpenAiCompatibleProviderRegistration.EnsureConfigurationIsUnambiguous(
            hasProviderCatalog,
            hasLegacyProfiles);
        var enforceHttps = configuration.GetValue("model:enforceModelEndpointHttps", true);
        if (hasProviderCatalog)
        {
            var catalogLogger = loggerFactory.CreateLogger("Threadsmith.Models.ProviderCatalog");
            return ModelProviderConfigurationLoader.Load(
                Path.GetFullPath(paths.UserProviderCatalog),
                Path.GetFullPath(paths.RepositoryProviderCatalog),
                registry,
                limits: null,
                enforceHttps: enforceHttps,
                observeDiagnostic: diagnostic =>
                {
                    if (catalogLogger.IsEnabled(LogLevel.Information))
                    {
                        catalogLogger.LogInformation(
                            "{DiagnosticKind}: {DiagnosticMessage}",
                            diagnostic.Kind,
                            diagnostic.Message);
                    }
                });
        }

        var legacyCatalog = ModelProfileConfigurationLoader.Load(configuration);
        if (legacyCatalog.Profiles.Count == 0)
        {
            return null;
        }

        // Warn once at startup while preserving exact legacy behavior through in-memory adaptation.
        loggerFactory.CreateLogger("Threadsmith.Models.ProviderCatalog").LogWarning(
            "Legacy model:profiles configuration is deprecated; migrate to ~/.threadsmith/providers.json "
            + "and repository .threadsmith/providers.json before a future announced removal milestone.");
        return openAiRegistration.CreateLegacyCatalog(legacyCatalog, enforceHttps);
    }

    /// <summary>Resolves the configured default or host-policy fallback used to initialize session state.</summary>
    private static ModelProfile? ResolveStartupProfile(
        IConfiguration configuration,
        ConfiguredModelCatalog catalog,
        ModelProfileId? preferredProfileId)
    {
        if (catalog.Profiles.Count == 0)
        {
            return null;
        }

        var preferredName = configuration["model:defaultProfile"];
        var profile = preferredProfileId is { } configuredDefault
            ? catalog.Get(configuredDefault)
            : catalog.Profiles.FirstOrDefault(candidate => string.Equals(
                candidate.Name,
                preferredName,
                StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            return profile;
        }

        var selection = new DefaultModelSelectionPolicy(catalog).Resolve(
            new ModelSelectionRequest
            {
                WorkloadClass = WorkloadClass.General,
                RequiredCapabilities = new ModelCapabilitySet { Streaming = true },
            });
        return catalog.Get(selection.ProfileId);
    }
}

/// <summary>Owns model composition results and the application-lifetime HTTP connection pool.</summary>
internal sealed class ModelServices : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDisposable? _additionalResource;

    /// <summary>Initializes a new instance of the <see cref="ModelServices"/> class.</summary>
    internal ModelServices(
        HttpClient httpClient,
        ConfiguredModelCatalog catalog,
        IModelProvider provider,
        ModelProfile? startupProfile,
        ModelProfileId? preferredProfileId,
        string status,
        SessionModelPreferences sessionPreferences,
        ActiveModelSelectionService? activeModels,
        IDisposable? additionalResource = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(sessionPreferences);
        _httpClient = httpClient;
        _additionalResource = additionalResource;
        Catalog = catalog;
        Provider = provider;
        StartupProfile = startupProfile;
        PreferredProfileId = preferredProfileId;
        Status = status;
        SessionPreferences = sessionPreferences;
        ActiveModels = activeModels;
    }

    /// <summary>Gets the immutable host-owned selectable model catalog.</summary>
    internal ConfiguredModelCatalog Catalog { get; }

    /// <summary>Gets the provider used for session model requests.</summary>
    internal IModelProvider Provider { get; }

    /// <summary>Gets the profile used to initialize session preferences, when configured.</summary>
    internal ModelProfile? StartupProfile { get; }

    /// <summary>Gets the stable preferred profile passed to model-aware applications.</summary>
    internal ModelProfileId? PreferredProfileId { get; }

    /// <summary>Gets the sanitized startup status displayed by terminal surfaces.</summary>
    internal string Status { get; }

    /// <summary>Gets shared active model/reasoning preferences.</summary>
    internal SessionModelPreferences SessionPreferences { get; }

    /// <summary>Gets runtime model selection, when configured models are available.</summary>
    internal ActiveModelSelectionService? ActiveModels { get; }

    /// <summary>Disposes the shared HTTP client and its owned sockets handler after all model use completes.</summary>
    public void Dispose()
    {
        _additionalResource?.Dispose();
        _httpClient.Dispose();
    }
}
