namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies the external MCP SDK remains isolated in the MCP adapter assembly.</summary>
public static class McpSdkIsolationTests
{
    /// <summary>Core execution, persistence, and telemetry assemblies do not reference the MCP SDK.</summary>
    [Fact]
    public static void Host_boundary_assemblies_do_not_reference_mcp_sdk()
    {
        Assembly[] assemblies =
        [
            typeof(SessionId).Assembly,
            typeof(ExecutionLimits).Assembly,
            typeof(MigrationRunner).Assembly,
            typeof(SecretOutputSanitizer).Assembly,
        ];

        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(
                GetAssemblyReferences(assembly.Location),
                reference => reference.StartsWith("ModelContextProtocol", StringComparison.Ordinal));
        }
    }

    private static IReadOnlyList<string> GetAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToArray();
    }
}
