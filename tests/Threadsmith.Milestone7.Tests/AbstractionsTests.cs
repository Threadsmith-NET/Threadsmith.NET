namespace Threadsmith.Milestone7.Tests;

using System.Reflection;
using System.Text.Json;
using Threadsmith.Extensions.Abstractions;
using Xunit;

/// <summary>
/// Plan-14 acceptance: the abstractions package is small, stable, standalone, free of
/// terminal-library / Roslyn / SDK types, and the reference convention + authoring guard
/// prevent the duplicate-contract-assembly bug (gap #5).
/// </summary>
public static class AbstractionsTests
{
    private static readonly Assembly _abstractions = typeof(ExtensionDescriptor).Assembly;

    /// <summary>The abstractions assembly references no other Threadsmith.* assembly (§8.1).</summary>
    [Fact]
    public static void AbstractionsReferencesNoThreadsmithAssemblies()
    {
        var referenced = _abstractions.GetReferencedAssemblies();
        var threadsmithRefs = referenced
            .Where(name => name.Name?.StartsWith("Threadsmith.", StringComparison.Ordinal) == true)
            .Select(name => name.Name)
            .ToList();
        Assert.Empty(threadsmithRefs);
    }

    /// <summary>The abstractions assembly references no terminal-library, Roslyn, model-SDK, or persistence packages (§8.1).</summary>
    [Theory]
    [InlineData("PrettyPrompt")]
    [InlineData("Spectre.Console")]
    [InlineData("Terminal.Gui")]
    [InlineData("Microsoft.CodeAnalysis")]
    [InlineData("OpenAI")]
    [InlineData("Microsoft.Data.Sqlite")]
    public static void AbstractionsReferencesNoForbiddenPackages(string forbiddenPrefix)
    {
        var referenced = _abstractions.GetReferencedAssemblies();
        var found = referenced.Any(name =>
            name.Name?.StartsWith(forbiddenPrefix, StringComparison.Ordinal) == true);
        Assert.False(found, $"Abstractions references a forbidden package '{forbiddenPrefix}'.");
    }

    /// <summary>No capability contract type returns or accepts a terminal-library / Roslyn / SDK type.</summary>
    [Fact]
    public static void CapabilityContractsExposeOnlyFrameworkTypes()
    {
        Type[] contracts =
        [
            typeof(IThreadsmithExtension),
            typeof(IToolCapability),
            typeof(IModelPreferenceContributor),
            typeof(IInvocationLease),
            typeof(IExtensionCapabilityRegistrar),
        ];

        foreach (var contract in contracts)
        {
            foreach (var method in contract.GetMethods())
            {
                AssertTypeIsFrameworkOrSelf(method.ReturnType, method.DeclaringType?.Name + "." + method.Name);
                foreach (var parameter in method.GetParameters())
                {
                    AssertTypeIsFrameworkOrSelf(parameter.ParameterType, method.DeclaringType?.Name + "." + method.Name);
                }
            }

            foreach (var property in contract.GetProperties())
            {
                AssertTypeIsFrameworkOrSelf(property.PropertyType, methodContainer: contract.Name);
            }
        }

        static void AssertTypeIsFrameworkOrSelf(Type? type, string methodContainer)
        {
            if (type == null)
            {
                return;
            }

            // Drill through Task<T>/ValueTask<T>/IReadOnlyList<T>.
            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    AssertTypeIsFrameworkOrSelf(arg, methodContainer);
                }

                return;
            }

            var assembly = type.Assembly.GetName().Name;
            var allowed = assembly == _abstractions.GetName().Name
                || assembly?.StartsWith("System", StringComparison.Ordinal) == true
                || assembly == "mscorlib"
                || assembly == "netstandard";
            Assert.True(allowed, $"Contract {methodContainer} exposes non-framework type {type.FullName} from {assembly}.");
        }
    }

    /// <summary>The sample extension's output package excludes the abstractions DLL (gap #5 precondition).</summary>
    [Fact]
    public static void SampleExtensionOutputExcludesAbstractionsDll()
    {
        var sampleOutput = ResolveSampleExtensionOutputDirectory();
        var abstractionsDll = Path.Combine(sampleOutput, "Threadsmith.Extensions.Abstractions.dll");
        var sampleDll = Path.Combine(sampleOutput, "Threadsmith.SampleExtensions.MinimalTool.dll");
        Assert.True(File.Exists(sampleDll), $"Sample extension DLL was not built at {sampleDll}.");
        Assert.False(
            File.Exists(abstractionsDll),
            "The sample extension copies Threadsmith.Extensions.Abstractions.dll into its output, "
            + "which would load a duplicate contract assembly into the extension collectible context (gap #5).");
    }

    /// <summary>The sample extension manifest JSON round-trips and declares capabilities.</summary>
    [Fact]
    public static void ExtensionManifestIsJsonSerializable()
    {
        var manifest = new ExtensionManifest
        {
            Id = "example.git-tools",
            Name = "Example Git Tools",
            Version = "1.2.0",
            EntryAssembly = "Example.GitTools.dll",
            HostApiVersion = ExtensionContractVersion.Current,
            Capabilities = ["tool-provider", "validator"],
            Permissions = ["repository-read", "process-execute"],
        };
        var json = JsonSerializer.Serialize(manifest);
        var roundTripped = JsonSerializer.Deserialize<ExtensionManifest>(json)!;
        Assert.Equal(manifest.Id, roundTripped.Id);
        Assert.Equal(manifest.Capabilities, roundTripped.Capabilities);
        Assert.Equal(manifest.Permissions, roundTripped.Permissions);
    }

    private static string ResolveSampleExtensionOutputDirectory()
    {
        // Test bin: <root>/tests/Threadsmith.Milestone7.Tests/bin/Debug/net10.0
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", "MinimalToolExtension", "bin", configuration, "net10.0");
    }
}