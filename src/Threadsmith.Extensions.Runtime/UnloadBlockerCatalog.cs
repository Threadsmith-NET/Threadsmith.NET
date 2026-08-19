namespace Threadsmith.Extensions.Runtime;

using System.Reflection;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Host-owned catalog of common reasons a collectible AssemblyLoadContext cannot unload (strategy §17.18).
/// These are diagnostics, not fixes — an ALC is not a security boundary and the host cannot force unload.</summary>
public sealed record UnloadBlockerReport
{
    /// <summary>The generation that could not unload.</summary>
    public required ExtensionGenerationId GenerationId { get; init; }

    /// <summary>The owning extension id.</summary>
    public required ExtensionId ExtensionId { get; init; }

    /// <summary>Candidate blockers discovered by reflection.</summary>
    public IReadOnlyList<UnloadBlocker> Blockers { get; init; } = [];

    /// <summary>Whether any blockers were found.</summary>
    public bool HasBlockers => Blockers.Count > 0;
}

/// <summary>One candidate unload blocker.</summary>
public sealed record UnloadBlocker
{
    /// <summary>The blocker category (static event, lingering registration, running task, unmanaged handle, finalizer).</summary>
    public required string Category { get; init; }

    /// <summary>A human-readable description of the suspected retained reference.</summary>
    public required string Description { get; init; }
}

/// <summary>Builds an <see cref="UnloadBlockerReport"/> for a generation whose ALC survived unload verification (§17.18).</summary>
public sealed class UnloadBlockerCatalog
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="UnloadBlockerCatalog"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public UnloadBlockerCatalog(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Inspects a load context for common blockers.</summary>
    /// <param name="generation">The generation whose ALC survived.</param>
    /// <param name="loadContext">The captured load context to scan (may be null once already released).</param>
    /// <returns>The blocker report.</returns>
    public UnloadBlockerReport Inspect(ExtensionGeneration generation, ExtensionLoadContext? loadContext)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var blockers = new List<UnloadBlocker>();

        _logger.LogInformation(
            "Inspecting generation {GenerationId} for unload blockers.",
            generation.GenerationId);

        // Static events: handlers attached to default-context static events retain the extension ALC.
        AddStaticEventBlockers(loadContext, blockers);

        // Running tasks: lingering Task instances captured in extension statics.
        AddRunningTaskBlockers(loadContext, blockers);

        // Unmanaged handles / native resources are not reflectable here; surface a reminder.
        blockers.Add(new UnloadBlocker
        {
            Category = "unmanaged-handle",
            Description = "The extension may hold unmanaged handles or native resources not visible to reflection; review DisposeAsync.",
        });

        return new UnloadBlockerReport
        {
            GenerationId = generation.GenerationId,
            ExtensionId = generation.ExtensionId,
            Blockers = blockers,
        };
    }

    private static void AddStaticEventBlockers(ExtensionLoadContext? alc, List<UnloadBlocker> blockers)
    {
        if (alc is null)
        {
            return;
        }

        foreach (var assembly in alc.Assemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                foreach (var evt in type.GetEvents(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    blockers.Add(new UnloadBlocker
                    {
                        Category = "static-event",
                        Description = $"Static event '{type.FullName}.{evt.Name}' may retain handlers from the extension.",
                    });
                }
            }
        }
    }

    private static void AddRunningTaskBlockers(ExtensionLoadContext? alc, List<UnloadBlocker> blockers)
    {
        if (alc is null)
        {
            return;
        }

        // Running tasks captured in extension static fields pin the ALC until they complete.
        foreach (var assembly in alc.Assemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object? value = SafeGetValue(field);
                    if (value is Task or System.Threading.Tasks.ValueTask)
                    {
                        blockers.Add(new UnloadBlocker
                        {
                            Category = "running-task",
                            Description = $"Static field '{type.FullName}.{field.Name}' holds an in-flight Task that may pin the ALC.",
                        });
                    }
                }
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException)
        {
            return [];
        }
    }

    private static object? SafeGetValue(FieldInfo field)
    {
        try
        {
            return field.GetValue(null);
        }
        catch
        {
            return null;
        }
    }
}