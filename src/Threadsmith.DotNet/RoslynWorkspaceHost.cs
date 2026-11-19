namespace Threadsmith.DotNet;

using System.Composition.Hosting;
using Microsoft.CodeAnalysis.Host.Mef;

/// <summary>Composes the platform storage services and the workspace-owned analyzer loader.</summary>
internal static class RoslynWorkspaceHost
{
    private const string SqliteFactoryName = "Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorageService+ServiceFactory";
    private static readonly Lazy<MefHostServices> PersistentServices = new(() => CreateServices(includePersistentStorage: true));
    private static readonly Lazy<MefHostServices> NonPersistentServices = new(() => CreateServices(includePersistentStorage: false));

    /// <summary>Gets platform-appropriate services for every semantic workspace.</summary>
    internal static MefHostServices Services => OperatingSystem.IsWindows()
        ? PersistentServices.Value
        : WithoutPersistentStorage;

    /// <summary>Gets the nonpersistent composition, also exercised on Windows by contract tests.</summary>
    internal static MefHostServices WithoutPersistentStorage => NonPersistentServices.Value;

    private static MefHostServices CreateServices(bool includePersistentStorage)
    {
        var parts = MefHostServices.DefaultAssemblies.SelectMany(assembly => assembly.GetTypes()).ToArray();
        if (!includePersistentStorage && !parts.Any(type => type.FullName == SqliteFactoryName))
        {
            throw new InvalidOperationException("The pinned Roslyn SQLite service changed; review workspace storage composition before upgrading.");
        }

        // Roslyn's GetPersistentStorageService selects NoOpPersistentStorageService when this
        // optional export is absent. Keep all language and MSBuild services in the normal host.
        // A query lock cannot protect against background flushes owned by other workspaces.
        var composition = new ContainerConfiguration()
            .WithParts(parts.Where(type => includePersistentStorage || type.FullName != SqliteFactoryName))
            .WithPart<WorkspaceAnalyzerServiceFactory>()
            .CreateContainer();
        return MefHostServices.Create(composition);
    }
}
