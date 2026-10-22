namespace Threadsmith.App;

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Reads authority before ordinary merging loses presence and source identity.</summary>
internal static class SecurityPolicyConfigurationLoader
{
    /// <summary>Builds a detached trusted adapter view with whole-array replacement.</summary>
    internal static IConfigurationRoot BuildTrusted(ConfigurationPaths paths)
    {
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { ReadFile(paths.MachineConfiguration, "Machine", []), ReadFile(paths.UserConfiguration, "User", []) })
        {
            foreach (var (key, node) in source.Values.Where(item => item.Value.ValueKind == JsonValueKind.Array))
            {
                RemoveSubtree(key);
                Flatten(node, key);
            }

            foreach (var (key, node) in source.Values.Where(item => item.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array))
            {
                RemoveSubtree(key);
                Flatten(node, key);
            }
        }

        var environment = new ConfigurationBuilder().Add(new NonSecretEnvironmentVariablesConfigurationSource("THREADSMITH_")).Build();
        var environmentValues = environment.AsEnumerable().Where(pair => pair.Value is not null).ToArray();
        foreach (var key in environmentValues.Select(pair => pair.Key))
        {
            var segments = key.Split(':');
            var firstIndex = Array.FindIndex(segments, segment => int.TryParse(segment, out _));
            if (firstIndex >= 0)
            {
                RemoveSubtree(string.Join(':', segments.Take(firstIndex)));
            }
        }

        foreach (var (key, value) in environmentValues)
        {
            merged[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(merged).Build();

        void RemoveSubtree(string key)
        {
            foreach (var prior in merged.Keys.Where(prior => prior.Equals(key, StringComparison.OrdinalIgnoreCase) || prior.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                merged.Remove(prior);
            }
        }

        void Flatten(JsonElement node, string key)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                merged[key] = null;
                foreach (var item in node.EnumerateArray())
                {
                    Flatten(item, key + ":" + (index++).ToString(CultureInfo.InvariantCulture));
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in node.EnumerateObject())
                {
                    Flatten(property.Value, key + ":" + property.Name);
                }
            }
            else
            {
                merged[key] = node.ValueKind == JsonValueKind.Null ? null : node.ToString();
            }
        }
    }

    private static readonly string[] ListKeys =
    [
        "tools:allowedExecutables", "tools:allowedNetworkHosts", "tools:allow", "tools:deny",
        "tools:requireApproval", "tools:writeFile:allowedFolders", "editableRoots", "prohibitedPaths",
    ];

    private static readonly string[] BooleanKeys =
    [
        "tools:runProcess:requireApproval", "tools:parallel:enabled",
    ];

    private static readonly string[] ScalarKeys =
    [
        .. BooleanKeys,
        "mutation:approvalPolicy", "mutation:largeDiffThreshold",
        "tools:config:csharp_script:allowed_assemblies",
        "repository:configurationBytes",
    ];

    private static readonly string[] TrustedOnlyKeys =
    [
        "repository:configurationBytes", "secretResolution:limits", "limits:policyStores", "context:deployedPrompts:limits",
        "mcp:profiles", "mcp:limits", "mcp:maximumConcurrentConnections", "mcp:defaultDrainKillTimeoutSeconds",
        "webSearch:provider", "nuget:advisorySources", "hooks:handlers", "hooks:managedGrants", "hooks:limits",
        "skills:trustedSigners", "skills:allowlistedPackages", "skills:enabledSelectors", "skills:revokedDigests",
        "skills:deniedSkillIds", "skills:deniedPublishers", "skills:revokedSigners", "skills:organizationCatalogPath",
        "agents:delegation", "agents:queueCapacity", "agents:maxActiveGlobal", "agents:maxActivePerParent",
        "agents:maxActiveImplementers", "agents:shutdownTimeoutSeconds", "agents:delegateAgents",
    ];

    private static readonly Dictionary<string, int> ResourceDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tools:runProcess:defaultTimeoutSeconds"] = 30,
        ["tools:runProcess:maxTimeoutSeconds"] = 60,
        ["tools:config:csharp_script:timeout_ms"] = 5000,
        ["tools:config:csharp_script:max_output_bytes"] = 65536,
        ["tools:config:csharp_script:max_code_characters"] = 65536,
        ["tools:config:csharp_script:max_assemblies"] = 32,
        ["tools:config:csharp_script:max_assembly_name_characters"] = 128,
        ["tools:config:csharp_script:max_assembly_setting_characters"] = 4096,
        ["tools:runtime:defaults:timeoutMilliseconds"] = -1,
        ["tools:runtime:defaults:maximumOutputBytes"] = int.MaxValue,
        ["tools:runtime:defaults:maximumSourceConcurrency"] = int.MaxValue,
        ["tools:parallel:maximumConcurrency"] = 4,
        ["webFetch:maximumUrlCharacters"] = 2048,
        ["webFetch:maximumRedirects"] = 3,
        ["webFetch:timeoutSeconds"] = 15,
        ["webFetch:maximumCompressedBytes"] = 1048576,
        ["webFetch:maximumDecodedBytes"] = 2097152,
        ["webFetch:maximumExtractedCharacters"] = 131072,
        ["webFetch:maximumHtmlTokens"] = 100000,
        ["webFetch:maximumHtmlDepth"] = 128,
        ["webFetch:maximumJsonTokens"] = 100000,
        ["webFetch:maximumJsonDepth"] = 64,
        ["webFetch:maximumStringCharacters"] = 131072,
        ["webFetch:maximumScannedUserCharacters"] = 32768,
        ["webFetch:maximumUserUrlCandidates"] = 8,
        ["webFetch:maximumReferences"] = 100,
        ["webFetch:referenceLifetimeSeconds"] = 600,
    };

    /// <summary>Resolves a detached candidate without changing any live authority or settings.</summary>
    internal static EffectiveSessionPolicy Load(ConfigurationPaths paths, Guid bindingId, long generation)
    {
        var sources = new List<PolicyInputSource>();
        var diagnostics = new List<string>();
        var trusted = new[]
        {
            ReadFile(paths.MachineConfiguration, "Machine", sources),
            ReadFile(paths.UserConfiguration, "User", sources),
            ReadEnvironment(sources),
        };
        var maximumConfigurationBytes = 1024L * 1024;
        foreach (var source in trusted)
        {
            if (source.Values.TryGetValue("repository:configurationBytes", out var size))
            {
                if (!long.TryParse(size.ToString(), CultureInfo.InvariantCulture, out maximumConfigurationBytes) || maximumConfigurationBytes <= 0)
                {
                    throw Invalid(source, "repository:configurationBytes");
                }
            }
        }

        var restrictions = new[]
        {
            ReadFile(paths.RepositoryConfiguration, "Repository", sources, maximumConfigurationBytes),
            ReadFile(paths.SessionConfiguration, "RepositorySession", sources, maximumConfigurationBytes),
        };
        foreach (var source in restrictions)
        {
            foreach (var key in TrustedOnlyKeys.Where(source.Values.ContainsKey))
            {
                diagnostics.Add($"PolicySourceNotAllowed: {key}, {source.Kind} '{source.Location}', generation {generation}: ignored; this setting accepts only designated trusted configuration. Review the user configuration manually if this authority is intended.");
            }
        }

        var repository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.RepositoryRoot));
        var limits = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, defaultValue) in ResourceDefaults)
        {
            var ceiling = defaultValue;
            foreach (var source in trusted.Concat(restrictions))
            {
                if (!source.Values.TryGetValue(key, out var node))
                {
                    continue;
                }

                if (!int.TryParse(node.ToString(), CultureInfo.InvariantCulture, out var parsed)
                    || (parsed <= 0 && !(parsed == -1 && key.EndsWith(":timeoutMilliseconds", StringComparison.OrdinalIgnoreCase))
                        && !(parsed == 0 && key == "webFetch:maximumRedirects")))
                {
                    throw Invalid(source, key);
                }

                ceiling = source.Kind is "Repository" or "RepositorySession" ? Narrow(ceiling, parsed) : parsed;
            }

            limits[key] = ceiling;
        }

        limits["tools:runProcess:defaultTimeoutSeconds"] = Math.Min(limits["tools:runProcess:defaultTimeoutSeconds"], limits["tools:runProcess:maxTimeoutSeconds"]);
        ResolveRuntimeOverrides(trusted, restrictions, limits, sources);
        string[] assemblies = ["System.Linq", "System.Collections", "System.Collections.Generic"];
        foreach (var source in trusted.Concat(restrictions))
        {
            const string key = "tools:config:csharp_script:allowed_assemblies";
            if (!source.Values.TryGetValue(key, out var node))
            {
                continue;
            }

            if (node.ValueKind != JsonValueKind.String)
            {
                throw Invalid(source, key);
            }

            var requested = (node.GetString() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (requested.Any(name => name.IndexOfAny(['*', '?', '/', '\\', ':']) >= 0))
            {
                throw Invalid(source, key);
            }

            assemblies = source.Kind is "Repository" or "RepositorySession"
                ? [.. assemblies.Intersect(requested, StringComparer.OrdinalIgnoreCase)] : requested;
        }

        ImmutableArray<string> ResolveList(string key, string[]? defaults, bool pathsAreRoots = false)
        {
            string[]? value = defaults;
            foreach (var source in trusted)
            {
                if (ReadList(source, key) is { } configured)
                {
                    value = configured;
                }
            }

            if (pathsAreRoots && value is not null)
            {
                value = NormalizeRoots(value, repository);
            }

            foreach (var source in restrictions)
            {
                if (ReadList(source, key) is not { } filter)
                {
                    continue;
                }

                if (pathsAreRoots)
                {
                    filter = NormalizeRoots(filter, repository);
                }

                var next = value is null ? filter : pathsAreRoots
                    ? IntersectRoots(value, filter)
                    : [.. value.Intersect(filter, StringComparer.OrdinalIgnoreCase)];
                if (filter.Any(item => !next.Contains(item, StringComparer.OrdinalIgnoreCase)))
                {
                    diagnostics.Add($"PolicyWideningRejected: {key}, {source.Kind} '{source.Location}', generation {generation}: only the intersection is effective. Review the filter or deliberately configure the trusted user policy.");
                }

                value = next;
            }

            var comparer = pathsAreRoots ? RepositoryPathPolicy.GetPathComparer(repository) : StringComparer.OrdinalIgnoreCase;
            return value is null ? default : [.. value.Distinct(comparer).Order(comparer)];
        }

        ImmutableArray<string> Union(string key, StringComparer comparer) =>
            [.. trusted.Concat(restrictions).SelectMany(source => ReadList(source, key) ?? [])
                .Distinct(comparer).Order(comparer)];

        var executables = ResolveList("tools:allowedExecutables", [OperatingSystem.IsWindows() ? "powershell" : "bash", "dotnet", "git"]);
        if (executables.Any(name => name.IndexOfAny(['/', '\\', '*', '?', ':']) >= 0 || name.Any(char.IsWhiteSpace)))
        {
            throw new InvalidDataException("PolicyValueInvalid: tools:allowedExecutables requires exact bare executable names; correct the indicated policy input.");
        }

        var requireShellApproval = true;
        var parallelEnabled = true;
        var mutationCeiling = MutationApprovalPolicy.ReviewAll;
        var threshold = 500;
        foreach (var source in trusted)
        {
            if (source.Values.TryGetValue("tools:parallel:enabled", out var parallel))
            {
                parallelEnabled = ReadBoolean(parallel, source, "tools:parallel:enabled");
            }

            if (source.Values.TryGetValue("tools:runProcess:requireApproval", out var approval))
            {
                requireShellApproval = ReadBoolean(approval, source, "tools:runProcess:requireApproval");
            }

            if (source.Values.TryGetValue("mutation:approvalPolicy", out var mode))
            {
                if (mode.ValueKind != JsonValueKind.String || !Enum.TryParse(mode.GetString(), true, out mutationCeiling)
                    || !Enum.IsDefined(mutationCeiling))
                {
                    throw Invalid(source, "mutation:approvalPolicy");
                }

                if (mutationCeiling == MutationApprovalPolicy.TrustPlan)
                {
                    mutationCeiling = MutationApprovalPolicy.ReviewAll;
                }
            }

            threshold = ReadThreshold(source, threshold);
        }

        foreach (var source in restrictions)
        {
            if (source.Values.TryGetValue("tools:parallel:enabled", out var parallel))
            {
                parallelEnabled &= ReadBoolean(parallel, source, "tools:parallel:enabled");
            }

            if (source.Values.TryGetValue("tools:runProcess:requireApproval", out var approval))
            {
                requireShellApproval |= ReadBoolean(approval, source, "tools:runProcess:requireApproval");
            }

            threshold = Math.Min(threshold, ReadThreshold(source, threshold));
        }

        // Static preauthorization and mandatory outbound restrictions are deliberately separate.
        string[] hosts = [];
        foreach (var source in trusted)
        {
            hosts = ReadList(source, "tools:allowedNetworkHosts") ?? hosts;
        }

        string[]? hostFilter = null;
        foreach (var source in restrictions)
        {
            if (ReadList(source, "tools:allowedNetworkHosts") is { } filter)
            {
                hostFilter = hostFilter is null ? filter : [.. hostFilter.Intersect(filter, StringComparer.OrdinalIgnoreCase)];
            }
        }

        if (hosts.Concat(hostFilter ?? []).Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown))
        {
            throw new InvalidDataException("PolicyValueInvalid: tools:allowedNetworkHosts requires exact hostnames, without URL paths or wildcard grants.");
        }

        var policy = new EffectiveSessionPolicy
        {
            BindingId = bindingId,
            Generation = generation,
            RepositoryPath = repository,
            Fingerprint = string.Empty,
            StartupAuthorityFingerprint = CaptureStartupAuthority(paths, restrictions),
            AllowedExecutables = executables,
            AllowedTools = ResolveList("tools:allow", null),
            DeniedTools = Union("tools:deny", StringComparer.OrdinalIgnoreCase),
            ReviewTools = Union("tools:requireApproval", StringComparer.OrdinalIgnoreCase),
            WriteFolders = ResolveList("tools:writeFile:allowedFolders", [".inbox"], pathsAreRoots: true),
            EditableRoots = ResolveList("editableRoots", ["."], pathsAreRoots: true),
            ProhibitedPaths = Union("prohibitedPaths", StringComparer.Ordinal),
            AllowedNetworkHosts = [.. hosts.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            NetworkHostRestriction = hostFilter is null ? default : [.. hostFilter.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            RequireShellApproval = requireShellApproval,
            ParallelToolsEnabled = parallelEnabled,
            MutationCeiling = mutationCeiling,
            LargeDiffThreshold = threshold,
            ResourceLimits = limits.ToImmutable(),
            ScriptAssemblies = [.. assemblies.Order(StringComparer.OrdinalIgnoreCase)],
            ControlPaths = [paths.MachineConfiguration, paths.UserConfiguration, paths.RepositoryConfiguration,
                paths.SessionConfiguration, paths.UserProviderCatalog, paths.RepositoryProviderCatalog,
                Path.Combine(paths.RepositoryConfigurationDirectory, "extensions.json"),
                paths.SecretsConfiguration, paths.UserSecretsConfiguration,
                Path.Combine(Path.GetDirectoryName(paths.UserConfiguration) ?? repository, "mcp-tool-approvals.json"),
                Path.Combine(Path.GetDirectoryName(paths.UserConfiguration) ?? repository, "skill-policy.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "outbound-consent.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db-wal"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db-shm")],
            Sources = [.. sources],
            Diagnostics = [.. diagnostics.Distinct(StringComparer.Ordinal)],
        };
        var normalized = string.Join('\n', new[]
        {
            repository, string.Join('|', policy.AllowedExecutables),
            policy.StartupAuthorityFingerprint,
            policy.AllowedTools.IsDefault ? "unrestricted" : string.Join('|', policy.AllowedTools),
            string.Join('|', policy.DeniedTools), string.Join('|', policy.ReviewTools),
            string.Join('|', policy.WriteFolders), string.Join('|', policy.EditableRoots),
            string.Join('|', policy.ProhibitedPaths), string.Join('|', policy.AllowedNetworkHosts),
            policy.NetworkHostRestriction.IsDefault ? "unrestricted" : string.Join('|', policy.NetworkHostRestriction),
            requireShellApproval.ToString(), parallelEnabled.ToString(), mutationCeiling.ToString(), threshold.ToString(CultureInfo.InvariantCulture),
            string.Join('|', policy.ScriptAssemblies),
            string.Join('|', policy.ResourceLimits.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))),
        });
        _ = WebFetchOptions.FromPolicy(policy);
        return policy with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) };
    }

    private static string CaptureStartupAuthority(ConfigurationPaths paths, Source[] restrictions)
    {
        // Existing MCP, hook, skill, provider and secret adapters retain their own validators
        // and live revocation stores. Their immutable definitions are rebuilt only at restart.
        var trusted = BuildTrusted(paths);
        using var lifetime = trusted as IDisposable;
        var keys = TrustedOnlyKeys.Concat(new[]
        {
            "tools:jira:providers", "tools:pr:providers",
            "tools:runtime:defaults", "tools:runtime:byTool", "tools:parallel:enabled", "tools:parallel:maximumConcurrency",
        }).ToArray();
        var entries = trusted.AsEnumerable()
            .Where(pair => keys.Any(key => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                || pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new KeyValuePair<string, string?>(pair.Key.ToUpperInvariant(), pair.Value)).ToArray();
        var requests = restrictions.Select(source => source.Values.TryGetValue("hooks:repositoryHandlers", out var handlers)
            ? JsonSerializer.Serialize(Normalize(handlers)) : string.Empty).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { entries, requests })));

        static object Normalize(JsonElement node) => node.ValueKind switch
        {
            JsonValueKind.Object => node.EnumerateObject().OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(property => property.Name.ToUpperInvariant(), property => Normalize(property.Value), StringComparer.Ordinal),
            JsonValueKind.Array => node.EnumerateArray().Select(Normalize).ToArray(),
            _ => node,
        };
    }

    private static Source ReadFile(string path, string kind, List<PolicyInputSource> provenance, long maximumBytes = int.MaxValue)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        byte[] content;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > maximumBytes || stream.Length > int.MaxValue)
            {
                throw new InvalidDataException($"PolicyValueInvalid: '{path}' exceeds its configuration byte limit.");
            }

            content = new byte[(int)stream.Length];
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException($"PolicyValueInvalid: '{path}' changed during policy loading; retry after the settings write completes.");
            }
        }
        catch (FileNotFoundException)
        {
            return new Source(kind, path, values);
        }
        catch (DirectoryNotFoundException)
        {
            return new Source(kind, path, values);
        }

        var json = content.AsMemory();
        if (json.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            json = json[3..];
        }

        using var document = JsonDocument.Parse(json, RepositorySettingsCoordinator.DocumentOptions);
        void Visit(JsonElement node, string prefix)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"PolicyValueInvalid: '{path}' must contain configuration objects.");
            }

            foreach (var property in node.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
                if (!values.TryAdd(key, property.Value))
                {
                    throw new InvalidDataException($"PolicyValueInvalid: duplicate key '{key}' in '{path}'.");
                }

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    Visit(property.Value, key);
                }
                else if (ListKeys.Concat(ScalarKeys).Concat(ResourceDefaults.Keys).Any(known => known.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"PolicyValueInvalid: '{key}' in '{path}' must be an object containing policy settings.");
                }
            }
        }

        Visit(document.RootElement.Clone(), string.Empty);
        foreach (var key in ListKeys.Concat(ScalarKeys).Concat(ResourceDefaults.Keys).Concat(TrustedOnlyKeys).Distinct(StringComparer.OrdinalIgnoreCase).Where(values.ContainsKey))
        {
            provenance.Add(new PolicyInputSource(key, kind, path));
        }

        return new Source(kind, path, values);
    }

    private static Source ReadEnvironment(List<PolicyInputSource> provenance)
    {
        // Only these exact existing launch inputs are authority; arbitrary new environment keys are not grants.
        var configuration = new ConfigurationBuilder().Add(new NonSecretEnvironmentVariablesConfigurationSource("THREADSMITH_")).Build();
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var runtimeOverrides = configuration.GetSection("tools:runtime:byTool").GetChildren().ToArray();
        if (runtimeOverrides.Length > 0)
        {
            values["tools:runtime:byTool"] = JsonSerializer.SerializeToElement(runtimeOverrides.Select(item =>
                item.GetChildren().ToDictionary(field => field.Key, field => field.Value, StringComparer.OrdinalIgnoreCase)).ToArray());
            provenance.Add(new PolicyInputSource("tools:runtime:byTool", "Environment", "THREADSMITH_ launch environment"));
        }

        foreach (var key in ListKeys.Concat(ScalarKeys).Concat(ResourceDefaults.Keys))
        {
            var section = configuration.GetSection(key);
            var children = section.GetChildren().ToArray();
            if (children.Length > 0)
            {
                if (!ListKeys.Contains(key) || children.Any(child => !int.TryParse(child.Key, out _)))
                {
                    throw new InvalidDataException($"PolicyValueInvalid: environment '{key}' must use indexed list entries.");
                }

                values[key] = JsonSerializer.SerializeToElement(children.Select(child => child.Value).ToArray());
            }
            else if (section.Value is { } scalar)
            {
                values[key] = BooleanKeys.Contains(key, StringComparer.OrdinalIgnoreCase) && bool.TryParse(scalar, out var boolean)
                    ? JsonSerializer.SerializeToElement(boolean) : JsonSerializer.SerializeToElement(scalar);
            }
            else
            {
                continue;
            }

            provenance.Add(new PolicyInputSource(key, "Environment", "THREADSMITH_ launch environment"));
        }

        return new Source("Environment", "THREADSMITH_ launch environment", values);
    }

    private static string[]? ReadList(Source source, string key)
    {
        if (!source.Values.TryGetValue(key, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.Array || node.EnumerateArray().Any(item =>
            item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
            || (item.GetString() ?? string.Empty).Any(char.IsControl)))
        {
            throw Invalid(source, key);
        }

        return [.. node.EnumerateArray().Select(item => item.GetString() ?? string.Empty)];
    }

    private static bool ReadBoolean(JsonElement value, Source source, string key) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(source, key);

    private static int ReadThreshold(Source source, int fallback)
    {
        const string key = "mutation:largeDiffThreshold";
        if (!source.Values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed : throw Invalid(source, key);
    }

    private static InvalidDataException Invalid(Source source, string key) =>
        new($"PolicyValueInvalid: {key}, {source.Kind} '{source.Location}': invalid authority input; correct this setting before opening the repository.");

    private static int Narrow(int ceiling, int restriction) => ceiling == -1 ? restriction : restriction == -1 ? ceiling : Math.Min(ceiling, restriction);

    private static void ResolveRuntimeOverrides(Source[] trusted, Source[] restrictions, ImmutableDictionary<string, int>.Builder limits, List<PolicyInputSource> provenance)
    {
        const string key = "tools:runtime:byTool";
        foreach (var source in trusted.Concat(restrictions))
        {
            if (!source.Values.TryGetValue(key, out var array))
            {
                continue;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                throw Invalid(source, key);
            }

            var isRestriction = source.Kind is "Repository" or "RepositorySession";
            if (!isRestriction)
            {
                foreach (var previous in limits.Keys.Where(name => name.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    limits.Remove(previous);
                }
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid(source, key);
                }

                var fields = item.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
                if (!fields.TryGetValue("toolId", out var id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString() ?? string.Empty))
                {
                    throw Invalid(source, key);
                }

                foreach (var (name, value) in fields.Where(field => !field.Key.Equals("toolId", StringComparison.OrdinalIgnoreCase)))
                {
                    var defaultKey = "tools:runtime:defaults:" + name;
                    if (!ResourceDefaults.ContainsKey(defaultKey) || !int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var parsed)
                        || (parsed <= 0 && !(parsed == -1 && name.Equals("timeoutMilliseconds", StringComparison.OrdinalIgnoreCase))))
                    {
                        throw Invalid(source, key);
                    }

                    var specific = key + ":" + id.GetString() + ":" + name;
                    limits[specific] = isRestriction ? Narrow(limits.GetValueOrDefault(specific, limits[defaultKey]), parsed) : parsed;
                }
            }

            provenance.Add(new PolicyInputSource(key, source.Kind, source.Location));
        }

        // Repository-wide limits also narrow trusted per-tool exceptions.
        foreach (var source in restrictions)
        {
            foreach (var specific in limits.Keys.Where(name => name.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var defaultKey = "tools:runtime:defaults:" + specific[(specific.LastIndexOf(':') + 1)..];
                if (source.Values.ContainsKey(defaultKey))
                {
                    limits[specific] = Narrow(limits[specific], limits[defaultKey]);
                }
            }
        }
    }

    private static string[] NormalizeRoots(string[] values, string repository) => [.. values.Select(value =>
    {
        if (value.IndexOfAny(['*', '?']) >= 0 || (Path.IsPathRooted(value) && !Path.IsPathFullyQualified(value)))
        {
            throw new InvalidDataException("PolicyValueInvalid: policy roots require literal repository-relative or fully qualified paths.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, repository));
    })];

    private static string[] IntersectRoots(string[] ceiling, string[] filter) => [.. ceiling
        .SelectMany(parent => filter.Select(child => IsWithin(child, parent) ? child : IsWithin(parent, child) ? parent : null))
        .OfType<string>().Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];

    private static bool IsWithin(string candidate, string root)
    {
        return RepositoryPathPolicy.ContainsPath(root, candidate);
    }

    private sealed record Source(string Kind, string Location, Dictionary<string, JsonElement> Values);
}
