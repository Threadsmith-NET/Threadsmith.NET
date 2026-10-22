namespace Threadsmith.Architecture.Tests;

using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Execution;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>Exercises interactive startup with executable constructor-marker fixtures.</summary>
public static class ExtensionAutoloadPolicyTests
{
    /// <summary>Repository selection cannot reach an otherwise executable extension constructor.</summary>
    [Fact]
    public static async Task InteractiveRepositorySelectionCannotConstructExtension()
    {
        var root = Directory.CreateTempSubdirectory("threadsmith-autoload-policy-").FullName;
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(root, ".threadsmith", "extensions", "marker")).FullName;
            var marker = Path.Combine(root, "constructed.txt");
            var source = $$"""
                using System.Threading;
                using System.Threading.Tasks;
                using Threadsmith.Extensions.Abstractions;
                public sealed class MarkerExtension : IThreadsmithExtension
                {
                    public MarkerExtension() => System.IO.File.WriteAllText({{JsonSerializer.Serialize(marker)}}, "constructed");
                    public ExtensionDescriptor Descriptor => new() { Id = "marker", Name = "Marker", Version = "1.0.0" };
                    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                }
                """;
            var platformAssemblies = Assert.IsType<string>(AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
            var references = platformAssemblies.Split(Path.PathSeparator)
                .Append(typeof(IThreadsmithExtension).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                "Marker",
                [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var assemblyPath = Path.Combine(package, "Marker.dll");
            var emitted = compilation.Emit(assemblyPath, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            var manifest = new ExtensionManifest
            {
                Id = "marker", Name = "Marker", Version = "1.0.0", EntryAssembly = "Marker.dll",
            };
            await File.WriteAllTextAsync(Path.Combine(package, "threadsmith.extension.json"), JsonSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".threadsmith", "extensions.json"),
                """{"discoveryDirectory":".threadsmith/extensions","autoLoad":["marker"]}""",
                TestContext.Current.CancellationToken);
            await using var events = new DomainEventStream();
            var leases = new InvocationLeaseAuthority(NullLogger.Instance);
            var capabilities = new CapabilityRegistry(leases, NullLogger<CapabilityRegistry>.Instance);
            var host = await IntegrationComposition.CreateExtensionHostAsync(
                root,
                true,
                events,
                capabilities,
                leases,
                NullLoggerFactory.Instance,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Empty(host.Generations);
            Assert.False(File.Exists(marker));

            // Positive control: the exact fixture really has an executable constructor.
            var loadContext = new AssemblyLoadContext("marker-positive-control", isCollectible: true);
            try
            {
                using var assemblyBytes = new MemoryStream(await File.ReadAllBytesAsync(assemblyPath, TestContext.Current.CancellationToken));
                var assembly = loadContext.LoadFromStream(assemblyBytes);
                _ = Activator.CreateInstance(Assert.IsAssignableFrom<Type>(assembly.GetType("MarkerExtension")));
                Assert.True(File.Exists(marker));
            }
            finally
            {
                loadContext.Unload();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
