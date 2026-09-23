# Threadsmith.NET --- Agent-Managed Runtime Extensions Implementation Plan

**Feature:** Agent-callable `extensions_manage` tool with multi-scope
discovery and mandatory human approval\
**Status:** Proposed\
**Areas:** Core, Extensions.Runtime, Tools, App, TUI/headless, extension
documentation/tests

## 1. Objective

Allow a user to ask Threadsmith to author and build an extension,
discover it, load it into the running process, and use its newly
registered capabilities without restarting Threadsmith.

The agent should use existing repository mutation/build tools to create
the extension. A new built-in `extensions_manage` tool owns runtime
lifecycle operations. After a load/unload/reload changes the capability
registry, the next model inference must use a fresh tool-catalog
snapshot.

**Security invariant:** every model-originated invocation of
`extensions_manage` requires fresh explicit user approval, regardless of
repository trust, mutation approval policy, tool policy, plan approval,
session trust, configuration, or any other policy that would normally
permit automatic execution. Approval is one-shot and cannot be
remembered.

## 2. Existing Architecture

The current repository already provides most of the required machinery:

-   `IExtensionManager` exposes discovery, load, and unload.
-   `ExtensionHost` discovers packages, shadow-copies them, loads
    generations into collectible `AssemblyLoadContext`s, activates
    capabilities, and supports verified unload.
-   Extension capabilities feed the ordinary capability/tool pipeline.
-   Hot replacement and generation draining already exist in the
    extension architecture.
-   Repository discovery currently defaults to
    `.threadsmith/extensions`, configured by
    `.threadsmith/extensions.json`.
-   `/extensions` already exposes user-driven discovery/load/unload.
-   Extension packages already use `threadsmith.extension.json`, an
    entry assembly, and private dependencies.

The main gaps are multi-scope discovery, a model-callable lifecycle
tool, a non-waivable approval class, and explicit tool-catalog refresh
after lifecycle changes.

## 3. Non-Goals

-   Do not add an `extension_create` code generator. Existing coding
    tools create source and project files.
-   Do not bypass ordinary mutation, build, executable, path, secret,
    network, or repository controls.
-   Do not consider an extension trusted merely because Threadsmith
    generated it.
-   Do not allow extension-management approval to inherit from
    `AlwaysTrustRepo`, `TrustSession`, `TrustPlan`, tool allowlists, or
    repository configuration.
-   Do not automatically load every discovered extension.

## 4. Target Workflow

``` text
User asks Threadsmith to create a reusable extension
        |
        v
ordinary write/edit tools
        |
        v
dotnet_build / validation
        |
        v
extensions_manage(discover)
        |
        +--> mandatory one-shot user approval
        |
        v
extensions_manage(load)
        |
        +--> mandatory one-shot user approval
        |
        v
ExtensionHost activates generation
        |
        v
CapabilityRegistry changes
        |
        v
tool-catalog generation changes
        |
        v
next inference receives fresh tool definitions
        |
        v
model can immediately use the new extension tool
```

A subsequent edit can build a replacement and call
`extensions_manage(reload)`, activating a new generation and
draining/unloading the previous one.

## 5. Security Model

### 5.1 Native-code boundary

Loading an extension executes compiled .NET code inside the Threadsmith
process. Treat extension lifecycle management as a privileged
native-code execution boundary rather than an ordinary tool mutation.

All actions exposed by `extensions_manage` require approval in v1,
including discovery. Discovery should not execute assemblies, but one
uniform rule avoids an ambiguous security contract and prevents future
discovery changes from silently becoming privileged.

### 5.2 Non-waivable approval classification

Do not special-case the tool ID in the TUI. Add a first-class host-owned
approval semantic to the central tool invocation pipeline.

Suggested shape:

``` csharp
public enum ToolApprovalRequirement
{
    None,
    PolicyControlled,
    AlwaysInteractive
}
```

If an equivalent enum exists when implemented, extend it instead.

`AlwaysInteractive` means:

-   every invocation requires a fresh human decision;
-   no trust/policy/configuration setting can suppress the prompt;
-   no "always allow", session approval, or persisted approval is
    permitted;
-   repository-controlled configuration cannot grant it;
-   subagents do not inherit it by default;
-   headless execution fails closed when no human approval channel
    exists;
-   audit records distinguish it from policy-controlled approvals.

The host invocation pipeline owns enforcement; the tool adapter must not
be able to bypass it.

### 5.3 Approval prompt

Display at least:

-   action;
-   stable extension ID;
-   name/version;
-   scope;
-   source directory;
-   entry assembly;
-   manifest-declared capabilities and permissions;
-   current/replacement generation for reload when applicable;
-   explicit warning that compiled code will execute inside Threadsmith.

Do not present an "always approve" option.

### 5.4 TOCTOU protection

Approval must bind to the exact package inspected before prompting.
Compute a candidate digest covering the manifest, entry assembly, and
loadable package/dependency files.

Prefer staging an immutable candidate before approval. Otherwise
revalidate the digest immediately before activation. If bytes changed
after approval, invalidate approval and require a new prompt.

### 5.5 Existing controls still apply

Approval does not waive compatibility validation, duplicate-contract
checks, extension permissions, resource/path restrictions, executable
controls, network/secret controls, or capability policy.

## 6. Multi-Scope Discovery

### 6.1 Scopes

Support:

``` csharp
public enum ExtensionDiscoveryScope
{
    Application,
    User,
    Repository
}
```

Meanings:

-   **Application** --- extensions installed/shipped with Threadsmith.
-   **User** --- extensions installed for the current OS user and
    reusable across repositories.
-   **Repository** --- extensions belonging to the active
    repository/worktree.

Repository scope preserves the existing `.threadsmith/extensions`
behavior.

### 6.2 Default roots

Resolve through Threadsmith's existing path abstractions:

-   Application: `<application-root>/extensions`
-   User: Threadsmith user-data/config root + `/extensions`
-   Repository: `<repo>/.threadsmith/extensions`

The existing repository `discoveryDirectory` remains a repository-scope
override.

### 6.3 Discovery identity

Extend summaries with scope:

``` csharp
public sealed record ExtensionSummary
{
    // existing fields
    public required ExtensionDiscoveryScope Scope { get; init; }
    public required string Directory { get; init; }
}
```

Use an internal `DiscoveredExtensionCandidate` for digest/package
identity rather than overloading the presentation DTO.

Replace the single ambient `SetDiscoveryDirectory` model with a catalog
of explicit discovery sources.

### 6.4 Manager contract direction

Evolve `IExtensionManager` toward:

``` csharp
Task<IReadOnlyList<ExtensionSummary>> DiscoverAsync(
    ExtensionDiscoveryScope? scope = null,
    CancellationToken cancellationToken = default);

Task<ExtensionSummary?> LoadAsync(
    string extensionId,
    ExtensionDiscoveryScope? scope,
    SessionId sessionId,
    CancellationToken cancellationToken = default);

Task<bool> UnloadAsync(
    string extensionId,
    SessionId sessionId,
    CancellationToken cancellationToken = default);

Task<ExtensionSummary?> ReloadAsync(
    string extensionId,
    ExtensionDiscoveryScope? scope,
    SessionId sessionId,
    CancellationToken cancellationToken = default);
```

Exact overloads may be adjusted for compatibility.

### 6.5 Duplicate IDs

Do not silently discard duplicates across scopes.

-   retain every candidate;
-   show scope in `/extensions`;
-   require explicit scope for model-driven load/reload when ambiguous;
-   emit a diagnostic for collisions.

For compatibility with existing unqualified startup IDs, use:

``` text
Repository > User > Application
```

but expose which scope won. New configuration should support explicit
scope qualification.

### 6.6 Repository configuration

Keep `.threadsmith/extensions.json`. Its `discoveryDirectory` remains
repository-only and `autoLoad` remains repository startup selection.

Repository configuration must never configure user/application roots or
grant `extensions_manage` approval.

## 7. `extensions_manage` Tool

### 7.1 Input

``` json
{
  "action": "discover | load | unload | reload",
  "extensionId": "com.example.foo",
  "scope": "application | user | repository"
}
```

Rules:

-   `discover`: ID optional; scope optional.
-   `load`: ID required; scope optional only when uniquely resolvable.
-   `unload`: ID required; operates on active generation.
-   `reload`: ID required; resolves a replacement candidate and uses
    hot-replacement semantics.

Do not add source-generation arguments.

### 7.2 Output

Return bounded structured data:

``` json
{
  "succeeded": true,
  "action": "load",
  "extensionId": "com.example.foo",
  "scope": "repository",
  "state": "Active",
  "generationId": "...",
  "toolCatalogChanged": true,
  "message": "Extension loaded; new capabilities are available on the next inference."
}
```

Discovery results include ID, name, version, scope, lifecycle state,
capability counts, and ambiguity diagnostics.

### 7.3 Tool metadata

Classify conservatively:

-   host/runtime mutation;
-   `AlwaysInteractive`;
-   session-exclusive / non-parallel-safe;
-   non-idempotent at the tool level;
-   not inherited by subagents by default;
-   auditable;
-   cancellation-aware.

Do not run it in parallel with sibling tool calls because extension
lifecycle transitions mutate the tool universe.

## 8. Tool-Catalog Synchronization

A request cannot gain new tool definitions halfway through the
already-issued model request.

Successful `load`, `unload`, and `reload` therefore create a catalog
synchronization boundary:

``` text
request N -> extensions_manage -> lifecycle change
          -> catalog generation advances
          -> current tool result completes
request N+1 -> rebuild tool snapshot -> model sees new catalog
```

Reuse existing capability-registry generation/version semantics if
available. Otherwise add a monotonic host-owned catalog generation.

The continuation path must re-resolve tools whenever the generation
changes. Existing generation/lease fencing must continue to reject stale
pre-resolved extension capabilities.

## 9. Creation and Installation

Repository scope should be the default target for model-authored
extensions:

``` text
<repo>/.threadsmith/extensions/<extension-id>/
```

Ordinary coding tools can author and build the project/package.

Do **not** let repository mutation authority write to user/application
extension roots. For v1, user/application scopes should support
discovery/load of already installed extensions, while model-authored
installation targets repository scope.

A later feature may add `install`/`remove` actions to
`extensions_manage`; those operations must also be `AlwaysInteractive`,
digest-bound, path-confined, and atomic.

## 10. Reload Semantics

Implement `reload` as a first-class manager operation:

1.  discover replacement candidate;
2.  validate manifest/package and compute digest;
3.  request mandatory approval for that exact candidate;
4.  stage candidate;
5.  load/validate new generation;
6.  activate new capabilities;
7.  atomically make new generation current;
8.  close admission to old generation;
9.  drain outstanding leases;
10. deactivate/unload old generation;
11. report `UnloadBlocked` honestly if collectible unload fails;
12. advance tool-catalog generation.

Prefer activating the replacement before retiring the working generation
so a bad replacement does not unnecessarily destroy the existing
extension.

## 11. UI and Headless Behavior

### `/extensions`

Display scope alongside lifecycle state, ID/name, and version. Duplicate
IDs from different scopes remain separate entries.

Direct `/extensions` actions are already human-originated and need not
invoke the model tool. They should nevertheless show a native-code
warning before load/reload if the current UX does not already make that
risk explicit.

### Headless

`extensions_manage` fails closed when no interactive approval surface
exists:

``` json
{
  "succeeded": false,
  "code": "InteractiveApprovalRequired",
  "message": "Extension management requires fresh user approval and cannot run unattended."
}
```

No repository setting, CLI trust flag, or ordinary policy setting
bypasses this in v1.

### Tool availability

Normal `/tools` enable/disable controls may determine whether the model
can request `extensions_manage`. Enabling it only makes the tool
available; it never suppresses mandatory approval.

## 12. Implementation Tasks

### Task 1 --- Add extension discovery scopes

**Projects:** Core, Extensions.Runtime

-   Add `ExtensionDiscoveryScope`.
-   Add scope to host-owned summaries.
-   Introduce a discovered-candidate internal model.
-   Add application/user/repository source resolution.
-   Preserve repository `discoveryDirectory`.
-   Remove/deprecate single-root assumptions.

### Task 2 --- Refactor discovery catalog

**Project:** Extensions.Runtime

-   Scan all configured scopes without loading assemblies.
-   Track candidates by `(stableId, scope)`.
-   Preserve duplicate IDs rather than overwriting dictionary entries.
-   Add deterministic compatibility precedence for unqualified startup
    selection.
-   Add tests for empty/missing/malformed roots and duplicate IDs.

### Task 3 --- Add first-class `AlwaysInteractive` approval

**Projects:** Core, Tools, Interaction/App

-   Extend central approval metadata/contracts.
-   Enforce before adapter execution.
-   Ensure trust and mutation policies cannot waive it.
-   Remove persistent/session approval choices.
-   Fail closed in unattended/headless mode.
-   Emit explicit audit events.
-   Add regression tests proving every existing trust mode still
    prompts.

### Task 4 --- Add candidate digest/staging

**Project:** Extensions.Runtime

-   Produce a stable package digest.
-   Bind approval metadata to candidate identity.
-   Stage immutably before approval where practical.
-   Reject post-approval package changes.
-   Ensure shadow-copy/load consumes the approved candidate.

### Task 5 --- Add `extensions_manage`

**Project:** Tools/App integration

-   Implement `discover`, `load`, `unload`, `reload`.
-   Resolve ambiguity by scope.
-   Route lifecycle operations only through `IExtensionManager`.
-   Return bounded structured results.
-   Mark tool `AlwaysInteractive` and session-exclusive.
-   Keep it unavailable to subagents by default.

### Task 6 --- Add manager-level reload

**Projects:** Core, Extensions.Runtime

-   Add `ReloadAsync` or equivalent.
-   Reuse existing hot-replacement/drain/unload mechanisms.
-   Preserve old generation if replacement validation/load fails.
-   Report blocked unload separately from replacement activation
    success.

### Task 7 --- Tool-catalog generation boundary

**Projects:** Extensions.Runtime, Tools, CoreRuntime/App

-   Advance catalog generation on capability registration/removal.
-   Detect generation changes between inference continuations.
-   Rebuild tool definitions for the next inference.
-   Reject stale resolved handles using existing generation fencing.

### Task 8 --- Update `/extensions`

**Projects:** Interaction/TUI

-   Show discovery scope.
-   Handle duplicate IDs.
-   Support explicit scoped load/unload/reload where appropriate.
-   Add native-code warning to direct user activation if needed.

### Task 9 --- Documentation

Update:

-   extension authoring guide;
-   tool operations/security documentation;
-   extension architecture docs/ADRs if the discovery model changes
    materially;
-   manual test plan;
-   `.threadsmith/extensions.json` documentation.

Document clearly that model-authored extensions should normally use
repository scope.

## 13. Required Tests

### Discovery

-   discovers application/user/repository candidates;
-   repository default remains `.threadsmith/extensions`;
-   repository `discoveryDirectory` override still works;
-   duplicate stable IDs remain distinct;
-   ambiguous model load requires scope;
-   compatibility precedence is deterministic.

### Approval/security

For **every** trust/policy level:

-   `extensions_manage(discover)` prompts;
-   `load` prompts;
-   `unload` prompts;
-   `reload` prompts;
-   prior approval does not authorize the next invocation;
-   `AlwaysTrustRepo` does not suppress approval;
-   approved plan does not suppress approval;
-   tool allowlist does not suppress approval;
-   repository config cannot suppress approval;
-   headless mode denies without interactive approval;
-   subagent cannot invoke it by default.

### TOCTOU

-   modifying DLL after approval invalidates approval;
-   modifying manifest after approval invalidates approval;
-   modifying dependency after approval invalidates approval;
-   approved immutable staged package loads successfully.

### Lifecycle

-   newly loaded tool appears on next inference;
-   unloaded tool disappears on next inference;
-   stale capability handles are rejected;
-   reload activates new generation;
-   failed replacement leaves old generation usable;
-   blocked old-generation unload is reported accurately.

## 14. Manual Acceptance Scenario

1.  Start Threadsmith on a test repository.
2.  Ask it to create a minimal repository-scoped extension exposing
    `hello_extension`.
3.  Allow normal source edits/build according to ordinary policy.
4.  Agent calls `extensions_manage(discover)`; verify mandatory one-shot
    approval.
5.  Agent calls `extensions_manage(load)`; verify mandatory one-shot
    approval and native-code warning.
6.  Verify the next inference advertises `hello_extension`.
7.  Ask Threadsmith to invoke it successfully.
8.  Ask Threadsmith to modify the extension behavior and rebuild.
9.  Agent calls `extensions_manage(reload)`; verify a new approval is
    required despite prior approval.
10. Verify new behavior is available without restarting Threadsmith.
11. Agent calls `extensions_manage(unload)`; verify another approval is
    required.
12. Verify the extension tool disappears from the next inference.
13. Repeat with `AlwaysTrustRepo`; all four lifecycle actions must still
    require approval.
14. Repeat headless; lifecycle operation must fail with
    `InteractiveApprovalRequired`.

## 15. Completion Criteria

The feature is complete when:

-   application, user, and repository extension discovery all work;
-   existing repository discovery/configuration remains compatible;
-   the model can request discovery/load/unload/reload through
    `extensions_manage`;
-   every invocation requires fresh non-waivable human approval;
-   approval is bound to the exact candidate package;
-   repository trust and mutation policy cannot bypass the boundary;
-   newly loaded capabilities appear on the next inference without
    restart;
-   unload/reload correctly refresh the model tool catalog;
-   hot replacement preserves the working generation when replacement
    activation fails;
-   tests cover security, scope collision, lifecycle, stale-generation,
    and headless behavior.

## 16. Design Principle

The important separation is:

> **The model may author native extension code using Threadsmith's
> ordinary coding workflow, but only the host and the human user may
> authorize that code to cross the runtime execution boundary.**

That keeps self-extension composable and useful without allowing
model-generated executable code to inherit authority from ordinary
repository trust.
