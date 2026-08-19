# Plan 61 — Low-friction Web Fetch Authorization

**Milestone:** M22.1 — Low-friction Web Fetch Authorization

**Prerequisites:** plans 03, 08, 18, 20, 27, 35, 36, 40, 49, and 51–58

**Depends on by:** future bounded research workflows and external-document evidence

**Status:** Implementation complete; focused deterministic coverage passes. Maintained real-terminal/public-site compatibility closeout remains.

## 1 Objective

Make Plan-58 web retrieval convenient for ordinary documentation research without weakening its consent, policy, public-HTTPS, SSRF, redirect, transport, content, provenance, or untrusted-evidence boundaries.

An exact HTTPS URL authored in the current top-level user message becomes a bounded one-shot host-owned fetch candidate for that run, so a request such as `Read https://example.com/docs` needs no separate `/fetch-authorize` command. When `web_fetch` is already progressively active and the model proposes a different direct URL, the interactive host offers one explicit inline approval instead of requiring the user to leave the conversation and pre-authorize it. Headless execution remains deterministic and non-interactive, while `/fetch-authorize` remains available for pre-authorized redirect chains and automation.

## 2 Architectural Context

Plan 58 deliberately chose exact direct grants and opaque search-result references. Its network and ingestion controls are appropriate, but its only direct-URL user path requires a separate command. That adds avoidable friction when the user has already supplied the exact page in the current request. It also makes a fetch-ineligible search result or other model-proposed public page awkward: the model cannot pause at the normal tool boundary and ask for exact approval.

Threadsmith already distinguishes host-owned user input, model output, repository content, tool evidence, restored conversation, and extension/MCP/hook data. Plans 03 and 35 own current-turn intake and conversation provenance; Plans 08, 27, 51–55 own tool schemas, invocation policy, and canonical continuations; Plan 49 owns interactive activity; Plans 56–57 own safe transitions, approval serialization, and bounded sibling execution. This milestone reuses those boundaries rather than adding a second fetcher or relaxing Plan 58.

A URL appearing in user-authored input is authority only under a new explicit disclosure contract and only for the current top-level run. A URL produced by the model is never self-authorizing. Inline approval is a host-owned invocation decision, not model dialogue, repository trust, mutation approval policy, or durable origin allowlisting.

## 3 Scope

- Deterministic extraction of bounded absolute HTTPS URL candidates from the current raw top-level user message before model execution.
- Opaque user-message URL references bound to the exact message, repository, session, run, URL digest, consent/policy/tool generations, expiry, and one-shot use.
- Progressive `web_fetch` activation when a valid current-user URL candidate exists, without permanently advertising the schema.
- A revised disclosure/consent version explaining that exact URLs placed in a user request may be contacted when the model invokes fetch.
- Interactive inline approval for a model-proposed direct URL only while `web_fetch` is already legitimately active.
- Sanitized approval projection that identifies the public origin/path, indicates query presence without printing query values, and clearly distinguishes user-authored, search-derived, and model-proposed destinations.
- Exact one-invocation grant creation after affirmative approval, followed by the complete Plan-58 policy and transport pipeline.
- Deterministic headless rejection/projection for missing direct authority plus existing explicit pre-authorization parity.
- Retention of `/fetch-authorize <initial> [redirect ...]` for exact redirect groups, scripts, and advance authorization.
- Cancellation, timeout, approval serialization, safe-boundary handling, telemetry, diagnostics, tests, ADR-47, Scenario AA, documentation, and DOX.

## 4 Non-Scope

- Relaxing Plan-58 HTTPS, DNS/IP, connection pinning, redirect, credential, content sniffing, parser, byte, deadline, or provenance controls.
- Treating every URL in prior conversation, restored archives, memory, repository files, search snippets, fetched content, model output, extensions, MCP, hooks, logs, or clipboard metadata as user authority.
- Permanently advertising `web_fetch` or a general `request_network_access` tool on unrelated requests.
- Automatic fetching merely because a URL appears; network I/O still requires a model tool invocation and all host gates.
- Silent approval of model-proposed URLs, origin-wide/session-wide grants, remembered approvals, wildcard hosts, or repository-configured authority.
- Inferring authorization from model intent classification or asking a second model to classify the user's request.
- Interactive prompts in non-interactive/headless execution.
- Automatic direct-flow redirects. Every direct redirect remains pre-authorized in one exact invocation group through the existing explicit surface.
- Browser rendering, authentication, cookies, PDFs, binaries, downloads, crawling, or autonomous link traversal.

## 5 Current State

Plan 61 is implemented. Fresh raw user-message intake now issues bounded opaque `userUrlId` references under consent schema 3; the shared fetch tool supports current-user, search-result, explicit-direct-group, and exact inline-approved model routes. Interactive prompting is serialized through a TUI-neutral router, headless execution returns `DirectAuthorizationRequired`, run/session/repository/tool lifecycle revokes transient authority, and every executable route converges on the unchanged Plan-58 transport and extraction pipeline. Focused deterministic coverage is maintained in `WebFetchTests`; MTP-226 retains real-terminal and explicitly opted-in public-site closeout.

Before this implementation, Plan 58 supported two routes: an opaque one-shot search-result reference and an exact direct URL grant created through `/fetch-authorize` or its headless host boundary. The progressively disclosed fetch schema is scoped to eligible session/run state, and repository/session transitions revoke transient authority. Search results that fail stricter fetch URL policy remain visible without references.

The model cannot turn a URL from arbitrary text into authority. This is safe but forces a user who already typed an exact documentation URL to submit a second command. Interactive direct URL proposals also fail rather than entering a normal host approval flow. Existing retrieval-aware consent does not disclose current-message URL authorization and therefore cannot be silently interpreted as consent for the new behavior.

## 6 Proposed Design

### 6.1 Current-user URL candidates

At top-level intake, before request assembly or model execution, the host scans only the newly submitted raw user message. Use a deterministic bounded URI recognizer, not a model classifier. Initially recognize absolute `https://` bare URLs and Markdown link destinations only at message start or after a supported opening/token delimiter; reject embedded substrings such as `prefixhttps://...`, trim only well-defined surrounding punctuation, and preserve the exact protected transport URL separately from sanitized projection.

Apply Plan-58 structural URL normalization immediately. Invalid, credential-bearing, overlong, non-HTTPS, non-default-port, or excess candidates create no authority and perform no DNS/network operation. Cap candidates per message and total scanned characters. A URL span reaching the scan boundary creates authority only when the raw message ends at that boundary; reject it when any delimiter or continuation lies beyond the scan rather than authorizing a truncated prefix. Duplicate canonical URLs collapse deterministically.

For each valid candidate, issue an opaque `UserUrlReference` bound to:

- canonical repository identity;
- current `SessionId`, `RunId`, and user `ConversationMessageId`;
- exact URL digest and candidate ordinal;
- retrieval-consent, tool-availability, policy, and effective fetch-options generations;
- bounded expiry and schema version;
- one-shot consumption state.

The reference—not a model-repeated URL—is the preferred tool input. Candidate presence progressively activates `web_fetch` for continuations in that run. The exact transport URL remains protected authority state. The user message retains its ordinary conversation persistence, but restored or replayed messages never reconstruct live URL authority.

Under the revised disclosure, placing an exact URL in the current user message authorizes only one attempted fetch of that exact URL during that run. It does not contact the URL automatically, authorize redirects, authorize its origin, or survive the next top-level user message, run completion, repository/session transition, consent/tool/policy change, cancellation, or expiry.

### 6.2 Disclosure and consent migration

Introduce a new retrieval-consent schema version that plainly states:

- search terms may be sent to the configured provider;
- selected search results may be retrieved;
- an exact public HTTPS URL placed in the user's current request may be contacted if the model invokes `web_fetch`;
- model-proposed destinations require separate inline approval or explicit pre-authorization;
- fetched content is untrusted and supplied to the model.

Older Plan-58 schema-2 consent remains valid only for its existing search-result and explicit-command behavior. It must not silently enable current-message authorization. The first attempted use of the new route requires visible re-consent; denial leaves ordinary conversation and existing compatible search behavior available with zero fetch traffic. Consent remains user-owned and stored outside repository control.

### 6.3 Progressive schema and request contract

Keep `web_fetch` absent from unrelated model requests. The effective schema may become active through an eligible search reference, current-user URL reference, or existing explicit direct grant. Accept exactly one route per invocation:

- `searchResultId` for a host-issued search result;
- `userUrlId` for a current-message exact URL;
- `url` for an ungranted model-proposed direct destination; or
- the existing exact direct-group identity used by host pre-authorization.

Do not expose raw authority tokens in transcript text or status. Canonical tool generation changes only when effective activation changes. User URL references remain scoped to their producing run across model/tool continuation rounds and are removed before the next top-level turn.

### 6.4 Inline approval for model-proposed URLs

A direct `url` argument without an existing exact grant is an authorization request, not an executable network request. Before DNS or transport, the common pipeline validates structural URL policy and ordinary tool/consent/policy/hook availability, then asks the interactive host for one exact decision.

The approval projection shows:

- that the destination was proposed by the model and is not user-authored or search-result-authorized;
- canonical public origin and a conservatively redacted path shape that preserves only separators/depth and never prints non-empty path segments;
- whether a query is present, bounded query-key names when safe, and a non-reversible exact-URL digest, but no query values, user-info, fragment, opaque tokens, headers, or fetched content;
- that approval permits one attempt only, sends no ambient credentials, and does not authorize redirects or the origin;
- approve/deny choices, with deny/cancel as the safe default.

An affirmative answer atomically creates one exact invocation-bound grant and resumes that same invocation through Plan 58. Approval cannot be reused by another call, sibling, retry after terminal failure, session, run, or redirect. Denial returns a stable bounded tool result that the model may explain but cannot override. Approval prompts are serialized in canonical invocation order and never overlap; approval-wait time and network-operation time remain distinguishable. URL-free prompt lifecycle notifications travel only from the process-local prompt router to its attached interactive adapter, not through the durable domain-event stream.

No trust mode, mutation policy, repository setting, model preference, hook, extension, MCP server, fetched instruction, or prior approval may auto-approve a model-proposed URL. A managed policy may deny or narrow before/after the prompt but cannot grant.

### 6.5 Interactive, headless, and explicit surfaces

Interactive conversation handles inline approval at the existing host-owned prompt boundary without requiring the user to type a slash command. It must preserve native selection/paste behavior and display a concise numbered or yes/no decision. Cancellation returns to the composer without stale authorization.

Headless execution never opens an interactive prompt or reads opportunistically from stdin. An ungranted model-proposed URL returns a stable `DirectAuthorizationRequired` outcome with sanitized origin, redacted path shape, and digest data plus a non-secret machine-readable indication that explicit authority is required. The caller may use the existing exact headless authorization input and rerun under a fresh invocation identity according to the public headless contract.

`/fetch-authorize` remains supported for:

- pre-authorizing a known exact URL before a model turn;
- atomically authorizing an initial URL plus every expected redirect target;
- deterministic scripts and headless workflows;
- environments that disable or cannot render inline prompts.

Current-user URL authority covers only the initial URL. If it redirects, Plan-58 direct-flow rules stop before DNS for the redirect unless the whole chain was explicitly pre-authorized. The host may explain this and show the existing command shape without printing protected query values.

### 6.6 Security, policy, and lifecycle

All three routes converge before network I/O on Plan 58's current effective options and common gates: consent, tool availability, phase, policy/hooks, exact URL normalization, public DNS/address classification, connection-time pinning, manual redirect authorization, credential-free transport, bounds, content sniffing/extraction, cancellation, and untrusted-evidence projection.

Transient host-owned network claims admit only the exact current route and do not bypass other checks. User-message references and pending/approved inline grants are revoked on top-level run completion/failure/cancellation, next user intake, `/new`, `/resume`, `/clone`, `/open`, consent revocation, tool disablement, policy/options generation change, or shutdown. Repository-open rollback must restore only the prior repository's effective limits, never authority from the abandoned transition.

A URL in quoted repository text pasted by the user is technically part of that user message; the revised disclosure makes that behavior explicit. The host does not attempt semantic intent classification. Users who do not want a URL contacted can deny/disable fetch or avoid granting the revised route. The model remains instructed to invoke fetch only when relevant to the user's request; security does not rely on that instruction because authority is exact, one-shot, current-run-scoped, and still policy/transport constrained.

### 6.7 Observability and privacy

Record source kind (`SearchResult`, `CurrentUserMessage`, `ExplicitDirectGroup`, or `ModelProposedApproved`), one-shot consumption, and existing Plan-58 result/failure data only through the established query-free fetch result boundary. Approval request details and outcomes are process-local interaction state: do not publish them to the durable domain stream or record raw query values, protected exact URLs, sanitized origin/path/digests, opaque IDs, approval internals, headers, bodies, or extracted content.

Current-user messages retain their existing governed archive behavior; this plan adds no second durable raw-URL store. Live authority records are transient and non-restorable. Context inspection and `/tools` may report bounded activation/approval state and counts without listing URLs or tokens.

## 7 Public Contracts

Add or extend host-owned immutable contracts equivalent to:

- `UserUrlReference` metadata and source identity;
- `WebFetchAuthorizationSource.CurrentUserMessage` and `.ModelProposedApproved`;
- `DirectFetchApprovalRequest` / `DirectFetchApprovalDecision` / stable denial outcome;
- a TUI-neutral `IDirectFetchApprovalPrompt` application boundary;
- headless authorization-required projection and exit classification;
- revised consent disclosure/schema version.

Do not expose terminal, HTTP, DNS/socket, parser, provider SDK, extension, persistence-row, raw URL-query, or live authorization-handle types across subsystem boundaries. Reuse existing approval/prompt infrastructure when its lifetime and authority semantics fit; do not force direct-fetch approval into mutation approval policy.

## 8 Project/File Changes

- `Threadsmith.Tools` — current-user URL candidate/reference authority, direct approval state, source classification, common pipeline integration, and lifecycle revocation.
- `Threadsmith.Execution` / conversation intake owner — current-message provenance capture and run-scoped progressive activation refresh.
- `Threadsmith.Context` — bounded user-URL reference projection and canonical tool-generation handling without replay-derived authority.
- `Threadsmith.App` — shared composition and repository/session lifecycle integration.
- `Threadsmith.Tui` — inline approval prompt and sanitized status/error projection.
- `Threadsmith.Cli` — deterministic non-interactive authorization-required output and explicit grant parity.
- consent/persistence owner — revised disclosure version and fail-closed migration; no durable live grant restoration.
- Focused tests in existing milestone suites; add no product project solely for this feature.
- ADR-47, Scenario AA, milestone/plan indexes and DAGs, manual tests, operations/user/configuration docs when implemented, and applicable DOX.

Any new project-level fixture copied to output uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inspect current user-intake provenance, conversation archive creation, Plan-58 authority/options state, progressive registry refresh, tool approval handling, TUI prompts, headless outcomes, and safe transitions.
2. Add ADR-47 amending ADR-44 with current-user URL authority, inline direct approval, non-interactive behavior, and unchanged Plan-58 hard controls.
3. Version retrieval consent and add exact disclosure/re-consent behavior without broadening older records.
4. Define bounded deterministic current-message URL recognition and opaque reference contracts; prove restored/model/tool/repository content cannot issue them.
5. Bind references to message/repository/session/run/generations/expiry and implement one-shot consumption plus complete lifecycle revocation.
6. Extend progressive tool activation and canonical schema projection for current-user references while preserving dormant inventories.
7. Add direct URL authorization-request classification before DNS/network and the TUI-neutral approval boundary.
8. Implement serialized interactive prompting, affirmative exact invocation-group creation, denial/cancellation, and sanitized display.
9. Implement deterministic headless authorization-required projection and reuse explicit direct-chain authorization for automation.
10. Converge every route on the existing Plan-58 common policy, transient network claims, repository-bound effective limits, transport, extraction, provenance, and activity paths.
11. Add focused unit/integration/TUI/headless/canonical/lifecycle/security tests and Scenario AA fixtures.
12. Update milestone/shared context/plan indexes/DAG, acceptance scenarios, documentation DOX, and—only when implementation ships—user guide, operations guide, configuration, manual test plan, root status, and source/test DOX.

## 10 Testing

Automated coverage must verify:

- one exact bare or Markdown HTTPS URL in the current raw user message creates one opaque reference and activates `web_fetch` only for that repository/session/run;
- duplicate URLs collapse, punctuation is handled deterministically, and malformed/non-HTTPS/credential-bearing/non-default-port/overlong/excess candidates create no authority and no network activity;
- URLs from prior/restored messages, governed memory, system/developer prompts, repository files, search snippets without references, fetched content, model output, extensions, MCP, hooks, logs, or tool results cannot create `userUrlId` authority;
- legacy consent does not enable the new current-message route; disclosure acceptance enables only the documented behavior and denial performs zero fetch traffic;
- a valid `userUrlId` is one-shot, exact-URL-bound, expiry/generation-fenced, and rejected after replay, next top-level turn, run terminal state, repository/session transition, consent/tool/policy/options change, cancellation, or shutdown;
- candidate creation alone performs no DNS/network I/O; invocation still passes all Plan-58 URL, public-address, connection, redirect, credential, bound, content, and evidence checks;
- unrelated requests retain the smaller canonical tool inventory and activation changes/restoration are deterministic;
- an ungranted model-proposed direct URL triggers no DNS/network before interactive approval;
- approval display identifies model provenance, origin/path/query presence/digest, omits query values/secrets, and defaults safely on denial/cancellation;
- affirmative approval authorizes exactly the pending invocation once; another call, sibling, retry, redirect, session, or run cannot reuse it;
- approval prompts serialize in original invocation order, do not overlap, obey cancellation/deadline/safe-boundary rules, and do not deadlock Plan-57 batches;
- model/repository/content/hooks/extensions/MCP and trust/mutation policies cannot approve, remember, widen, or convert approval into an origin/session grant;
- headless mode never prompts, blocks on stdin, or silently grants; it returns a stable sanitized authorization-required outcome and explicit pre-authorization succeeds through the same common fetch path;
- `/fetch-authorize` continues to support one exact redirect group, while current-user and inline single-URL routes cannot follow an unapproved direct redirect;
- source-kind telemetry, activity, context inspection, diagnostics, and support bundles remain bounded and secret-free;
- existing Plan-58 search-result/direct grants, repository rebind, lifecycle revocation, SSRF, content sniffing, parser, provenance, canonical continuation, TUI, CLI, and architecture tests remain compatible.

## 11 Security/Permissions

This milestone changes authorization ergonomics, not network trust. Exact current-user URLs are treated as a narrow user action only after explicit revised disclosure. They are current-run, exact, one-shot, and non-restorable. Model-proposed URLs remain denied until an affirmative host-owned interactive decision or pre-existing explicit grant.

No route bypasses public-HTTPS validation, SSRF/DNS rebinding defenses, connection pinning, redirect authorization, ordinary tool policy, consent, hooks, budgets, cancellation, credential isolation, content bounds, or untrusted-evidence treatment. Approval is not a trust decision about the site or content.

The host must never use a model classifier to decide whether URL text is user-authored. Provenance comes from the raw intake boundary. It must never derive live authority from the conversation archive on resume/clone. Repository configuration may disable or narrow behavior but cannot enable consent, issue references, approve model URLs, widen limits, or remember destinations.

## 12 Observability

Emit secret-free structured events/telemetry for candidate count, reference issue/consume/reject reason, activation source, approval request/outcome, sanitized origin identity/digest, headless authorization-required outcome, and existing fetch timing/failure data.

Never log or persist protected exact URLs solely for authorization, query values, URL credentials, opaque IDs, model raw arguments, prompt interaction internals, headers, content, DNS answers, or transient grants. Approval-wait duration is not network duration. Diagnostic bundles retain canary redaction and bounded classifications only.

## 13 Migration/Compatibility

Existing search-result references and explicit direct groups retain their behavior. Consent schema 2 remains valid for its original Plan-58 routes but does not authorize current-message URL inference. A new schema requires one visible re-consent before the ergonomic route is enabled.

The model-visible request contract grows only when `web_fetch` is progressively active. Providers/caches see an intentional tool-generation change under existing Plans 51–55 rules. Older persisted sessions restore conversation text but no current-user references, pending approvals, or grants. No SQLite migration is needed unless inspection proves the consent owner requires it; any migration must be ordered and fail closed.

Interactive clients that cannot render the approval boundary deny model-proposed direct URLs and retain `/fetch-authorize`. Headless callers retain deterministic explicit authorization. Extensions and MCP are unaffected.

## 14 Acceptance Criteria

- `Read https://public.example/docs` can progress from one user message to one exact one-shot `web_fetch` invocation without a separate slash command after the revised disclosure is accepted.
- Merely recognizing a user URL performs no network I/O; every invocation still traverses all Plan-58 controls.
- Current-user authority is raw-intake-proven, message/repository/session/run/generation/expiry-bound, one-shot, non-restorable, and unavailable to every non-user source.
- A model-proposed direct URL performs zero DNS/network activity until explicit inline approval; approval grants only that exact pending invocation.
- Interactive denial/cancellation is safe and conversational; headless mode never prompts or silently authorizes and reports a stable actionable outcome.
- `/fetch-authorize` remains available for exact redirect chains and automation, and neither ergonomic route implicitly authorizes redirects or origins.
- Unrelated turns retain the smaller tool schema; canonical continuation, approval serialization, activity, lifecycle revocation, and repository option rebinding remain correct.
- Existing consent, policy, SSRF, transport, content, provenance, privacy, and untrusted-evidence guarantees are unchanged.
- Focused deterministic tests, ADR-47, Scenario AA, documentation, milestone/index/DAG/shared-context updates, and DOX pass before M22.1 is marked complete.

## 15 Risks

- **Accidental contact from a mentioned URL:** revised disclosure is explicit; recognition is bounded to the current raw user message; authority is exact/current-run/one-shot; no I/O occurs until the model invokes fetch; users can deny/disable fetch.
- **Model social engineering through inline approval:** prompts identify model provenance, reveal bounded destination identity, default to deny, and never offer remembered/session/origin approval.
- **Prompt flood or concurrency deadlock:** one bounded pending request per invocation, deterministic serialization, aggregate approval limits, cancellation, and no concurrent approval UI.
- **Archive replay becomes authority:** live references are minted only at fresh top-level intake and never reconstructed from persisted conversation.
- **Query leakage in UI/telemetry:** display origin/path plus query presence/key names/digest, not values; keep exact URL in transient protected state.
- **Schema growth:** activation remains progressive and current-run scoped.
- **Redirect friction remains:** intentional; predictable chains use `/fetch-authorize`, preserving exact atomic redirect authority.
- **Consent silently broadened:** version and re-consent rather than interpreting schema 2 as permission.

## 16 Documentation

Planning adds Plan 61, M22.1 detail/index/DAG entries, Scenario AA, shared-context registration, and root/docs DOX/status references. Implementation must add ADR-47, update the user guide and `docs/operations/web-fetch.md` with natural-language URL and inline/headless flows, update configuration and the maintained manual test plan where applicable, and refresh source/test DOX. Planned behavior must not be described as currently available.

## 17 Open Decisions

Resolved for planning:

- M22.1 improves authorization UX but does not relax Plan-58 transport or ingestion controls.
- Exact URLs are recognized only from the fresh raw top-level user message, not from replayed or non-user content.
- Current-user URLs use opaque one-shot references and progressively activate the existing fetch schema.
- A revised disclosure/re-consent is required before current-message URLs carry authority.
- Model-proposed URLs require exact inline approval and can be proposed only while `web_fetch` is already legitimately active.
- Interactive approval is one invocation only; no remembered origin/session grant is introduced.
- Headless mode is non-interactive and fail-closed.
- `/fetch-authorize` remains the redirect-chain and automation surface.
- Direct redirects remain explicitly pre-authorized as one atomic group.
- ADR-47 amends, rather than rewrites, ADR-44.

Resolved in implementation:

- The recognizer scans at most 32 KiB, accepts at most eight unique normalized bare/Markdown HTTPS destinations, and trims only defined terminal punctuation plus unbalanced closing delimiters.
- A dedicated `IDirectFetchApprovalPrompt`/serialized router keeps transient network approval separate from mutation approval policy while reusing ordinary approval events for bounded outcome observability.
- One prompt may execute at a time, and `web_fetch` serializes per registration so sibling prompts retain canonical order.
- Headless/unattached execution returns `ToolErrorClassification.DirectAuthorizationRequired` and never reads stdin.
- Approval projection uses bounded origin/path, query-present state, and exact digest; it deliberately omits query-key names as well as values.
