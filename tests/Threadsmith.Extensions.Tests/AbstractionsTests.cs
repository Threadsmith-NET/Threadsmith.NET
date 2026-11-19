namespace Threadsmith.Extensions.Tests;

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
}
