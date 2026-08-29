# Implementation Plan 93: Codex Headless Auth JSON Numeric Compatibility

**Status:** Planned.
**Delivery track:** Maintenance — compatibility bug fix for completed Plan 50 Codex OAuth authentication
**Prerequisites:** Plan 50, ADR-40, current `Threadsmith.Models.OpenAiCodex` OAuth implementation, and current Codex authentication command surfaces
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned provider authority, bounded untrusted input, cancellation propagation, provider-neutral boundaries, and maintenance-track routing
**Related contracts:** [planning governance](planning-governance.md), [Plan 50](plan-50-openai-codex-responses-oauth-provider.md), [ADR-40](../architecture/adr-40-native-codex-provider-and-output-reserve.md), [Threadsmith.Models.OpenAiCodex AGENTS](../../src/Threadsmith.Models.OpenAiCodex/AGENTS.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Fix Codex OAuth numeric-field parsing so `threadsmith --codex-login` works in the headless device flow when the upstream authorization service returns integer-valued JSON fields as strings instead of JSON numbers.

The fix must preserve Threadsmith-owned Codex authentication, existing bounds, redaction, cancellation, and provider isolation while replacing generic `JsonElement` type failures with tolerant bounded parsing for known optional integer fields.

## 2 Architectural Context

Plan 50 completed the native `openai-codex` provider, including independent browser and headless OAuth flows, token caching outside repositories, protected account model discovery, and no Pi credential reuse. ADR-40 keeps Codex authority compiled and isolated from repository/provider catalogs.

The observed installed-binary failure for:

```powershell
"C:\Program Files\Threadsmith\Threadsmith.App.exe" --codex-login
```

is:

```text
Threadsmith could not start or continue: The requested operation requires an element of type 'Number', but the target element has type 'String'.
```

Current code uses `JsonElement.TryGetInt32` directly for optional OAuth fields such as `interval` and `expires_in`. `System.Text.Json` throws before returning `false` when those properties are strings, so a provider-compatible stringified integer aborts authentication before the user can complete the headless flow.

## 3 Scope

- Add one small Codex-OAuth-local helper for optional bounded integer extraction from untrusted JSON.
- Accept both JSON numbers and integer-valued JSON strings for known OAuth numeric fields.
- Preserve existing fallback and clamp behavior for absent, malformed, or out-of-range optional numeric values.
- Apply the helper to headless device-code polling interval parsing.
- Apply the same helper to token `expires_in` parsing so both headless and browser token exchanges tolerate the same upstream shape.
- Add focused automated tests for headless stringified numeric responses and token exchange stringified expiry.
- Keep diagnostics sanitized and avoid logging or surfacing raw OAuth payloads, codes, tokens, or authorization URLs containing secrets/state.

## 4 Non-Scope

- No new Codex OAuth authorities, scopes, redirect URIs, client identities, headers, or configurable endpoints.
- No Pi credential, settings, catalog, or runtime-state import.
- No change to the Codex Responses provider, model discovery contract, profile selection, context budgeting, or output-reserve semantics.
- No broad OAuth framework extraction or MCP coupling.
- No persistence schema migration and no token-cache format change.
- No user-facing command rename; existing `--codex-login`, `--tui --codex-login`, and `/auth openai-codex login` forms remain authoritative.

## 5 Current State

`OpenAiCodexOAuthManager.StartDeviceAsync` parses the device-code response and reads `interval` with `TryGetInt32`. If the upstream response contains `"interval":"5"`, the command fails with a generic JSON type error before printing a usable verification prompt.

`OpenAiCodexOAuthManager.PostTokenAsync` similarly reads `expires_in` with `TryGetInt32`. Even if the device challenge starts successfully, a stringified token expiry can fail the later token exchange in both headless and browser login paths.

Existing tests cover browser challenge validation, browser token persistence with numeric `expires_in`, malformed credential cache behavior, refresh cancellation, provider streaming, catalog discovery, and redaction boundaries. They do not cover headless device flow success or stringified OAuth numeric fields.

## 6 Proposed Design

Add a private Codex-OAuth parsing helper in `OpenAiCodexOAuth.cs` that reads an optional JSON property as a bounded integer:

- if the property is a JSON number, parse it with `TryGetInt32`;
- if the property is a JSON string, parse only a small bounded integer text form using invariant culture;
- if the property is missing, null, malformed, nonintegral, or outside `Int32`, return the caller-provided fallback path;
- after a successful parse, apply the existing clamp bounds at each call site.

Use the helper for:

- device-code `interval`, preserving the existing `1..30` second clamp and default of `5` seconds;
- token `expires_in`, preserving the existing `60..2,592,000` second clamp and default of `3600` seconds.

Keep the helper private to the provider project unless another implemented provider has real reuse. Do not introduce a generalized JSON compatibility layer for one upstream compatibility issue.

If related required string parsing is touched, preserve fail-closed validation and use sanitized `InvalidDataException` messages rather than raw `JsonElement` type exceptions or raw response content.

## 7 Public Contracts

No public DTO, persisted event, provider catalog, command, or configuration contract changes.

The observable contract is restored behavior: Codex login accepts upstream OAuth responses where known optional integer fields are either JSON numbers or integer strings. Invalid optional numeric fields continue to fall back to safe bounded defaults instead of widening authority or exposing response payloads.

## 8 Project/File Changes

- `src/Threadsmith.Models.OpenAiCodex/OpenAiCodexOAuth.cs` — Codex-local optional integer parser and call-site updates for `interval` and `expires_in`.
- `tests/Threadsmith.CodexProvider.Tests/Plan50OpenAiCodexTests.cs` — focused regression coverage for stringified `interval`, stringified `expires_in`, and the headless device flow path.
- `docs/implementation-plans/plan-93-codex-headless-auth-json-numeric-compatibility.md` — this maintenance plan.
- `docs/implementation-plans/README.md` — navigation row only.

User/operator docs need updates only if implementation changes command behavior, recovery guidance, or troubleshooting text beyond restoring the documented `--codex-login` behavior.

## 9 Ordered Tasks

1. Re-read the applicable DOX chain, Plan 50, ADR-40, the OpenAI Codex provider AGENTS file, and C# guardrails before editing code.
2. Inspect `OpenAiCodexOAuthManager` and existing Codex provider tests to confirm every direct OAuth numeric parse site.
3. Add a private helper for optional integer parsing from JSON number or bounded integer string.
4. Replace direct `TryGetInt32` optional parsing for `interval` and `expires_in` with the helper while preserving defaults and clamp ranges.
5. Add tests proving `StartDeviceAsync` accepts `"interval":"5"` and returns the expected bounded poll interval.
6. Add tests proving token exchange accepts `"expires_in":"3600"` for at least browser completion and the headless completion path.
7. Add a malformed stringified numeric test proving the code follows the safe fallback path and does not throw the generic `JsonElement` Number/String exception.
8. Run the focused Codex provider test project.
9. Run architecture tests if project references or public contracts changed; otherwise explicitly record why they were not required.
10. Run `dotnet build src\Threadsmith.sln --no-restore` after focused tests pass.
11. Perform the DOX/status pass and update user/operator docs only if a durable documented command, troubleshooting procedure, or observable error contract changed.

## 10 Testing

Focused automated tests:

- `StartDeviceAsync` accepts a device authorization response with `interval` as a JSON string.
- `StartDeviceAsync` preserves fallback behavior for a malformed optional string interval.
- `CompleteBrowserAsync` or token exchange coverage accepts `expires_in` as a JSON string and persists a valid Threadsmith grant.
- `CompleteDeviceAsync` covers the headless sequence with stringified `interval` and stringified token `expires_in` without generic JSON type failures.
- Existing malformed credential-cache tests continue to report unauthenticated status without throwing.

Regression commands:

```powershell
dotnet test tests\Threadsmith.CodexProvider.Tests\Threadsmith.CodexProvider.Tests.csproj --no-restore
dotnet build src\Threadsmith.sln --no-restore
```

Run `tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj` if the implementation changes references, public contracts, project structure, or dependency direction.

## 11 Security/Permissions

Stringified numeric compatibility must not expand OAuth authority. The parser accepts only known optional integer fields from already-approved Codex OAuth endpoints and only within existing bounds.

Do not log raw OAuth responses, authorization codes, device codes beyond the existing intended user prompt, access tokens, refresh tokens, callback URLs containing state/code, or account identifiers. Repository configuration remains unable to alter Codex OAuth authority, token endpoints, resource endpoints, or credential headers.

## 12 Observability

No new telemetry is required. Existing fatal-command behavior should no longer surface the generic JSON Number/String exception for compatible upstream responses.

If malformed OAuth responses still prevent authentication, diagnostics must remain bounded and sanitized, naming only the invalid response field or authentication phase without including raw response content.

## 13 Migration/Compatibility

No migration is required. Existing Threadsmith Codex credential caches remain valid. Users who encountered the failure can rerun:

```powershell
threadsmith --codex-login
```

or the installed executable equivalent after upgrading.

The browser flow benefits from the same token-expiry compatibility but retains its existing PKCE/loopback behavior.

## 14 Acceptance Criteria

- Headless `--codex-login` reaches the verification URI/user-code prompt when the device authorization response returns `interval` as a JSON string.
- Headless device-flow completion succeeds when the token response returns `expires_in` as a JSON string.
- Browser completion also accepts stringified `expires_in`.
- Absent or malformed optional numeric fields use existing safe defaults and bounds without throwing generic `JsonElement` type exceptions.
- No raw tokens, authorization codes, callbacks, account identifiers, or OAuth response bodies appear in logs, diagnostics, persisted events, tests, or documentation.
- No public provider-neutral contract, repository configuration authority, or Pi independence boundary changes.
- Focused Codex provider tests and the solution build pass.

## 15 Risks

- **Upstream OAuth shape drift:** limiting compatibility to known optional integer fields avoids accepting arbitrary schema changes while covering the observed stringified integers.
- **Overly permissive parsing:** use bounded invariant integer parsing and existing clamps; reject noninteger text rather than accepting floats or culture-specific formats.
- **Incomplete coverage:** cover both the initial device-code response and later token exchange because headless login can fail at either phase.
- **Accidental secret exposure in tests:** fixtures must use dummy values only and assertions must avoid printing request bodies containing sensitive fields unless sanitized.

## 16 Documentation

The current user and operations docs already advertise `threadsmith --codex-login` as the headless device flow. This maintenance item restores that documented behavior.

Update `docs/user-guide.md` or `docs/operations/model-providers.md` only if implementation adds a new durable troubleshooting message, recovery step, or limitation. Do not add completion/status prose to acceptance scenarios, manual tests, milestone details, README, or AGENTS files.

## 17 Open Decisions

None.
