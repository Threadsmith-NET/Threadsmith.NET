# AGENTS.md — .threadsmith/ (Repository Configuration)

> **Scope:** Threadsmith.NET repository configuration, repository skill catalog, and prompt-append files.

## Purpose

Configure how Threadsmith.NET behaves when operating on this repository. Config is **data, not code** — it is never executed directly.

## Ownership

- `config.example` — reference configuration showing every supported key, including default-on operation-duration display, default-on semantic Markdown answers, bounded cross-turn conversation mode, category budgets, artifact threshold, pressure, compaction limits, trusted NuGet advisory sources, and web-fetch narrowing bounds (strategy §21.2; ADR-31).
- `extensions.example.json` — reference for the repo-level extension selection file (`extensions.json`), which selects which discovered extensions auto-load at startup.
- `providers.example.json` — secret-free reference for dedicated user/repository provider catalogs and OpenAI-compatible endpoint/header settings.
- `prompts/` — repo-provided prompt-append files (§21.2). Untrusted input.
- `skills/` — optional metadata-first repository skill packages. Untrusted declarative data; never a trust store.

## Local Contracts

### Configuration (`config.example`)

- JSON-with-comments format (loaded by `Microsoft.Extensions.Configuration` Json provider).
- Ordinary configuration precedence: compiled defaults < machine < user < repo < session < CLI < env. Static secret values are outside this graph and resolve only through the host-owned environment → eligible repository → user providers.
  - machine: `%ProgramData%/Threadsmith/config.json` (admin-managed; trusted).
  - user: `~/.threadsmith/config.json` (user home; trusted, cross-platform). Scaffolded from the shipped `config.example` on first launch when missing; existing user configuration is never overwritten.
  - repo/session: `<repo>/.threadsmith/config.json` and `session.json` (per-key ordinary overrides only; untrusted, §22.2).
  - static secrets: separate strict stores at `~/.threadsmith/secrets/config.json` and optional `<repo>/.threadsmith/secrets/config.json`; never ordinary configuration providers.
- Every key listed in `config.example` is a documented Threadsmith.NET option.
- Bounded `model:http` transport scalars follow normal layering; cookies and the absence of a global `HttpClient` timeout remain non-configurable safety/correctness invariants.
- Successful solution selection atomically remembers nested `solution.path`; `/models` and `/reasoning` atomically preserve unrelated settings while writing complete nested `model.providerId`, `model.profileId`, and `model.reasoningLevel`, with repository intent taking precedence over user-catalog defaults; explicit selection overrides memory, valid memory auto-loads, and missing memory is cleared without weakening escape/prohibited/reparse validation. Empty-repository initialization writes only minimal neutral strict JSON, never overwrites, and omits `tools:enabled` to avoid a deny-all allowlist.
- `tui:defaultTheme` and ordered `tui:themes[]` define bounded presentation-only themes. Interactive `/theme` selection atomically updates only the user-layer default through a syntax-preserving edit that retains unrelated settings, comments, trailing commas, and surrounding formatting; higher ordinary layers may override it. Configured ids replace complete built-in/earlier entries case-insensitively; invalid roles, colors, controls, or UI settings fail before terminal rendering. `tui:footer:enabled` controls only the native-scrollback-safe composer-adjacent status projection; usage accounting remains active when hidden. `tui:showOperationDurations` is one presentation-only Boolean, defaults to `true`, and controls interactive request/tool/MCP duration text together without changing execution, telemetry, persistence, or headless contracts. `tui:renderMarkdown` also defaults to `true` and selects bounded complete-block semantic Markdown for new interactive answers; `false` restores terminal-safe source chunk cadence without changing raw transcript, persistence, context, restoration, or headless output.
- `model:profiles` is the ordered configured-model universe. Profile credentials retain logical secret references resolved only at activation through the static-secret resolver. Profiles intended for interactive general turns advertise `capabilities:toolCalls: true` so the host can expose read-only functions and `propose_plan` without a classifier round trip. `context:activeTurnCompaction:profileId` optionally selects a streaming structured-output `Summary` profile from repository-excluding machine/user/environment configuration and a repository-excluding user/machine/host-owned provider snapshot; repository configuration and repository provider-catalog additions/overrides cannot choose, rewrite, or reroute the auxiliary model, and repository secret providers cannot satisfy its credentials. Omission preserves the active main profile fallback.
- `skills:repositoryCatalogEnabled` can disable metadata discovery under `.threadsmith/skills`; it cannot establish signer keys, allowlists, enablement, or revocation exceptions. Those values come only from repository-excluding trusted configuration and the user-owned `~/.threadsmith/skill-policy.json`.
- `nuget:advisorySources` is honored only from repository-excluding trusted machine/user configuration, accepts bounded named HTTPS sources with optional username plus logical `secrets:` reference, and remains subject to per-invocation network-host and secret policy; resolved values exist only in the scoped child environment and repository configuration cannot redirect package-health queries.
- `webFetch` may narrow the compiled URL, redirect, timeout, compressed, decoded, and extracted-text caps; repository data cannot broaden security limits or grant outbound/direct authorization.
- `tools:parallel` configures sibling execution (`enabled`, host-clamped `maximumConcurrency`, and closed `failureMode`). Repository data may narrow execution but cannot make unknown or dynamic tools parallel-safe. `tools:enabled`/`disabled` control repository-scoped availability; `disabled` wins, an explicitly present `enabled` array is an allowlist, and essential tools remain enabled. `tools:allow`/`deny` independently narrow invocation policy; executable and network-host allowlists are evaluated centrally before invocation. The highest-precedence configured `tools:allowedExecutables` array replaces lower arrays rather than inheriting their numeric entries.
- `tools:listFiles|readFile|search|findSymbol|findReferences|findImplementations|runProcess` per-tool operational limits override compiled defaults; an input field of `0` means "use the host default". `tools:readFile:maxContentBytes` may narrow the compiled 50-KiB textual-content ceiling independently of the 2,000-line ceiling and 1-MiB readable-file-size boundary. `tools:runProcess:shellExecutable` selects the bare model-facing command shell and is conversation-available only when that shell also appears in `tools:allowedExecutables`; `null` retains the platform default (`powershell` on Windows, `bash` elsewhere), and the portable scaffold allowlists both defaults. Allowing a shell grants its complete composition language, including nested processes. The ordinary pipeline cannot prompt for process approval, so the tool is advertised only when trusted machine/user configuration sets `tools:runProcess:requireApproval=false`; the scaffold's general `tools:requireApproval` list does not duplicate that default gate, while repository configuration may add `run_process` there to reimpose approval.
- Arbitrary scalar tool settings live under `tools:config:<tool-id>`. `csharp_script` documents `timeout_ms`, `max_output_bytes`, and comma-delimited `allowed_assemblies`; it remains default-disabled and requires trusted execution regardless of repository data.
- `execution:maxModelRounds`, `execution:maxPlanningToolRounds`, `execution:maxCorrectiveTurns`, `execution:maxStructuredOutputCharacters`, `execution:toolResultPreviewCharacters` override the execution subsystem defaults. `execution:maxModelRounds` and `execution:maxPlanningToolRounds` are optional positive opt-in cutoffs; the default `0` disables each separate cutoff so exploration can continue until cancellation, tool policy, output accounting, or user-controlled budgets stop it. A positive planning-tool cutoff withholds inspection tools after an initial evidence window while retaining `propose_plan`. `execution:maxCorrectiveTurns` defaults to three bounded active-turn opportunities for the model to correct recoverable malformed or invalid requests before the host fails closed. `validation:stages` defaults to `semantic`, `compile`, `diagnostics`, and `tests`; repositories may explicitly narrow this post-approval list, but the reference configuration must not silently opt out of build/test validation. `validation:stages` does not disable always-on proposal/schema/path checks or pre-mutation Roslyn screening, which degrades explicitly when unavailable.
- `planning:approvalPolicy` defaults to `reviewAll`; `/plan-policy` persists every plan policy except `TrustSession` in repository settings, and `/plan-policy AlwaysTrustRepo` additionally writes `planning:approvalRepositoryIdentity` plus a matching user-owned plan-policy trust grant outside the repository. Repository content alone cannot grant identity-fenced plan trust, and plan policy never approves exact diffs or writes. `mutation:approvalPolicy` defaults to `reviewAll`; only `alwaysTrustRepo` is durably written by `/policy`, and selecting another policy removes that opt-in. `mutation:largeDiffThreshold` controls exact-preview changed-line classification for `ReviewRisky`; repository data cannot weaken invariant trust, path, baseline, prohibited/secret-path, or Git-metadata checks.
- `repository:configurationBytes` bounds ordinary repo/session configuration files; sourced from trusted machine/user/env layers only, never the repo config it guards. Static secret stores instead use the resolver's compiled strict 64-KiB bound.
- **Persistence, artifacts, and retention:** `persistence:path`, `persistence:artifactDirectory`, `persistence:retention:enabled`, `persistence:retention:sessionAgeDays`, `persistence:retention:metadataOnly`, and per-kind retention toggles (`retainFullPrompts`/`retainFullModelOutput`/`retainProcessLogs`/`retainSourceExcerpts`/`retainDiffs`/`retainTelemetry`/`retainSessionSummaries`). Ordered transactional schema migrations run at startup; a failed migration rolls back and leaves prior data readable (§19.5).
- **Redaction audit:** `persistence:redactionAudit:enabled` and `persistence:redactionAudit:repairArtifacts` — a defense-in-depth startup scan of persisted events/artifacts for unredacted secrets; artifacts are re-sanitized when `repairArtifacts` is set (event payloads are immutable history and are never rewritten).
- **MCP connections:** `mcp:defaultDrainKillTimeoutSeconds` and `mcp:profiles[]` — profiles carry stdio/SSE/streamable-HTTP transport, trust, scoped secrets, bounded timeouts, capability policy, optional environment/working directory, HTTP `headers`, OAuth, and `autoConnect`. Stdio commands must be bare executable names; arbitrary parent environment is not inherited. Inline HTTP header values are allowed; `secrets:` values resolve only when the exact reference is in `secretScope`. OAuth-enabled HTTP profiles use authorization-code + PKCE, advertised authorization-server metadata, configured-scope restriction, and a localhost callback; they may either provide a pre-registered `oauth.clientId`, provide an HTTPS `oauth.clientMetadataDocumentUri` with a fixed exact-match redirect port, or omit both to use SDK-backed dynamic client registration. They cannot also configure an `Authorization` header, and unsupported `oauth.discoveryUrl` overrides fail closed. Per-profile tokens and dynamic client-registration fields remain outside repository configuration in the user-owned secret cache. The shared lifecycle manager discovers allowed tools/resources/resource-templates/prompts on connect; imported tools default disabled and require repository/schema-bound user approval outside repository control in addition to repository-scoped narrowing availability, while resources/prompts remain explicit bounded untrusted operations. Imported tools remain governed by the standard tool pipeline.
- **Diagnostic bundles:** `diagnostics:enabled`, `diagnostics:directory`, `diagnostics:includeLogs`, `diagnostics:includeEvents`, `diagnostics:includeArtifacts`, `diagnostics:includeConfiguration`, `diagnostics:includeVersionInfo`, `diagnostics:maxBytes`, and `diagnostics:recentEventsPerSession` — secret-free support bundles (every entry sanitized before write; a canary-secret test gates the exit criterion).
- `invoke_skill` is a centrally governed default-enabled tool but can invoke only an explicit verified/enabled/compatible package during its phase boundary; package text cannot widen tool policy.
- Verified by `RepoConfigTests.cs` — all required keys must be present and the per-tool/execution/repository limit keys must bind to their documented values.

### Provider catalogs (`providers.json` / `providers.example.json`)

- `~/.threadsmith/providers.json` is the optional user base; `<repo>/.threadsmith/providers.json` is the optional repository override.
- Provider and nested model arrays merge by stable ID. Matching IDs inherit omitted properties, ordinary arrays replace, new entries append, and `enabled: false` disables without deleting.
- Explicit compiled `type` discriminators are allowlisted. Overrides cannot change an inherited discriminator, and catalog data cannot name CLR types or assemblies.
- Inline credentials are prohibited; only logical `secrets:` references survive unresolved until activation. OpenAI-compatible safe headers and resolved bearer authentication are applied request-locally.
- OpenAI-compatible `chatCompletionsPath` is relative beneath `baseUri`; root/authority/traversal/query/fragment escapes and credential, cookie, proxy, hop-by-hop, control, or excessive headers fail before dispatch.
- Both files are bounded and treated as immutable snapshots. Invalid JSON, unknown types, duplicate IDs, invalid defaults, or excessive input fail before provider invocation.
- Legacy `model:profiles[]` adapts only when neither provider catalog exists, emits one warning, writes nothing, and cannot be combined with a dedicated catalog.

### Prompt append files (`prompts/`)

- **`coding-standards.md`** — C# coding standards summary (references guardrails).
- **`domain-glossary.md`** — Domain terminology and subsystem descriptions.
- Appended to the model's system prompt at request-assembly time.
- **Untrusted input** (§22.2): sanitized + bounded, never executed as code, never overrides host policy or guardrails.
- Versioned and referenced by id+version in execution records (§11.6).
- Loaded in configured order after stable host policy and before phase instructions, with 32 KiB per-file and 64 KiB total default bounds.

### Extension selection (`extensions.json` / `extensions.example.json`)

- Repo-level ONLY: loaded from `<repo>/.threadsmith/extensions.json`. **Never** read from the user `~/` config (standing user instruction; the main user config is for model/host preferences, not extension selection).
- `discoveryDirectory` (default `.threadsmith/extensions`) — the directory scanned for extension packages (each subdirectory is an extension package, or the directory itself is a single package).
- `autoLoad` — extension ids to load at startup. Empty/absent loads nothing.
- Untrusted input (§22.2): a malformed file falls back to safe defaults (load nothing) rather than failing startup.
- Loaded by `ExtensionSelectionConfig.LoadOrDefault`; consumed by the App composition root, which constructs the `ExtensionHost`, discovers, and auto-loads. The interactive `/extensions` command drives load/unload at runtime via `IExtensionManager`.

## Work Guidance

- Never edit `config.example` to add undocumented keys — only keys defined in the strategy.
- Prompt-append files are untrusted input. Do not put secrets, credentials, or executable code in them.
- When adding a new config key, update `RepoConfigTests.RequiredKeys`.
- When adding a prompt-append file, reference it in `config.example` `"prompt append files"`.

## Verification

- `dotnet test tests/Threadsmith.Architecture.Tests/ --filter "FullyQualifiedName~RepoConfigTests"` — config tests pass.
- `dotnet test tests/Threadsmith.Planning.Tests/` — append confinement, order, sanitization, bounds, versioning, and boundary refresh pass.

## Child DOX Index

No child AGENTS.md files yet.
