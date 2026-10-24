namespace Threadsmith.Core;

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;

/// <summary>Declares permission ownership on the configuration property that owns a value or subtree.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigurationPermissionAttribute : Attribute
{
    /// <summary>Gets the host-owned sources permitted to define this property.</summary>
    public PermissionConfigurationSources Sources { get; init; } = PermissionConfigurationSources.Machine | PermissionConfigurationSources.User | PermissionConfigurationSources.UserRepository | PermissionConfigurationSources.Launch;

    /// <summary>Gets the input shape used by source validation.</summary>
    public PermissionInputShape Shape { get; init; }

    /// <summary>Gets whether an existing adapter owns value validation and startup binding.</summary>
    public bool AdapterOwned { get; init; }

    /// <summary>Gets whether a change requires rebuilding startup-owned services.</summary>
    public bool StartupBound { get; init; }

    /// <summary>Gets the compiled resource limit, when this property declares one.</summary>
    public int DefaultLimit { get; init; } = int.MinValue;
}

/// <summary>Shapes whose presence and empty values must survive configuration merging.</summary>
public enum PermissionInputShape
{
    /// <summary>A structured setting validated by its owning subsystem.</summary>
    Subtree,

    /// <summary>An array of strings; an empty array is distinct from absence.</summary>
    List,

    /// <summary>A boolean value.</summary>
    Boolean,

    /// <summary>A scalar value validated by its owning subsystem.</summary>
    Scalar,
}

/// <summary>A cached property declaration shared by source loading and coordinated writes.</summary>
public sealed record ConfigurationPermission(string Path, PermissionInputShape Shape, bool AdapterOwned, bool StartupBound, int? DefaultLimit, PermissionConfigurationSources Sources);

/// <summary>Source identities supplied by the host, never by configuration content.</summary>
[Flags]
public enum PermissionConfigurationSources
{
    /// <summary>No source is admitted.</summary>
    None = 0,

    /// <summary>Machine configuration.</summary>
    Machine = 1,

    /// <summary>User defaults.</summary>
    User = 2,

    /// <summary>User-owned repository permissions.</summary>
    UserRepository = 4,

    /// <summary>Explicit launch environment.</summary>
    Launch = 8,
}

/// <summary>Discovers permission ownership once from configuration property metadata.</summary>
public static class ConfigurationPermissionCatalog
{
    /// <summary>Gets the immutable declarations for supported application configuration.</summary>
    public static ImmutableArray<ConfigurationPermission> Properties { get; } = Describe(typeof(PermissionConfiguration));

    /// <summary>Gets all permission paths, including structured property descendants.</summary>
    public static ImmutableArray<string> Paths { get; } = [.. Properties.Select(property => property.Path)];

    /// <summary>Tests ownership without reclassifying values at individual readers or writers.</summary>
    public static bool IsPermission(string path) => Paths.Any(key => path.Equals(key, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks enclosing permission declarations against the actual host-selected source.</summary>
    public static bool Allows(string path, PermissionConfigurationSources source) => Properties
        .Where(property => path.Equals(property.Path, StringComparison.OrdinalIgnoreCase) || path.StartsWith(property.Path + ":", StringComparison.OrdinalIgnoreCase))
        .All(property => (property.Sources & source) != 0);

    /// <summary>Reflects a configuration contract, respecting JSON names and inherited property declarations.</summary>
    public static ImmutableArray<ConfigurationPermission> Describe(Type configurationType)
    {
        ArgumentNullException.ThrowIfNull(configurationType);
        var result = ImmutableArray.CreateBuilder<ConfigurationPermission>();
        var visiting = new HashSet<Type>();
        Visit(configurationType, string.Empty);
        if (result.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            throw new InvalidOperationException("Configuration permission names are ambiguous.");
        }

        return result.ToImmutable();

        void Visit(Type type, string prefix)
        {
            if (!visiting.Add(type))
            {
                throw new InvalidOperationException("Configuration permission metadata contains a cycle.");
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0 || property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                {
                    continue;
                }

                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
                var path = prefix.Length == 0 ? name : prefix + ":" + name;
                if (property.GetCustomAttribute<ConfigurationPermissionAttribute>() is { } permission)
                {
                    result.Add(new(
                        path,
                        permission.Shape,
                        permission.AdapterOwned,
                        permission.AdapterOwned || permission.StartupBound,
                        permission.DefaultLimit == int.MinValue ? null : permission.DefaultLimit,
                        permission.Sources));
                }

                var nestedType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (nestedType.IsClass && nestedType != typeof(string)
                    && !typeof(System.Collections.IEnumerable).IsAssignableFrom(nestedType)
                    && nestedType.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
                {
                    Visit(nestedType, path);
                }
            }

            visiting.Remove(type);
        }
    }
}
