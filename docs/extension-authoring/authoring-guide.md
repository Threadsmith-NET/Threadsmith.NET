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
its capabilities. At runtime, `/extensions` opens a navigable list to browse, load, and unload
extensions. Review executable extension code before loading it. Repository `autoLoad` requests
are refused before discovery or construction. Selection is not execution authority.

### Repository settings

Use repository-owned `.threadsmith/extensions.json` for extension-specific settings:

```json
{
  "discoveryDirectory": ".threadsmith/extensions",
  "settings": {
    "com.example.my-extension": {
      "endpoint": "https://example.invalid",
      "credentialReference": "secrets:my-extension.password",
      "optionalLabel": null
    }
  }
}
```

A settings section uses the stable descriptor ID. IDs and setting keys are case-sensitive. Values
must be JSON strings or null. Each activation receives only its own section through
`context.Configuration` and the repository root through `context.HostInformation.RepositoryPath`.
Validate your extension's key names, types and required values. Store logical references, not
passwords. Settings are not expanded during loading and do not grant secret access. The current
extension invocation contract does not yet expose host secret resolution.

Each generation keeps an immutable snapshot. File edits take effect only after explicit unload/load
or hot replacement. In-flight old-generation calls retain their original view. Failed or cancelled
activation cannot publish a partial settings update. A missing file or section supplies an empty
view. Other file/parse errors reject explicit loading with `ExtensionSettingsInvalid`, without a
configuration dump. Invalid startup selection retains the no-autoload fallback.

Limits: 256 KiB UTF-8 for the whole file (including BOM), JSON depth 8, 128 sections, 256 UTF-16 code
units per ID/key, 128 keys per section, 8,192 code units per value and 65,536 aggregate key/value
code units per section. Blank names, duplicate IDs/keys, duplicate root properties ignoring case,
invalid types and exceeded limits fail. Unknown root properties are ignored but still count toward
file/depth limits. Only this repository file is used; there is no user-level settings merge.

The extension manager and discovery selection are startup-bound. After `/open` switches repositories,
restart Threadsmith in the new repository before discovering or loading extensions. Stale manager
operations fail with `ExtensionRepositoryStale`. A tool generation cannot execute in a different
repository from the one captured at activation. Already admitted old-repository calls keep their
captured settings; a repository switch does not rewrite them.

Host callers may supply `ExtensionLoadRequest.Configuration` as an explicit override, including an
empty map. Null selects repository settings. Explicit maps are bounded and copied before loading
awaits. The host never passes the whole application configuration to an extension.

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