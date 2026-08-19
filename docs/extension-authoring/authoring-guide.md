# Authoring a Threadsmith.NET Extension

This guide shows how to build, package, and debug an extension for Threadsmith.NET. It covers the
authoring convention that prevents the #1 `AssemblyLoadContext` bug (ADR-19, ADR-21), the capability
contracts, and the lifecycle.

> **Contracts:** `Threadsmith.Extensions.Abstractions` (Layer 0). See
> `src/Threadsmith.Extensions.Abstractions/ExtensionContracts.cs` and `CapabilityContracts.cs` for
> the authoritative types.

## 1. The reference convention (do this exactly)

Extensions must load the host's single shared copy of the abstractions contract from the default
load context. Reference the abstractions package with `PrivateAssets="all"` **and**
`ExcludeAssets="runtime"` so the contract DLL is **not** copied into your extension output:

```xml
<ProjectReference
  Include="path/to/Threadsmith.Extensions.Abstractions.csproj"
  Private="false"
  PrivateAssets="all"
  ExcludeAssets="runtime" />
```

Then import the authoring guard so your build fails if the contract DLL ever leaks into your output:

```xml
<Import Project="path/to/Threadsmith.Extensions.Abstractions/build/Threadsmith.Extensions.Abstractions.targets" />
```

If you bundle the contract DLL, the host rejects your extension at load with
`DuplicateContractAssemblyException` (ADR-21). The guard catches it at build time; the runtime check
is the backstop.

## 2. Implement the extension

Implement `IThreadsmithExtension` and one or more capability interfaces:

```csharp
public sealed class MyExtension : IThreadsmithExtension
{
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "com.example.my-extension",
        Name = "My Extension",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider"],
        Permissions = ["repository-read"],
    };

    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken ct)
    {
        context.Capabilities.RegisterTool(new MyToolCapability());
        return default;
    }

    public ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken ct)
        => default;
}
```

## 3. Implement a capability

A tool capability implements `IToolCapability`. The host invokes it via JSON-in / JSON-out so no
extension type crosses the boundary:

```csharp
internal sealed class MyToolCapability : IToolCapability
{
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "my_tool", Kind = CapabilityKind.Tool, DisplayName = "My Tool",
    };

    public ExtensionToolDefinition Definition { get; } = new()
    {
        Id = "my_tool", Version = "1.0", Description = "Does a thing.",
        Category = ExtensionToolCategory.RepositoryInspection,
        InputSchema = new ExtensionToolSchema("MyInput", 1, "{\"type\":\"object\"}"),
        OutputSchema = new ExtensionToolSchema("MyOutput", 1, "{\"type\":\"object\"}"),
        RequiredTrust = ExtensionTrustRequirement.RepositoryRead,
        RequiredApproval = ExtensionApprovalRequirement.None,
        SideEffect = ExtensionToolSideEffect.ReadOnly,
        Idempotency = ExtensionToolIdempotency.Idempotent,
        SupportsCancellation = true,
    };

    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson, ExtensionToolInvocationContext context, CancellationToken ct = default)
    {
        // Deserialize argumentsJson yourself; return a host-owned ExtensionToolResult.
        return Task.FromResult(new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = "{}",
            Sources = [new ExtensionProvenanceSource("extension", "my_tool")],
        });
    }

    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext ctx) => [];
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => [];
    public string? GetExecutable(string argumentsJson) => null;
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson) => [];
}
```

Resource paths, secret references, executables, and network hosts you declare here are enforced by
the host policy pipeline (plan-08, ADR-11). Declare them truthfully.

## 4. Model-preference contributors (advisory)

Implement `IModelPreferenceContributor` to advise the host on model profile selection per workload.
Hints are advisory and aggregated by priority (plan-16 §7).

## 5. Package and deploy

Drop the built extension (entry assembly + private dependencies + `threadsmith.extension.json`
manifest) into the extension directory configured for the repository
(`.threadsmith/extensions/` by default). The manifest lets the host discover the extension's stable
id without loading the assembly:

```json
{
  "Id": "com.example.my-extension",
  "Name": "My Extension",
  "Version": "1.0.0",
  "HostApiVersion": "current",
  "EntryAssembly": "MyExtension.dll",
  "Capabilities": [ "tool-provider" ],
  "Permissions": [ "repository-read" ]
}
```

Manifest property names are PascalCase (the host deserializes with the default JSON options). Set
`CopyToOutputDirectory="PreserveNewest"` on the manifest so it lands in the extension output.

The host shadow-copies the package, loads it into a collectible ALC, activates it, and registers
its capabilities. To select which discovered extensions auto-load at startup, list their ids in the
repo-level `.threadsmith/extensions.json` `autoLoad` array (repo-level only; never the user `~/`
config). At runtime, `/extensions` opens a navigable list to browse, load, and unload extensions.

## 6. Avoid pinning your ALC (unload)

Your extension's ALC unloads only when nothing in the host retains a reference to your types
(ADR-20, ADR-24). Common leaks to avoid:

- Subscribing handlers to **static events in the default context** (e.g. `AppDomain.ProcessExit`)
  and never detaching — this pins your ALC. Always detach in `DeactivateAsync`.
- Leaving **running `Task`s in static fields** that outlive deactivation.
- Holding **unmanaged handles** without releasing them in `DeactivateAsync`.

The host's `UnloadBlockerCatalog` diagnoses these when unload verification reports
`UnloadBlocked`; fix the leak and reload.

## 7. See also

- Sample: `samples/extensions/MinimalToolExtension/`
- Contracts: `src/Threadsmith.Extensions.Abstractions/`
- ADRs 19–24 in `docs/architecture/`
- Tool contracts reference: `docs/extension-authoring/tool-contracts.md`