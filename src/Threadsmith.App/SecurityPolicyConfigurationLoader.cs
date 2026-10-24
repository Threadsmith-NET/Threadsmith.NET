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
        var trustedSources = ReadTrustedSources(paths, []);
        foreach (var source in trustedSources.Where(source => source.Kind != "Environment"))
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
            if (!ConfigurationPermissionCatalog.Allows(key, PermissionConfigurationSources.Launch))
            {
                throw new InvalidDataException($"Permission '{key}' cannot be defined by the launch environment.");
            }

            merged[key] = value;
        }

        var machine = trustedSources.Single(source => source.Kind == "Machine");
        if (machine.Values.TryGetValue("enforced", out var enforced))
        {
            var values = ConfigurationDocument.Parse(Encoding.UTF8.GetBytes(enforced.GetRawText()));
            foreach (var (key, value) in values.Where(pair => pair.Value.ValueKind != JsonValueKind.Object))
            {
                if (!ConfigurationPermissionCatalog.IsPermission(key) || !ConfigurationPermissionCatalog.Allows(key, PermissionConfigurationSources.Machine))
                {
                    throw new InvalidDataException($"Machine enforced property '{key}' is not a declared permission.");
                }

                RemoveSubtree(key);
                Flatten(value, key);
            }
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
            // The common parser already retains object descendants. Reuse its flattener
            // for a selected subtree too, rather than giving adapters another JSON dialect.
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            Add(node, key);
            foreach (var item in ConfigurationDocument.Flatten(values))
            {
                merged[item.Key] = item.Value;
            }

            void Add(JsonElement value, string path)
            {
                values[path] = value;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        Add(property.Value, path + ":" + property.Name);
                    }
                }
            }
        }
    }

    private static readonly string[] ListKeys =
[.. ConfigurationPermissionCatalog.Properties
        .Where(property => property.Shape == PermissionInputShape.List).Select(property => property.Path)];

    private static readonly string[] BooleanKeys = [.. ConfigurationPermissionCatalog.Properties
        .Where(property => property.Shape == PermissionInputShape.Boolean).Select(property => property.Path)];

    private static readonly string[] ScalarKeys = [.. ConfigurationPermissionCatalog.Properties
        .Where(property => property.Shape is PermissionInputShape.Scalar or PermissionInputShape.Boolean).Select(property => property.Path)];

    private static readonly Dictionary<string, int> ResourceDefaults = ConfigurationPermissionCatalog.Properties
        .Where(property => property.DefaultLimit.HasValue)
        .ToDictionary(property => property.Path, property => property.DefaultLimit.GetValueOrDefault(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a detached candidate without changing any live authority or settings.</summary>
    internal static EffectiveSessionPolicy Load(ConfigurationPaths paths, Guid bindingId, long generation)
    {
        var sources = new List<PolicyInputSource>();
        var diagnostics = new List<string>();
        var trusted = ReadTrustedSources(paths, sources);
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

        var projectSources = new[]
        {
            ReadFile(paths.RepositoryConfiguration, "Repository", sources, maximumConfigurationBytes),
            ReadFile(paths.SessionConfiguration, "RepositorySession", sources, maximumConfigurationBytes),
        };
        var localPermissions = new RepositoryPermissionStore(paths.UserConfiguration).Read(paths.RepositoryRoot);
        var machine = trusted.Single(source => source.Kind == "Machine");
        var restrictions = new List<Source>();

        if (machine.Values.TryGetValue("enforced", out var enforcement))
        {
            var values = ConfigurationDocument.Parse(Encoding.UTF8.GetBytes(enforcement.GetRawText()));
            if (values.Any(pair => !ConfigurationPermissionCatalog.Allows(pair.Key, PermissionConfigurationSources.Machine)
                || (!ConfigurationPermissionCatalog.IsPermission(pair.Key)
                && !(pair.Value.ValueKind == JsonValueKind.Object && ConfigurationPermissionCatalog.Paths.Any(key => key.StartsWith(pair.Key + ":", StringComparison.OrdinalIgnoreCase))))))
            {
                throw new InvalidDataException("Machine enforced settings must contain declared permission properties only.");
            }

            restrictions.Add(new Source("Enforced", paths.MachineConfiguration, values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
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

                ceiling = IsRestriction(source) ? Narrow(ceiling, parsed) : parsed;
            }

            limits[key] = ceiling;
        }

        limits["tools:runProcess:defaultTimeoutSeconds"] = Math.Min(limits["tools:runProcess:defaultTimeoutSeconds"], limits["tools:runProcess:maxTimeoutSeconds"]);
        ResolveRuntimeOverrides(trusted, [.. restrictions], limits, sources);
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

            assemblies = IsRestriction(source)
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

        ImmutableArray<string> Union(string key, StringComparer comparer)
        {
            string[] defaults = [];
            foreach (var source in trusted)
            {
                defaults = ReadList(source, key) ?? defaults;
            }

            return [.. defaults.Concat(restrictions.SelectMany(source => ReadList(source, key) ?? []))
                .Distinct(comparer).Order(comparer)];
        }

        var executables = ResolveList("tools:allowedExecutables", [OperatingSystem.IsWindows() ? "powershell" : "bash", "dotnet", "git"]);
        if (executables.Any(name => name.IndexOfAny(['/', '\\', '*', '?', ':']) >= 0 || name.Any(char.IsWhiteSpace)))
        {
            throw new InvalidDataException("PolicyValueInvalid: tools:allowedExecutables requires exact bare executable names; correct the indicated policy input.");
        }

        var requireShellApproval = true;
        var parallelEnabled = true;

        var mutationCeiling = MutationApprovalPolicy.AlwaysTrustRepo;
        var mutationDefault = MutationApprovalPolicy.ReviewAll;
        var hasExplicitMutationCeiling = false;
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
                if (mode.ValueKind != JsonValueKind.String || !Enum.TryParse(mode.GetString(), true, out mutationDefault)
                    || !Enum.IsDefined(mutationDefault))
                {
                    throw Invalid(source, "mutation:approvalPolicy");
                }

                if (mutationDefault is MutationApprovalPolicy.TrustPlan or MutationApprovalPolicy.TrustSession)
                {
                    mutationDefault = MutationApprovalPolicy.ReviewAll;
                }

                if (source.Kind == "Environment")
                {
                    mutationCeiling = RestrictMutation(mutationCeiling, mutationDefault);
                    hasExplicitMutationCeiling = true;
                }
            }

            threshold = ReadThreshold(source, threshold);
        }

        foreach (var source in restrictions)
        {
            if (source.Values.TryGetValue("mutation:approvalPolicy", out var mode))
            {
                if (mode.ValueKind != JsonValueKind.String || !Enum.TryParse<MutationApprovalPolicy>(mode.GetString(), true, out var restriction) || !Enum.IsDefined(restriction))
                {
                    throw Invalid(source, "mutation:approvalPolicy");
                }

                mutationCeiling = RestrictMutation(mutationCeiling, restriction);
                hasExplicitMutationCeiling = true;
            }

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

        var editableRoots = ResolveList("editableRoots", ["."], pathsAreRoots: true);
        if (!hasExplicitMutationCeiling)
        {
            mutationCeiling = MutationApprovalPolicy.ReviewAll;
        }

        var policy = new EffectiveSessionPolicy
        {
            BindingId = bindingId,
            Generation = generation,
            RepositoryPath = repository,
            Fingerprint = string.Empty,
            StartupAuthorityFingerprint = CaptureStartupAuthority(paths, projectSources),
            AllowedExecutables = executables,
            AllowedTools = ResolveList("tools:allow", null),
            DeniedTools = Union("tools:deny", StringComparer.OrdinalIgnoreCase),
            ReviewTools = Union("tools:requireApproval", StringComparer.OrdinalIgnoreCase),
            WriteFolders = ResolveList("tools:writeFile:allowedFolders", [".inbox"], pathsAreRoots: true),
            EditableRoots = editableRoots,
            MutationRoots = [repository],
            ProhibitedPaths = Union("prohibitedPaths", StringComparer.Ordinal),
            AllowedNetworkHosts = [.. hosts.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            NetworkHostRestriction = hostFilter is null ? default : [.. hostFilter.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            RequireShellApproval = requireShellApproval,
            ParallelToolsEnabled = parallelEnabled,
            MutationCeiling = mutationCeiling,
            HasExplicitMutationCeiling = hasExplicitMutationCeiling,
            MutationDefault = mutationDefault,
            LargeDiffThreshold = threshold,
            ResourceLimits = limits.ToImmutable(),
            ScriptAssemblies = [.. assemblies.Order(StringComparer.OrdinalIgnoreCase)],
            ControlPaths = [paths.MachineConfiguration, paths.UserConfiguration, paths.RepositoryConfiguration,
                paths.SessionConfiguration, paths.UserProviderCatalog, paths.RepositoryProviderCatalog,
                Path.Combine(paths.RepositoryConfigurationDirectory, "extensions.json"),
                paths.SecretsConfiguration, paths.UserSecretsConfiguration,
                Path.Combine(Path.GetDirectoryName(paths.UserConfiguration) ?? repository, "mcp-tool-approvals.json"),
                Path.Combine(Path.GetDirectoryName(paths.UserConfiguration) ?? repository, "skill-policy.json"),
                Path.Combine(Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "outbound-consent.json"),
                Path.Combine(Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db"),
                Path.Combine(Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db-wal"),
                Path.Combine(Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db-shm"),
                Path.Combine(Threadsmith.Core.HostStoragePaths.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Threadsmith", "repository-facts.db-journal")],
            ControlDirectories = [new RepositoryPermissionStore(paths.UserConfiguration).DirectoryPath],
            Sources = [.. sources],
            Diagnostics = [.. diagnostics.Distinct(StringComparer.Ordinal)],
        };
        var normalized = string.Join('\n', new[]
        {
            repository, string.Join('|', policy.AllowedExecutables),
            policy.StartupAuthorityFingerprint,
            policy.AllowedTools.IsDefault ? "unrestricted" : string.Join('|', policy.AllowedTools),
            string.Join('|', policy.DeniedTools), string.Join('|', policy.ReviewTools),
            string.Join('|', policy.WriteFolders), string.Join('|', policy.EditableRoots), string.Join('|', policy.MutationRoots),
            string.Join('|', policy.ProhibitedPaths), string.Join('|', policy.AllowedNetworkHosts),
            policy.NetworkHostRestriction.IsDefault ? "unrestricted" : string.Join('|', policy.NetworkHostRestriction),
            requireShellApproval.ToString(), parallelEnabled.ToString(), mutationCeiling.ToString(), mutationDefault.ToString(), hasExplicitMutationCeiling.ToString(), threshold.ToString(CultureInfo.InvariantCulture),
            localPermissions.Revision.ToString(CultureInfo.InvariantCulture),
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
        var keys = ConfigurationPermissionCatalog.Properties.Where(property => property.StartupBound).Select(property => property.Path).ToArray();
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
        var values = ConfigurationDocument.Read(path, maximumBytes).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var allowedSource = kind == "Machine" ? PermissionConfigurationSources.Machine : PermissionConfigurationSources.User;
        if (kind is "Machine" or "User" && values.Keys.Any(key => !ConfigurationPermissionCatalog.Allows(key, allowedSource)))
        {
            throw new InvalidDataException($"Configuration '{path}' contains a permission property forbidden in {kind} scope.");
        }

        foreach (var key in ConfigurationPermissionCatalog.Paths.Where(values.ContainsKey))
        {
            provenance.Add(new PolicyInputSource(key, kind, path));
        }

        return new Source(kind, path, values);
    }

    private static Source[] ReadTrustedSources(ConfigurationPaths paths, List<PolicyInputSource> provenance)
    {
        var store = new RepositoryPermissionStore(paths.UserConfiguration);
        var local = store.Read(paths.RepositoryRoot);
        foreach (var key in ConfigurationPermissionCatalog.Paths.Where(local.Settings.ContainsKey))
        {
            provenance.Add(new(key, "UserRepository", store.GetPath(paths.RepositoryRoot)));
        }

        return
        [
            ReadFile(paths.MachineConfiguration, "Machine", provenance),
            ReadFile(paths.UserConfiguration, "User", provenance),
            new Source("UserRepository", store.GetPath(paths.RepositoryRoot), local.Settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)),
            ReadEnvironment(provenance),
        ];
    }

    private static bool IsRestriction(Source source) => source.Kind == "Enforced";

    private static MutationApprovalPolicy RestrictMutation(MutationApprovalPolicy left, MutationApprovalPolicy right)
        => left is MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.TrustPlan or MutationApprovalPolicy.TrustSession
            || right is MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.TrustPlan or MutationApprovalPolicy.TrustSession
            ? MutationApprovalPolicy.ReviewAll : left == MutationApprovalPolicy.ReviewRisky || right == MutationApprovalPolicy.ReviewRisky
                ? MutationApprovalPolicy.ReviewRisky : MutationApprovalPolicy.AlwaysTrustRepo;

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

            if (!ConfigurationPermissionCatalog.Allows(key, PermissionConfigurationSources.Launch))
            {
                throw new InvalidDataException($"Permission '{key}' cannot be defined by the launch environment.");
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

            var isRestriction = IsRestriction(source);
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
