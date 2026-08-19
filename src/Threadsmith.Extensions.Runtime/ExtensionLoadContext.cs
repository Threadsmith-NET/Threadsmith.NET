namespace Threadsmith.Extensions.Runtime;

using System.Reflection;
using System.Runtime.Loader;
using Threadsmith.Extensions.Abstractions;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> hosting one extension generation with
/// <see cref="AssemblyDependencyResolver"/> dependency resolution and shared-contract resolution
/// from the default context (strategy §17.10, §17.11; ADRs 13, 14).
/// </summary>
/// <remarks>
/// An <see cref="AssemblyLoadContext"/> is a dependency-isolation and unload mechanism, not a
/// security boundary (§17.24, §36). Shared contract assemblies (the abstractions package) resolve
/// from <see cref="AssemblyLoadContext.Default"/> so there is exactly one copy in the process;
/// a duplicate copy bundled by the extension is rejected at load (gap #5).
/// </remarks>
public sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _sharedContractAssemblyName;

    /// <summary>Initializes a new instance of the <see cref="ExtensionLoadContext"/> class.</summary>
    /// <param name="entryAssemblyPath">The shadow-copied entry assembly path with a sibling deps.json.</param>
    /// <param name="sharedContractAssemblyName">
    /// The simple name of the shared contract assembly resolved from the default context.
    /// Defaults to the abstractions package name.
    /// </param>
    public ExtensionLoadContext(string entryAssemblyPath, string? sharedContractAssemblyName = null)
        : base(
            name: $"Extension:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _sharedContractAssemblyName = sharedContractAssemblyName
            ?? typeof(IThreadsmithExtension).Assembly.GetName().Name
            ?? "Threadsmith.Extensions.Abstractions";
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedContract(assemblyName))
        {
            // The shared contract must resolve from the default context. If the resolver finds a
            // bundled copy in the extension package, the extension mis-configured its reference
            // (the #1 ALC bug, §17.11) and we reject it rather than loading a second contract copy.
            var bundled = _resolver.ResolveAssemblyToPath(assemblyName);
            if (!string.IsNullOrEmpty(bundled))
            {
                throw new DuplicateContractAssemblyException(assemblyName.Name ?? string.Empty, bundled);
            }

            // Returning null defers to the default context, which holds the single shared copy.
            return null;
        }

        // Extension-private dependencies resolve from the extension package via the resolver.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>Determines whether an assembly name is the shared contract that must resolve from default.</summary>
    /// <param name="assemblyName">The assembly name to test.</param>
    /// <returns><see langword="true"/> when the assembly is the shared contract.</returns>
    private bool IsSharedContract(AssemblyName assemblyName)
    {
        return string.Equals(assemblyName.Name, _sharedContractAssemblyName, StringComparison.Ordinal);
    }
}