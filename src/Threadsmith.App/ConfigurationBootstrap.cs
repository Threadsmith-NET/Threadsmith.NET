namespace Threadsmith.App;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;

/// <summary>Resolves configuration locations and builds the bounded normal-layer configuration.</summary>
internal static class ConfigurationBootstrap
{
    private const long _maximumRepositoryConfigurationBytes = 1024 * 1024;

    /// <summary>Resolves normalized machine, user, repository, session, secret, and provider paths.</summary>
    internal static ConfigurationPaths ResolvePaths(string? requestedRepository)
    {
        var repositoryRoot = Path.GetFullPath(requestedRepository ?? Environment.CurrentDirectory);
        var userDirectory = Path.Combine(
            Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".threadsmith");
        var repositoryConfigurationDirectory = Path.Combine(repositoryRoot, ".threadsmith");
        return new ConfigurationPaths
        {
            RepositoryRoot = repositoryRoot,
            MachineConfiguration = Path.Combine(
                Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Threadsmith",
                "config.json"),
            UserConfiguration = Path.Combine(userDirectory, "config.json"),
            RepositoryConfigurationDirectory = repositoryConfigurationDirectory,
            RepositoryConfigurationDirectoryExistedAtStartup = Directory.Exists(
                repositoryConfigurationDirectory),
            RepositoryConfiguration = Path.Combine(repositoryConfigurationDirectory, "config.json"),
            UserProviderCatalog = Path.Combine(userDirectory, "providers.json"),
            RepositoryProviderCatalog = Path.Combine(repositoryConfigurationDirectory, "providers.json"),
            SessionConfiguration = Path.Combine(repositoryConfigurationDirectory, "session.json"),
            SecretsConfiguration = Path.Combine(repositoryConfigurationDirectory, "secrets", "config.json"),
        };
    }

    /// <summary>Formats a concise actionable startup configuration error without exposing a stack trace.</summary>
    internal static string FormatLoadError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (!string.IsNullOrWhiteSpace(message)
                && !messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        return $"Configuration error: {string.Join(" ", messages)} Check the indicated JSON file near the reported line and column.";
    }

    /// <summary>Builds effective configuration after bounding all repository-owned configuration inputs.</summary>
    internal static IConfigurationRoot Build(string[] args, ConfigurationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(paths);

        // The byte cap comes only from trusted machine/user/environment layers. Repository configuration
        // cannot widen the bound that protects itself, its session override, or its separate secret store.
        var trustedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(ConfigurationDocument.Flatten(ConfigurationDocument.Read(paths.MachineConfiguration)))
            .AddInMemoryCollection(ConfigurationDocument.Flatten(ConfigurationDocument.Read(paths.UserConfiguration)))
            .Add(new NonSecretEnvironmentVariablesConfigurationSource("THREADSMITH_"))
            .Build();
        var maximumRepositoryConfigurationBytes = trustedConfiguration.GetValue(
            "repository:configurationBytes",
            _maximumRepositoryConfigurationBytes);
        EnsureRepositoryConfigurationIsBounded(
            paths.RepositoryConfiguration,
            maximumRepositoryConfigurationBytes);
        EnsureRepositoryConfigurationIsBounded(
            paths.SessionConfiguration,
            maximumRepositoryConfigurationBytes);

        // Static secret stores are not inspected here. Their dedicated providers enforce separate
        // bounds, trust, confinement, permission, and Git-index/ignore proof before value reads.
        // --set:key=value participates at the documented CLI layer and never enters model input.
        var commandLineConfiguration = args
            .Where(argument => argument.StartsWith("--set:", StringComparison.Ordinal))
            .Select(argument => argument[6..].Split('=', 2))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            .ToDictionary(parts => parts[0], parts => (string?)parts[1], StringComparer.OrdinalIgnoreCase);
        if (commandLineConfiguration.Keys.Any(ConfigurationPermissionCatalog.IsPermission))
        {
            throw new InvalidDataException("Permission settings cannot be supplied by --set. Use user-owned repository permissions or trusted user/machine configuration.");
        }

        // Keep compiled defaults together so the remaining providers express precedence, not fallback logic.
        return new ConfigurationBuilder()
            .AddInMemoryCollection(CreateCompiledDefaults())
            .AddInMemoryCollection(BuildTrusted(paths).AsEnumerable())
            .Add(new RepositoryConfigurationSource { InitialData = ConfigurationDocument.Flatten(ConfigurationDocument.Read(paths.RepositoryConfiguration, maximumRepositoryConfigurationBytes), projectPreferencesOnly: true) })
            .Add(new RepositoryConfigurationSource { InitialData = ConfigurationDocument.Flatten(ConfigurationDocument.Read(paths.SessionConfiguration, maximumRepositoryConfigurationBytes), projectPreferencesOnly: true) })
            .AddInMemoryCollection(commandLineConfiguration)
            .AddInMemoryCollection(new ConfigurationBuilder()
                .Add(new NonSecretEnvironmentVariablesConfigurationSource("THREADSMITH_"))
                .Build().AsEnumerable().Where(pair => !ConfigurationPermissionCatalog.IsPermission(pair.Key)))
            .Build();
    }

    /// <summary>Builds the configuration view whose values cannot be controlled by the active repository.</summary>
    internal static IConfigurationRoot BuildTrusted(ConfigurationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return SecurityPolicyConfigurationLoader.BuildTrusted(paths);
    }

    /// <summary>Creates the user configuration from the shipped documented example on first launch.</summary>
    internal static async Task ScaffoldUserConfigurationIfMissingAsync(string userConfigurationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userConfigurationPath);
        try
        {
            await RepositorySettingsCoordinator.UpdateAsync(
                userConfigurationPath,
                async token =>
                {
                    if (File.Exists(userConfigurationPath))
                    {
                        return null;
                    }

                    // User configuration is strict JSON; preserve string literals while stripping example comments.
                    var examplePath = Path.Combine(AppContext.BaseDirectory, "config.example");
                    var content = File.Exists(examplePath)
                        ? StripJsonComments(await File.ReadAllTextAsync(examplePath, token))
                        : "{}\n";
                    var root = JsonNode.Parse(content, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                        ?? throw new InvalidDataException("The shipped configuration example must contain an object.");
                    ConfigurationDocument.RemovePermissionProperties(root);
                    return new RepositorySettingsUpdate(JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true })) { Overwrite = false, IsHostInitialization = true };
                },
                cancellationToken: cancellationToken);
        }
        catch (IOException) when (File.Exists(userConfigurationPath))
        {
            // Another process completed first-launch scaffolding; preserve its configuration.
        }
    }

    /// <summary>Returns all compiled defaults used before normal configuration providers apply overrides.</summary>
    private static IReadOnlyDictionary<string, string?> CreateCompiledDefaults()
    {
        return new Dictionary<string, string?>
        {
            ["headless:semanticReadinessTimeoutSeconds"] = "30",
            ["tools:config:memories:MaxNumberOfRepoMemories"] = "20",
            ["tools:config:memories:Recall:MaximumResults"] = "3",
            ["tools:config:memories:Recall:SemanticMinimum"] = RepositoryMemoryOptions.DefaultSemanticMinimum.ToString(CultureInfo.InvariantCulture),
            ["tools:config:memories:Recall:RerankerEnabled"] = "false",
            ["tools:config:memories:Recall:RerankerCandidateLimit"] = "8",
            ["tools:config:memories:standingPreferenceWarningThreshold"] = "3",
            ["reranking:cpuThreads"] = "8",
            ["events:subscriberCapacity"] = "256",
            ["persistence:path"] = ".threadsmith/threadsmith.db",
            ["persistence:artifactDirectory"] = ".threadsmith/artifacts",
            ["persistence:retention:enabled"] = "true",
            ["persistence:retention:sessionAgeDays"] = "30",
            ["persistence:retention:metadataOnly"] = "false",
            ["persistence:redactionAudit:enabled"] = "true",
            ["persistence:redactionAudit:repairArtifacts"] = "true",
            ["diagnostics:enabled"] = "true",
            ["diagnostics:directory"] = ".threadsmith/diagnostics",
            ["diagnostics:includeLogs"] = "true",
            ["diagnostics:includeEvents"] = "true",
            ["diagnostics:includeArtifacts"] = "true",
            ["diagnostics:includeConfiguration"] = "true",
            ["diagnostics:includeVersionInfo"] = "true",
            ["diagnostics:maxBytes"] = (64 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["diagnostics:recentEventsPerSession"] = "1000",
            ["mcp:defaultDrainKillTimeoutSeconds"] = "10",
            ["budget:calls"] = "1000",
            ["budget:wallClockSeconds"] = "3600",
            ["budget:cost"] = "0",
            ["execution:maxModelRounds"] = ExecutionLimits.DefaultMaxModelRounds.ToString(CultureInfo.InvariantCulture),
            ["execution:maxCorrectiveTurns"] = "3",
            ["execution:maxStructuredOutputCharacters"] = (8 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["execution:toolResultPreviewCharacters"] = "4096",
            ["execution:maxModelOutputBatchCharacters"] = "4096",
            ["execution:modelOutputFlushIntervalMilliseconds"] = "50",
            ["model:http:pooledConnectionLifetimeSeconds"] = "900",
            ["model:http:pooledConnectionIdleTimeoutSeconds"] = "120",
            ["model:http:connectTimeoutSeconds"] = "30",
            ["model:http:maxConnectionsPerServer"] = "16",
            ["mutation:approvalPolicy"] = "reviewAll",
            ["mutation:largeDiffThreshold"] = "500",
            ["scratchpad:enabled"] = "false",
            ["tui:showOperationDurations"] = "true",
            ["tools:listFiles:defaultEntries"] = "200",
            ["tools:listFiles:maxEntries"] = "2000",
            ["tools:readFile:maxBytes"] = ToolLimits.DefaultReadFileByteLimit.ToString(CultureInfo.InvariantCulture),
            ["tools:writeFile:allowedFolders:0"] = ".inbox",
            ["tools:readFile:defaultLines"] = ToolLimits.DefaultReadFileLineLimit.ToString(CultureInfo.InvariantCulture),
            ["tools:readFile:maxLines"] = ToolLimits.DefaultReadFileLineLimit.ToString(CultureInfo.InvariantCulture),
            ["tools:readFile:maxContentBytes"] = ToolLimits.DefaultReadFileContentByteLimit.ToString(CultureInfo.InvariantCulture),
            ["tools:search:maxBytes"] = (1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["tools:search:defaultMatches"] = "100",
            ["tools:findSymbol:maxResults"] = "1000",
            ["tools:findReferences:maxResults"] = "1000",
            ["tools:findImplementations:maxResults"] = "1000",
            ["tools:codeExplore:inspectCodeExploreOutput"] = "false",
            ["tools:runProcess:defaultTimeoutSeconds"] = "30",
            ["tools:runProcess:maxTimeoutSeconds"] = "60",
            ["tools:runProcess:requireApproval"] = "true",
            ["tools:runProcess:shellExecutable"] = OperatingSystem.IsWindows() ? "powershell" : "bash",
        };
    }

    /// <summary>Removes comments from the shipped JSON-with-comments example while preserving strings.</summary>
    private static string StripJsonComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var builder = new StringBuilder(source.Length);
        var inString = false;
        var escape = false;
        var inBlockComment = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (inBlockComment)
            {
                if (character == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }

                continue;
            }

            if (inString)
            {
                builder.Append(character);
                if (escape)
                {
                    escape = false;
                }
                else if (character == '\\')
                {
                    escape = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                builder.Append(character);
            }
            else if (character == '/' && next == '/')
            {
                // Preserve the newline after a line comment so surrounding JSON tokens remain separated.
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }
            }
            else if (character == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>Rejects oversized repository-owned configuration before a JSON provider opens it.</summary>
    private static void EnsureRepositoryConfigurationIsBounded(string path, long maximumBytes)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Repository configuration '{path}' exceeds the {maximumBytes}-byte safety limit.");
        }
    }
}

/// <summary>Adds prefixed process settings while keeping static secret values outside ordinary configuration.</summary>
internal sealed class NonSecretEnvironmentVariablesConfigurationSource : IConfigurationSource
{
    private readonly string _prefix;

    /// <summary>Initializes a new instance of the <see cref="NonSecretEnvironmentVariablesConfigurationSource"/> class.</summary>
    internal NonSecretEnvironmentVariablesConfigurationSource(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
    }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new NonSecretEnvironmentVariablesConfigurationProvider(_prefix);
    }
}

/// <summary>Normalizes prefixed environment keys and excludes the complete <c>secrets</c> subtree.</summary>
internal sealed class NonSecretEnvironmentVariablesConfigurationProvider : ConfigurationProvider
{
    private readonly string _prefix;

    /// <summary>Initializes a new instance of the <see cref="NonSecretEnvironmentVariablesConfigurationProvider"/> class.</summary>
    internal NonSecretEnvironmentVariablesConfigurationProvider(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
    }

    /// <inheritdoc />
    public override void Load()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is not string name
                || variable.Value is not string value
                || !name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = name[_prefix.Length..].Replace("__", ":", StringComparison.Ordinal);
            if (string.Equals(key, "secrets", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values[key] = value;
        }

        Data = values;
    }
}

/// <summary>Immutable normalized paths shared by all startup composition phases.</summary>
internal sealed record ConfigurationPaths
{
    /// <summary>Gets the normalized repository root used by every host subsystem.</summary>
    internal required string RepositoryRoot { get; init; }

    /// <summary>Gets the optional machine configuration path.</summary>
    internal required string MachineConfiguration { get; init; }

    /// <summary>Gets the optional user configuration path.</summary>
    internal required string UserConfiguration { get; init; }

    /// <summary>Gets the user-owned secret store shared by policy protection and secret resolution.</summary>
    internal string UserSecretsConfiguration => Path.Combine(
        Path.GetDirectoryName(UserConfiguration)
            ?? throw new InvalidOperationException("The user configuration path has no parent directory."),
        "secrets",
        "config.json");

    /// <summary>Gets the repository-owned Threadsmith configuration directory.</summary>
    internal required string RepositoryConfigurationDirectory { get; init; }

    /// <summary>Gets whether repository configuration existed before startup could scaffold it.</summary>
    internal required bool RepositoryConfigurationDirectoryExistedAtStartup { get; init; }

    /// <summary>Gets the optional repository configuration path.</summary>
    internal required string RepositoryConfiguration { get; init; }

    /// <summary>Gets the optional user provider-catalog path.</summary>
    internal required string UserProviderCatalog { get; init; }

    /// <summary>Gets the optional repository provider-catalog path.</summary>
    internal required string RepositoryProviderCatalog { get; init; }

    /// <summary>Gets the optional session override path.</summary>
    internal required string SessionConfiguration { get; init; }

    /// <summary>Gets the optional repository secrets configuration path.</summary>
    internal required string SecretsConfiguration { get; init; }
}
