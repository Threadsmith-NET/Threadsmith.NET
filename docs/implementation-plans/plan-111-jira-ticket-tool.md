# Plan 111 - Jira ticket access through the standard tool pipeline

**Status:** Active verification; deterministic implementation, adversarial corrections and documentation are complete, and the unscoped live matrix is partially verified while the remaining section 10 gates stay pending.
**Delivery track:** M31 governed Jira issue reads. This adds observable product behavior, not behavior-preserving Maintenance, and does not reopen completed milestones.
**Prerequisites:** Existing central tool registration/policy/invocation, trusted layered configuration, Secrets resolution, outbound consent, shared conversation/skill/child execution, and deployed prompt assets. The implemented `pr_fetch` path from [Plan 110](plan-110-provider-backed-pr-fetch.md) is the reference implementation. No pending review coordinator, Jira SDK, MCP server, or new secret provider is required.

## 1 Objective

Add one model-facing `jira` tool that reads a Jira ticket through the Jira API and returns its description as a readable body, with enough identity and provenance to attribute the result. A required `kind` discriminator initially accepts only `"read"`; later issue editing and status transitions can extend the tool without renaming it.

Ticket URLs are a first-class input: users can paste a supported ticket link without extracting its key themselves. The output window must show useful tool progress, including the `kind` operation and ticket key/number. Return the ticket body to the agent in the normal tool result; do not automatically dump it into tool-progress output. When the user asks to see the description, the agent displays it in its normal response using that result, just as it can list changed files returned by `pr_fetch`.

Use the existing tool configuration, Secrets, HTTP composition, policy, activity, and output pathways. Initial support is Jira Cloud REST API v3 using an Atlassian account email and API token. "Ticket body" means the standard `fields.description`, not comments, attachments, linked tickets, or site-specific acceptance-criteria fields.

## 2 Architectural Context

Read [AGENTS.md](../../AGENTS.md), [planning governance](planning-governance.md), [shared context section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../guardrails/portable-csharp-guardrails.md), [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md), [ADR-08](../architecture/adr-08-policy-gated-side-effects.md), and [ADR-48](../architecture/adr-48-extensible-secret-discovery.md) before implementation.

- `Threadsmith.Tools` owns the tool, host-owned input/output records, account options, Jira HTTP client, and description projection. Group these under `Jira`; use existing projects and `System.Net.Http`/`System.Text.Json`.
- `Threadsmith.App` owns trusted configuration, composition, pooled client lifetime, and registration. The tool receives the existing `ISecretResolver` and `IPromptLoader`.
- All entry points use `ToolInvocationPipeline`. No special Jira execution loop, skill runner, command, persistence store, or terminal renderer is introduced.
- Provider content is untrusted evidence. It cannot authorize tools, change host instructions, cause linked-resource fetches, or grant permission to mutate an issue.
- Cancellations, DTO boundaries, redaction, resource limits, packaging, and dependency direction remain governed by the existing contracts.

## 3 Scope

- One `jira` tool with required `kind: "read"`, a ticket key or supported `/browse/{key}` URL (including trusted browse-host aliases), and optional configured provider/account ID.
- Trusted named account profiles under `tools.jira.providers`, analogous to `tools.prFetch.providers`.
- Jira Cloud API tokens, including explicit routing for unscoped tokens and tokens with scopes.
- One bounded Get issue request for description and small attribution fields, with readable ADF projection and explicit coverage limitations.
- Existing enablement, outbound consent, network/secret policy, timeouts, duplicate protection, activity, sanitized failure, and provenance handling.
- Visible tool activity showing operation kind, ticket key/number, selected account, and completion/failure/cancellation state through the existing output-window lifecycle.
- Deterministic adapter/pipeline tests, setup documentation, prompt deployment, and focused manual verification instructions.

## 4 Non-Scope

- Edit/create/delete/comment/transition operations, bulk requests, JQL search, projects/boards/sprints, and workflow discovery.
- Comments, changelog, attachments, linked content, custom fields, rendered HTML, or downloading media embedded in a description.
- Jira Data Center/Server, government cloud, custom-domain API endpoints, arbitrary API base URLs, browser cookies, passwords, OAuth onboarding, or automatic cloud-ID discovery. Trusted custom-domain browse aliases are input-only mappings, not new API endpoints.
- Arbitrary Jira board/search/short-link routes and a validated granular-only token-scope recipe. The initial scoped-token setup uses the classic read scope defined in section 6.2.
- Cross-session or operation caches, cursors, refresh flags, background polling, or a general issue-tracker/provider framework.
- Changes to review orchestration or maintained skill contracts. Agents can use ordinary tool calls to obtain ticket context.

## 5 Current State

The inspected checkout has no Jira client, Atlassian Document Format converter, or Jira tool. Existing references to Jira in review prompts treat ticket text as requirements supplied to the model.

| Concern | Existing code and practical reuse |
|---|---|
| Tool contracts and admission | `src/Threadsmith.Tools/ToolContracts.cs`, `ToolDefinitionFactory`, `ToolInvocationPipeline.cs`, `ToolRegistry.cs`, and `ToolRuntimeOptions.cs`. Register a normal typed tool and retain wrappers/limits. |
| Account profiles and trust | `src/Threadsmith.Tools/PullRequests/PrFetchOptions.cs`. Follow stable dictionary IDs, trusted account binding, and repository-only disabling/narrowing. Do not couple Jira options to PR records. |
| Selection and model description | `PrFetchTool.ResolveProvider`, `GetSecretReferences`, `GetNetworkHosts`, and `DescribeActivity`. Reuse the pattern; Jira selects by exact configured site rather than PR URL globs. |
| HTTP lifecycle | `src/Threadsmith.App/PrFetchComposition.cs` and `HostFoundation.cs`. Follow injected pooled clients, fixed endpoint construction, public-only connection checks, and disposal on startup failure and normal shutdown. |
| Shared network protection | `PublicIpAddressPolicy` in `src/Threadsmith.Tools/WebFetch.cs`. Reuse `EnsureAllPublic` and `ConnectAsync` directly for DNS validation and connection to the validated address. |
| Credentials | `src/Threadsmith.Tools/SecretResolution.cs`. Use `SecretReference`, `SecretResolutionRequest`, `ISecretResolver`, and `SecretValue.RequireValue` only at the HTTP boundary. |
| Output | `ToolExecution<T>`, `ToolProvenanceSource`, `JsonOutputSanitizer`, and the pipeline's serialized-output ceiling. Extend the existing `IPostSanitizationToolOutputBoundary` used by `CodeExploreOutputFormattingTool` for effective-budget delivery; see section 6.4. No Jira-specific event store or sanitizer. |
| Visible progress | `DescribeActivity` and `ToolExecution<T>.TransientActivityDetail` feed `InteractionPresentationFormatter.CreateToolActivity`, `GetToolDetail`, and `FormatToolCompletion`. `InteractionOperationActivities`, `ConversationTranscript`, and `AgentWorkspaceProjection` already consume this shared lifecycle; populate these existing fields rather than add a Jira renderer or print result bodies. |
| Consent presentation | `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs:GetOutboundConsentPrompt` currently special-cases `pr_fetch` and otherwise says Web Search. Add accurate Jira wording in this existing path. |
| Model assets | `src/Threadsmith.Core/PromptContracts.cs`, `src/Threadsmith.Tools/Prompts`, and prompt-asset architecture/publish checks. |
| Regression examples | `tests/Threadsmith.ModelTooling.Tests/PrFetchTests.cs`, secret-resolution tests, and existing policy/availability tests. |

`PullRequestProvider` contains useful authenticated bounded-GET patterns, but its protected/private helpers depend on PR account options, PR targets, repository route prefixes, metadata, and pagination. Jira must not inherit from it or pretend to be an `IPullRequestProvider`. The concrete reason for separate Jira adapter code is different tenant routing and document semantics, not a different execution lifecycle. Share the existing public network helper now; only extract another narrow helper if implementing both actual call sites demonstrates a net reduction in complexity and preserves PR behavior.

`PrFetchCache` solves paged, revision-bound PR acquisition. A single issue-description request does not justify generalizing it. The existing Markdown presentation layer parses Markdown, and `WebFetch` extracts HTML; neither is an ADF reader or an appropriate dependency for `Threadsmith.Tools`.

## 6 Proposed Design

### 6.1 Tool and input selection

Use `Tool<JiraInput, JiraReadOutput>` with ID `jira`, display name `Jira`, `ToolCategory.ExternalSearch`, `ToolSideEffect.ReadOnly`, `ApprovalLevel.None`, and `RepositoryTrustLevel.UntrustedInspection`, matching the PR read path. Set `EnabledByDefault = false`, `RequiresOutboundConsent = true`, and retain default duplicate rejection. None of these metadata values bypass normal policy or consent.

Inputs:

```json
{"kind":"read","issue":"APP-123","provider":"work-jira"}
```

```json
{"kind":"read","issue":"https://example.atlassian.net/browse/APP-123"}
```

`kind` is a closed string discriminator, required in the schema and runtime. Reject missing/unknown/numeric values, including future operation names, before Secrets or HTTP access. The shared JSON enum converter permits integers by default, so a schema-only enum declaration is insufficient: use a strict converter or explicit string validation local to this contract.

`issue` accepts a bounded issue key or an absolute HTTPS `/browse/{key}` URL, with an optional trailing slash. Initial key grammar is `[A-Za-z][A-Za-z0-9_]*-[1-9][0-9]*`, maximum 128 characters; normalize letters to uppercase. URLs are limited to 2048 characters and must match the selected trusted site's exact host or an explicitly configured `browseHostAliases` entry, default port, and browse route. Use structured URI parsing to extract the site and key locally; neither the user nor the model must split the URL first. Reject user-info, backslashes, encoded separators, extra path components, API URLs, and unsupported hosts. Numeric issue IDs are not model inputs in this first version.

Accept query strings and fragments on copied browse links, but strip them during local normalization. They are not API parameters, alternate issue identifiers, redirect instructions, or credential destinations. Never forward or echo them in API requests, activity, canonical result URLs, or provenance. Account selection uses the parsed site and the key from the validated browse path only. Unsupported board/search/short-link routes return an actionable request for a browse link or key; do not guess from arbitrary URL text or follow redirects.

For example, `https://example.atlassian.net/browse/app-123?source=notification#description` becomes site `https://example.atlassian.net`, key `APP-123`, and canonical URL `https://example.atlassian.net/browse/APP-123`. With one matching account, supplying this full URL in `issue` produces the same API request as the equivalent key/account call, without fetching the browse page. Both scoped and unscoped profiles support this workflow.

An explicit `provider` selects one enabled configured account and still requires URL/site-or-alias agreement. Without it, a URL selects enabled accounts whose site or alias matches; a bare key selects only when exactly one account is enabled. Zero matches produce a setup/selection error; multiple matches require an explicit choice. Ambiguity text begins with the instruction to supply `provider`, lists at most three whole safe IDs in ordinal order, and reports the remaining count within 384 characters, before the existing presentation cap (currently 512 by default). A lower presentation cap may further shorten it; never depend on a complete account inventory in an error. Never probe accounts to find one that can access the issue. Resolve once consistently for validation, policy declarations, activity, and execution.

Browse aliases support copied custom-domain links without trusting their network destinations. For example, a trusted mapping of `issues.example.org` to the configured `example.atlassian.net` account accepts `https://issues.example.org/browse/APP-123`; the request still targets the configured tenant or gateway, and result/provenance URLs use `siteUrl`. Aliases are exact DNS names, never wildcards, inferred suffixes, redirects, or model-supplied configuration. Document the supported route explicitly, not "all Jira URLs." Capture sanitized real Jira Copy Link fixtures before release; unsupported routes must fail locally with the key/browse-link remedy, not silently expand scope.

### 6.2 Standard configuration and Secrets

Illustrative trusted user/machine configuration; credentials are references, never inline values:

```json
{
  "tools": {
    "jira": {
      "providers": {
        "work-jira": {
          "type": "jiraCloud",
          "enabled": true,
          "siteUrl": "https://example.atlassian.net",
          "browseHostAliases": ["issues.example.org"],
          "endpointMode": "scopedGateway",
          "cloudId": "11111111-2222-4333-8444-555555555555",
          "authentication": {
            "mode": "basic",
            "username": "developer@example.org",
            "secretReference": "secrets:jira:work-token"
          }
        }
      },
      "maximumResponseBytes": 2097152,
      "maximumBodyBytes": 65536,
      "timeoutSeconds": 30
    }
  }
}
```

The proposed default output ceiling is 256 KiB, enforced through the standard tool runtime options. The response ceiling defaults to 2 MiB and the readable-body ceiling to 64 KiB of UTF-8, independently of JSON escaping. Jira's response/body/timeout options use positive finite values; zero is invalid for these new settings. Repository Jira overrides may lower trusted limits and disable accounts, but cannot add/enable accounts, change site/alias/endpoint/authentication bindings, or increase Jira limits. The effective standard runtime output cap remains authoritative, including when smaller than these defaults. Profile configuration is fixed until restart, as with `pr_fetch`; resolved secret values are not cached by Jira.

Validate stable provider IDs using the PR rule (at most 80 ASCII letters/digits/hyphens/underscores), known type/mode fields, unknown configuration members, and required authentication fields. `siteUrl` must be an HTTPS origin with exactly one tenant label before `.atlassian.net`, default port, and no path beyond `/`, user-info, query, or fragment. `scopedGateway` requires a UUID cloud ID; `site` omits it. Do not infer endpoint mode from token contents or fall back between modes on failure.

Bound configuration before generating prompts: at most 16 profiles total, including disabled entries, and four aliases per profile. Canonical DNS hosts are ASCII/lowercase, at most 253 characters, with standard DNS label validation; reject IP literals, single-label/local names, trailing-dot variants, ports, paths, and user-info in aliases. Reject duplicates within a profile after normalization; cross-profile host overlap requires normal explicit account selection. Bound `siteUrl` to 261 characters, email to 254, cloud ID to the canonical 36-character UUID representation, and secret-reference text to 512 plus the existing reference parser's rules. Reject over-limit settings, never silently drop profiles. Build `Providers` deterministically in ordinal ID order from enabled IDs and canonical site hosts only, at most 8 KiB UTF-8; reject an over-budget configuration before registration. Do not include aliases, emails, secret references, or arbitrary configured labels in that token.

Both scoped and unscoped API tokens are required in this initial implementation. Atlassian documents email-plus-token Basic authentication and separate gateway routing for scoped tokens. In `site` mode construct `https://{configured-site}/rest/api/3/...`; in `scopedGateway` mode construct `https://api.atlassian.com/ex/jira/{configured-cloudId}/rest/api/3/...`. `siteUrl` remains the browse URL identity in both modes. The trusted operator owns the site/cloud-ID association. [Basic authentication](https://developer.atlassian.com/cloud/jira/platform/basic-auth-for-rest-apis/), [token and endpoint documentation](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/).

| Token variant | `endpointMode` | `cloudId` | Authentication |
|---|---|---|---|
| API token with scopes | `scopedGateway` | Required, trusted configured tenant UUID | Basic: account email plus resolved token |
| API token without scopes | `site` | Omitted | Basic: account email plus resolved token |

For an unscoped account, use the same profile shape with `"endpointMode": "site"`, remove `cloudId`, and reference that account's unscoped token through `authentication.secretReference`. Token scope selection happens in Atlassian; Threadsmith does not store or infer scope claims in ordinary configuration. Jira still applies account permissions for both variants.

The supported scoped-token setup selects the classic `read:jira-work` scope, the Get issue reference's recommended classic scope. The account also needs Browse projects and applicable issue-security access; scopes do not override those permissions. Selecting only `summary,description,updated` does not establish that a smaller granular scope set suffices. A granular-only least-privilege recipe is explicitly deferred until the complete endpoint scope set is documented and verified; do not reject an opaque token locally based on presumed claims, and do not advertise unverified granular combinations. No write/admin scopes are requested. [Get issue permissions and scopes](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/#api-rest-api-3-issue-issueidorkey-get).

Declare the selected logical reference via `GetSecretReferences`. Resolve with a Jira component ID, non-secret read purpose, and `MinimumTrust = UserOwned`. This deliberately matches `pr_fetch`: environment/user-store secrets work through existing providers; repository-owned secrets cannot satisfy this credential request. Never read secret files, enumerate environment variables, resolve secrets during startup/schema generation, or store authorization on shared `DefaultRequestHeaders`. Build the per-request header immediately before sending and do not retain the resolved value.

Rotation uses the same reference and the existing secret provider's refresh semantics on the next permitted invocation; Jira must not cache the resolved token or Basic header. A changed process environment may require host restart, unlike an update visible to a file-backed resolver. Do not assume token length/format or infer expiry from token bytes. Missing, revoked, expired, and under-scoped credentials produce safe remediation, never endpoint fallback or anonymous retry. The service may not distinguish expiry from other 401 causes; report authentication failure, not an invented diagnosis.

### 6.3 API operation and transport

Call the Jira Cloud Get issue endpoint with a host-constructed route:

```text
GET {configured-api-root}/rest/api/3/issue/{escaped-key}?fields=summary,description,updated&updateHistory=false
Accept: application/json
```

Return Jira's issue `id` and returned `key`, summary, description projection, and `updated` timestamp. Build the canonical browse URL from the trusted site and validated returned key, not response `self` links. Jira may resolve an old/moved key and return its current key without an HTTP redirect; preserve both requested and returned identity. Access depends on the account's project and issue-security permissions. [Get issue API](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/#api-rest-api-3-issue-issueidorkey-get).

Make one request on the success path: no preliminary account, project, fields, cloud-ID, issue history, or discovery calls. Request no expansions or other fields. Explicitly avoid view-history updates. The adapter exposes a typed read method taking the selected account, key, and `CancellationToken`; no arbitrary method/path/body/header API is model-visible.

Use the PR composition settings: no cookies, proxy, or automatic redirects; 15-minute pooled connection lifetime; 10-second connection timeout; invocation-owned total timeout; `ResponseHeadersRead`; and cancellation through DNS, connection, Secrets, send, streaming read, and projection. Reject every 3xx in this first version. The expected endpoint does not require redirect handling, so no redirect can change tenant, route, or credential destination.

`GetNetworkHosts` declares the actual API host (`api.atlassian.com` for gateway mode, the configured tenant host otherwise). Validate all resolved addresses using `PublicIpAddressPolicy` and connect only to a validated address. HTTPS certificate checks stay enabled. Response links, ADF links, and user text cannot create further network requests.

Count bytes as the response is streamed, including decompressed bytes; do not rely on `Content-Length`. Require JSON and apply an explicit JSON depth bound of 64. Exceeding the transport/JSON bound or receiving malformed JSON fails clearly: a cut-off JSON document is not a trustworthy partial issue. A valid in-bound response whose readable description exceeds projection/output limits instead returns the bounded partial result in section 6.4. The ordinary pipeline still owns final serialized and sanitized output enforcement, including lower per-tool overrides and JSON-escaping expansion.

Return sanitized distinctions for invalid arguments/configuration, missing credential, 401 authentication failure, 403 access denial, 404 not-found-or-inaccessible, 429 throttling, 5xx provider failure, transport failure, timeout, cancellation, malformed response, and output/resource limits. Map these through existing tool error/result mechanisms where available; do not replace the pipeline's failure lifecycle with an unrelated success envelope. Never forward raw response bodies, headers, or transport exceptions to logs/model context.

No automatic retry in this first step. For 429, include a bounded, parsed retry delay when provided; do not sleep beyond the invocation deadline or encourage immediate identical calls. Atlassian specifies `Retry-After` for throttling. [Rate limiting](https://developer.atlassian.com/cloud/jira/platform/rate-limiting/).

### 6.4 Ticket body semantics

Jira v3 descriptions use Atlassian Document Format (ADF), a structured JSON document, not an ordinary Markdown string. Convert locally to readable plain text with deterministic structure. Do not request rendered HTML or run a browser/JavaScript converter. [Jira v3 format support](https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro), [ADF structure](https://developer.atlassian.com/cloud/jira/platform/apis/document/structure/).

Use a small internal bounded `JiraDescriptionReader` over `JsonElement`; no ADF SDK types cross the tool boundary. The following is the closed initial projection contract, with golden fixtures for each row. These are application-owned plain-text rendering choices, not a claim to reproduce Jira's visual renderer. Traverse children once in document order; normalize line endings to LF, preserve authored text and code whitespace, and introduce only the specified separators. Adjacent block boundaries contribute two LF characters; do not trim authored whitespace or concatenate neighboring blocks accidentally.

| Nodes/marks | Plain-text mapping and coverage |
|---|---|
| `doc`, `paragraph`, `text`, `heading`, `hardBreak` | Version-1 root; text verbatim; headings as text on their own block; hard break as LF. |
| `bulletList`, `orderedList`, `listItem` | `- ` or sequential `n. ` prefixes, honoring valid starting order; two-space indentation per nesting level; continuation lines indented to their item. |
| `codeBlock`, `blockquote`, `rule` | Code preserves spaces/newlines without language execution; quote lines prefixed `> `; rule on a separate `---` line. |
| `table`, `tableRow`, `tableCell`, `tableHeader` | Rows separated by LF, cells by TAB, intra-cell line breaks shown as ` / `; retain cell order and header text. Merged cells add `[merged cell]` and a structural-loss limitation. |
| `panel` | Separate block with `[panelType]` from the validated known panel type, followed by child text. Unknown panel semantics are incomplete, not silently normal panels. |
| `mention`, `emoji`, `status`, `date` | Visible mention label (never account-ID lookup), emoji text or short name, status label, date as invariant UTC `YYYY-MM-DD` from epoch milliseconds. Missing display data produces a placeholder/limitation; invalid known attribute types fail validation. |
| `inlineCard`, `blockCard`, `embedCard` | Preserve a safe `attrs.url` as link text plus `[card preview not retrieved]`; otherwise `[card content unavailable]`. Always incomplete. Do not resolve `attrs.data`, fetch previews, or claim their titles/content were retrieved. |
| `media`, `mediaInline`, `mediaSingle`, `mediaGroup` | Preserve bounded visible alt text and descendant text once; emit `[media not retrieved]` and a limitation. Never interpret media IDs as visible content or download media. |
| `link` mark | Preserve text and append ` (URL)` unless identical; only HTTP(S), no user-info or controls, at most 2048 characters. Omitted unsafe/overlong destinations add a limitation; do not change API routing or fetch links. |
| Known presentation marks | Flatten bold/italic/underline/code/color/alignment styling; represent strike as `[struck: text]` and sub/superscript as `_{text}`/`^{text}` to retain meaningful distinctions. |
| Other nodes/marks, including extensions or checklist/task nodes outside the Jira subset | `[unsupported content]` plus safely available descendant text once, or retained marked text, with a deduplicated limitation. No raw attributes/JSON or guessed semantics. |

Use Jira's node catalog rather than assuming every node in the broader Atlassian/Confluence schema is supported by Jira. Label-only nodes need explicit handling because walking `content` alone loses their visible text. [Inline cards](https://developer.atlassian.com/cloud/jira/platform/apis/document/nodes/inlineCard/), [status](https://developer.atlassian.com/cloud/jira/platform/apis/document/nodes/status/), [date](https://developer.atlassian.com/cloud/jira/platform/apis/document/nodes/date/).

An explicit JSON `null` description is a successful absent body: `body = ""`, `bodyState = "absent"`, `bodyComplete = true`. A valid empty ADF document is present but empty. Missing `fields.description`, malformed nodes, invalid document type/version, or an unexpected primitive are protocol failures, not empty descriptions.

For media, cards, extension nodes, and other unsupported content, set `bodyComplete = false` with bounded, deduplicated limitations. `bodyComplete` means textual description coverage, not that the entire ticket or all attachments were retrieved. Unsupported-content loss alone is not size truncation. Traverse at most 50,000 nodes/marks and 32 nested ADF nodes (within the separate JSON depth ceiling); stop projection when the work/body bound is reached and retain the readable prefix, without scanning the rest solely to count omissions. Validate the consumed known-node shapes; do not claim schema validation of an unvisited suffix. Missing root/version/description or malformed consumed known nodes still fail.

On projection/output truncation return success with the readable prefix, `bodyComplete = false`, a fixed limitation code such as `body-byte-limit`, `projection-work-limit`, or `tool-output-limit`, and `ToolExecution.IsTruncated = true`. Do not split a Unicode scalar or return malformed JSON. Keep at most 16 fixed-code limitations in ordinal order; if necessary reserve the last entry for `additional-limitations-omitted`. Keep essential identity and coverage fields, bound summary to 512 Unicode scalars with `summary-truncated` and `IsTruncated = true` if needed, and validate provider issue ID as at most 128 ASCII digits and the returned key against the input key grammar. A metadata-only truncation does not by itself make `bodyComplete` false. No continuation/cache protocol is added; a repeated identical read cannot obtain the omitted suffix. The agent must disclose this and direct the user to Jira or a trusted limit adjustment, not invent the remainder.

Budget integration must use the actual configured tool, not the unwrapped default. `ToolInvocationPipeline` currently rejects serialized oversize before invoking `IPostSanitizationToolOutputBoundary`, and `ToolExecutionContext` does not expose the effective byte cap. Add a host-populated optional `MaximumOutputBytes` property to that execution context (pipeline always supplies the wrapped definition's positive cap; direct callers without it use the definition default). Jira pre-bounds the full serialized DTO using the same serializer settings, including identity, limitations, and JSON escaping, before returning. Preserve all existing early pipeline guards. Extend the existing internal post-sanitization hook with the effective byte-cap argument and migrate its code-explore implementation without changing its established behavior; Jira then re-bounds only sanitized structured content if redaction expands it. Do not add an alternate model body or a second sanitizer. Use bounded prefix fitting, not repeated one-character serialization loops. Never restore pre-sanitized text. If even the mandatory identity/coverage envelope cannot fit, return the standard explicit output-limit failure.

Any truncation in either phase must update the serialized completeness/limitations and the existing truncation flag. Completion wording must remain accurate after the post-sanitization phase: Jira should use a generic `description retrieved` detail rather than precomputing a false claim of full coverage. The current shared formatter only projects `IsTruncated` for search results; extend its existing detail path to append a generic `truncated` indicator for other successful tools when the final flag is true, without inspecting Jira bodies or duplicating search's indicator. Test this through the wrapped pipeline and shared presentation, not only by invoking the reader directly.

Reuse pipeline sanitization for control sequences and secrets. Do not duplicate raw ADF alongside the readable body in every model result. Add a raw-format option only in a later separately scoped operation if an actual consumer needs it.

The readable `body` must be available to ordinary model response generation, not hidden behind provenance, logs, or an inaccessible artifact. For a request such as "Show me the ticket description for https://example.atlassian.net/browse/APP-123", the agent calls `jira` with `kind: "read"` and the URL, then presents the returned description with ticket identity and any coverage limitations. Preserve the returned text rather than substituting an unrequested summary. Report absent or incomplete content honestly. Use the existing conversational response and Markdown presentation paths; no separate `show` kind, UI visibility argument, ticket renderer, or extra API request is needed.

### 6.5 Future operations without premature authority

Choose `jira` rather than a fetch-only tool name, keep `kind` required, and separate validated operation dispatch from the Jira HTTP adapter. There is only one implemented operation and no operation plugin registry. Retain the simple `JiraReadOutput` v1 contract now; adding operations requires the explicit schema migration in section 13, not just extending a switch. No unused edit payload or raw field bag is exposed now.

The current `ToolDefinition.SideEffect` and approval metadata are tool-wide, and default scheduling claims derive from that static classification in `Tool<TInput,TOutput>`. Adding `kind: "edit"` or `"transition"` to a tool still classified `ReadOnly` would bypass intended mutation boundaries. A later write plan must first extend ordinary per-invocation admission/effects/approval handling, or conservatively classify the whole tool as mutating, and cover wrappers, scheduling, child/skill eligibility, outbound consent, and audit through the same pipeline. An internal switch in `ExecuteAsync` is insufficient.

That later plan must also define issue-specific edit previews, explicit remote-write approval, cancellation/unknown-commit outcomes, concurrency protection supported by Jira, retry/idempotency rules, and result verification. Jira workflow transitions and issue edits are distinct API operations; reserve no unsupported assumptions about setting a status field directly. No mutation framework changes are needed for this read-only step.

## 7 Public Contracts

Proposed input properties are `kind` (required string, only `read`), `issue` (required bounded key/browse URL), and `provider` (optional configured account ID). Do not expose credentials, arbitrary endpoints, field selectors, expansions, request headers, or future mutation arguments.

Proposed host-owned read result:

| Property | Meaning |
|---|---|
| `kind`, `provider` | `read` and the selected stable account ID. |
| `requestedIssue`, `id`, `key`, `url` | Canonical requested key, Jira identity/current key, and trusted-site browse URL. |
| `summary`, `updatedAt`, `retrievedAt` | Small attribution fields and host retrieval time. `updatedAt` is provider metadata, not a transactional version token. |
| `body`, `bodyFormat` | Readable description; `bodyFormat` is `plainText`. |
| `bodyState`, `bodyComplete`, `limitations` | Absent/present description, textual coverage, and bounded fixed-code omissions. Partial bodies are usable success results, never represented as complete; see section 6.4. |

Attach `ToolProvenanceSource` with kind `jira-issue-untrusted`, the canonical URL, issue ID, and update/retrieval identity. Retain normal tool invocation provenance as well. DTOs contain no `HttpResponseMessage`, SDK types, live JSON documents, credentials, or terminal types. Match the repository's existing serialization conventions when implementing these logical property names.

## 8 Project/File Changes

| Area | Intended change |
|---|---|
| `src/Threadsmith.Tools/Jira/` | Add `JiraTool.cs`, `JiraContracts.cs`, `JiraOptions.cs`, `JiraCloudClient.cs`, and `JiraDescriptionReader.cs`; avoid empty interface/registry layers. |
| `src/Threadsmith.Tools/ToolContracts.cs`, `ToolInvocationPipeline.cs`, `CodeExploreOutputFormattingTool.cs` | Expose the effective execution byte cap and extend the existing post-sanitization hook as in section 6.4. Migrate the existing consumer with unchanged code-explore semantics; retain ordinary pipeline guards. |
| `src/Threadsmith.App/JiraComposition.cs` | Compose validated profiles, injected client and Secrets. Return no tool when no enabled trusted account exists. Validate configured entries consistently with PR composition. |
| `src/Threadsmith.App/HostFoundation.cs` | Register in the normal tool set, own/dispose the client, cover partial startup cleanup, and emit safe availability diagnostics. |
| `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs` | Add Jira-specific wording to existing outbound consent selection. |
| `src/Threadsmith.Interaction/Presentation/InteractionPresentationFormatter.cs` | Project the existing final `IsTruncated` flag through shared completion detail, without a Jira result parser or duplicate search indicator. |
| `src/Threadsmith.Tools/Prompts/Tool-jira-Description.md` and `src/Threadsmith.Core/PromptContracts.cs` | Register/cache/deploy description with a single `Providers` token listing enabled IDs and site hosts, never emails or secret references. |
| `tests/Threadsmith.ModelTooling.Tests/` | Add Jira adapter/contract/projection/pipeline cases using fake HTTP and Secrets; reuse existing test infrastructure. |
| Existing app, interaction, and architecture test projects | Cover actual composition, consent, dependency direction, and prompt/package checks without creating another test project. |
| `tests/Threadsmith.CoreRuntime.Tests/` | Extend existing tool-activity/transcript/frontend presentation tests to verify visible Jira kind/key/account/outcome and absence of ticket-body dumps. |
| Configuration and documentation | Extend `.threadsmith/config.example`; add `docs/operations/jira.md`; update prompt reference and existing tool/operations navigation. |

No new production package, project, external SDK, or persistence schema is planned. Any helper extraction must migrate both actual consumers and include PR regression checks; leaving competing implementations behind does not count as reuse.

## 9 Ordered Tasks

1. Adopt the scoped proposal and assign its new capability milestone per governance; resolve deployment assumptions in section 17. Reconfirm the active checkout and read C# guardrails before code edits.
2. Implement bounded typed options, deterministic provider prompts/errors, and strict input/account/alias selection, with trust-layer and endpoint-binding tests before transport is connected.
3. Implement the Jira Cloud read client using the established HTTP/Secrets patterns, network helper, bounded JSON reads, and safe error mapping.
4. Implement the closed ADF mapping and golden fixtures, body-state semantics, identity normalization, and explicitly partial prefix delivery.
5. Add the ordinary typed tool, network/secret declarations, provenance/activity metadata, required prompt catalog asset, and effective pre/post-sanitization output bounds. Extend the existing shared boundary narrowly and regress code-explore and configured wrappers.
6. Wire host composition and lifetime ownership; add Jira consent wording and populate the standard started/completed activity details. Validate visible operation/key/account/outcome in output-window and delegated-agent views, plus normal headless lifecycle behavior.
7. Exercise conversation, native skill, and permitted child calls through existing invocation paths; perform adversarial review against code outside the diff.
8. Add setup, token-mode, limitation, prompt, acceptance, and manual-test documentation. Run relevant tests/build/publish checks and the live verification matrix below. Record sanitized evidence or pending gates here; do not mark implementation complete with required live rows unverified.

## 10 Testing

Use deterministic HTTP handlers and canary secret resolvers; automated tests require no real Atlassian credentials.

- Contract/selection: required `kind`, numeric/unknown values, extra arguments, key normalization, supported/malformed URLs, explicit mismatches, disabled/no accounts, and ambiguity. Assert zero secret resolutions and HTTP calls for rejected inputs.
- URL-first workflow: pass a full browse URL in `issue`; cover lowercase keys, optional trailing slash, query/fragment decorations, exact trusted aliases, cross-profile alias ambiguity, lookalike hosts, encoded separators, and unsupported routes. Verify correct local site/key extraction, account selection, canonicalization, no browse-page/alias request, and equivalent API targets for key/URL input under both token modes. Include sanitized actual Copy Link fixtures and explicit rejection fixtures for unsupported routes.
- Trust/configuration: repository attempts to add/re-enable/rebind a profile, substitute cloud ID/host/alias/token reference, change endpoint mode, or raise limits; stable ID validation; unknown fields; invalid/missing email/reference/cloud ID; startup with no enabled account. Test each profile/alias/string/aggregate limit at and above its boundary, counting disabled profiles, deterministic prompt ordering, and bounded ambiguity messages under default and reduced presentation caps.
- Request accuracy: both endpoint modes, escaped key, exact selected fields and read-only query, actual policy host, one success request, per-request Basic header, no cookies/default authorization, no comment/history/attachment/media requests, and moved-key identity handling.
- Transport/error paths: denied host/reference/tool/consent before privileged work, redirect rejection, private/mixed DNS answers, invalid media type/JSON, decompression and streamed byte limits, 401/403/404/429/5xx, safe retry delay, and cancellation during resolution/send/body read/projection.
- Body fidelity: golden input/output fixtures for every mapping row in section 6.4, including status/date, label-only nodes, cards with URL versus data, merged tables, strike/subscript/superscript, invalid known-node attributes, unknown nodes/marks, media alt text, and checklist fallback. Cover nested mixed structures, null versus empty versus missing description, authored/code whitespace, unsafe links, Unicode/control characters, exact body/work/depth boundaries, deterministic limitation ordering, and no duplicate descendant text.
- Output/lifecycle: distinguish hard transport/JSON failure from partial description success. Test JSON-escaping expansion, reduced wrapped runtime caps, sanitizer expansion that newly causes truncation, mandatory-envelope failure, Unicode-safe prefix fitting, metadata-only truncation, and consistent final flags/coverage/progress. Verify provenance, start/completion/failure/cancellation activity, client disposal after startup failure and shutdown, and no secret value or encoded authorization in exceptions/events/prompts/logs. Preserve existing code-explore and non-Jira pipeline behavior when extending the shared hook/context.
- Secret lifecycle: have the existing fake resolver return token A then B for separate allowed reads using the same reference; assert request-local headers and no reuse of A. Cover missing/expired/revoked-token responses and insufficient-scope denial without token inspection, misleading diagnosis, fallback, or raw provider error leakage. Separate process-environment refresh expectations from file/provider refresh semantics.
- Visible progress: assert actual started activity and completed output-window blocks show `read`, the ticket key, account, and the appropriate outcome, with duration following existing settings. Cover moved keys, limited/absent descriptions, denial, HTTP failure, timeout, cancellation, main/delegated views, and supported interactive frontend consumers. Assert a canary ticket body and summary are absent from progress/completion blocks while the sanitized body remains in the model-facing structured result. DTO serialization alone is insufficient evidence of visible activity.
- Requested description display: exercise a conversation asking "Show me the ticket description" with a ticket URL. Verify the model continuation receives the readable body and coverage fields and can display them through the ordinary assistant-response path, while tool progress remains compact. Use the existing scripted model integration harness to prove data flow and response rendering without asserting nondeterministic live-model wording; manually verify the natural-language workflow. No second fetch or new tool kind is required solely for display.
- Real entry-point integration: ordinary conversation calls, a native skill call, and a read-only delegated child each use the same governed tool; restrictive child host/secret/tool policy remains effective. Duplicate calls retain the common guard. No per-loop Jira dispatch code is added.
- Regression: run relevant PR-tool and code-explore tests for shared changes, secret tests for any boundary changes, interaction availability/consent/completion tests (including existing search truncation), architecture dependency and prompt asset tests, solution build, and existing publish payload validation. Do not claim live Jira coverage from fake-handler tests.

Review specifically for bypasses around wrappers or policy, competing HTTP/secret lifecycle owners, incorrect static mutation classification, whole-issue overfetch, lost ADF text, unsafe returned URLs, and secret leakage. Source-level estimates are not performance measurements.

### Required live verification

These are implementation exit gates, not tests already performed for this plan. Use an operator-authorized read-only test issue and credentials supplied through normal Secrets; record dates, endpoint mode, sanitized outcomes and fixture coverage, never token values or private descriptions. Use separate user operations so the duplicate guard is not bypassed. If credentials or a required environment are unavailable, retain an explicit pending verification item and do not claim the affected support or overall implementation is verified.

| Gate | Required evidence | Current evidence |
|---|---|---|
| Scoped token | A successful gateway read using a token with classic `read:jira-work`, trusted cloud ID, normal permissions/consent, and key plus real copied browse-link input. | Deterministic gateway route/auth/response coverage complete; no live scoped profile, cloud ID or scoped token is configured, so the live gate remains pending. |
| Unscoped token | A successful direct-site read using an unscoped token with the same result/progress assertions; no gateway fallback. | On 2026-09-18 an operator-authorized issue was read through the compiled ordinary pipeline by key and decorated standard tenant browse URL. All observed requests stayed on the configured `.atlassian.net` tenant, returned the same identity and bounded body, and emitted content-free progress. |
| Secret rotation | For each mode, operator replaces a test token behind the same reference and the next allowed read uses it according to resolver refresh semantics. A separately revoked/expired test credential gives safe authentication failure; expiry also has deterministic handler coverage if waiting for actual expiry is impractical. | Deterministic per-request rotation coverage is complete. On 2026-09-18 an isolated invalid credential produced a sanitized live execution failure with no result body or credential disclosure; replacement behind the real reference and scoped-mode rotation remain pending. |
| URL compatibility | Capture sanitized Jira Copy Link examples for standard tenant and configured custom-domain alias. Exercise alias parsing against the configured API binding; if a real alias environment is unavailable, record that compatibility as unverified, not inferred from synthetic URLs. Unsupported route examples receive the documented local remedy. | On 2026-09-18 a real standard tenant browse link and a decorated variant resolved locally to the authorized issue, while an unsupported search route failed as invalid arguments with zero network requests. No custom browse alias is configured, so live alias compatibility remains unverified. |
| User-visible workflow | Actual output-window/child activity shows kind/key/account/outcome without content dumps; asking to show the description displays returned text and limitations in the normal assistant response. Include rich and partial body fixtures. | On 2026-09-18 live ordinary-pipeline activity contained kind/key/account/outcome and excluded the issue summary/body. The rich live description reported card/media omissions honestly; a reduced runtime cap retained a 48-byte body prefix with `bodyComplete:false`, `tool-output-limit` and truncation. The operator also reported successful ordinary interactive retrieval of several issues. Physical delegated output, requested-description wording, timeout and cancellation remain pending. |

## 11 Security/Permissions

Enabling an account and supplying an API token do not enable the tool or grant network/secret access. Use the existing repository-bound outbound consent and headless policy behavior, with accurate disclosure that ticket identifiers are sent to Jira and returned private descriptions may enter model context and ordinary session evidence. Do not adopt `web_fetch`'s current-message URL consent as credentialed Jira authority.

Only trusted configuration binds a credential to a site/cloud ID. No repository data, ticket text, model input, redirect, or response URL may change that binding. Do not weaken normal host allowlists to accommodate gateway mode. Gateway host approval does not authorize arbitrary tenants or arbitrary API routes.

Recommend the section 6.2 scoped-token recipe (`read:jira-work`) and an account with only necessary issue access; unscoped tokens remain supported without implying that they have scope-based least privilege. No write scopes are required by this plan. Keep returned body evidence in the existing retention/redaction model; never put body text, summary, email, or authorization headers in transient activity or diagnostic messages.

## 12 Observability

Use ordinary `ToolInvocationStarted`/completion/failure events, timing, logger, and transient activity. Populate `DescribeActivity` with `kind`, requested key, and selected provider; populate `ToolExecution<T>.TransientActivityDetail` with `kind`, returned key, provider, and a compact coverage outcome. Put operation and key first so they remain useful under normal display truncation. The existing pipeline owns execution duration, cancellation, and result emission.

Required visible content, illustrated below rather than prescribing a new layout:

```text
Jira | read | APP-123 | work-jira | running
Jira | read | APP-123 | work-jira | completed | description retrieved
Jira | read | APP-123 | work-jira | failed | access denied
Jira | read | APP-123 | work-jira | cancelled
```

The existing output window supplies running/completed/failed/timed-out/cancelled state and optional elapsed duration. Preserve kind/key/account on failure via the started-event detail when no completion detail exists. For a moved issue, identify the returned key and, when it differs, the requested key. Use compact completion text such as `no description` or `description retrieved with limitations` for known coverage limitations, with the final truncation flag projected through the shared detail extension in section 6.4. Never promise full coverage before post-sanitization bounds are applied; keep detailed limitations in the structured result. For invalid input or account ambiguity, report the safe validation/selection error without claiming a ticket fetch began.

Visibility must come from the actual shared activity/completion projection used by the main transcript and agent workspace, not merely a populated DTO, diagnostic log, or subsequent model answer. Reuse `InteractionPresentationFormatter.GetToolDetail` and its existing preference for completion/start activity details. No special output-body inspection flag, renderer, progress polling, extra network request, or Jira-specific event stream is needed.

Normal provenance/result evidence may contain the returned ticket body under existing session policy. Diagnostics contain only safe status, operation/account ID, bounded identity, outcome/limit reason, and duration. No custom ticket event stream, renderer, metrics subsystem, or background progress loop.

The tool-progress requirement is operation metadata only. Do not add automatic ticket summaries, descriptions, raw ADF, or raw result JSON to its Jira activity/completion blocks. This restriction does not apply to the normal assistant response: when the user asks to see a ticket description, the agent should display the readable description from its tool result there, including any limitations. Even when progress and assistant responses share the same output window, they retain these distinct roles.

## 13 Migration/Compatibility

This is additive and opt-in. Existing installations without Jira accounts have no new registered Jira capability. Existing PR profiles, web consent, Secrets stores, and tools continue to behave as before. Configuration changes require restart.

There is no persisted Jira cache or storage migration in this initial release. Each permitted non-duplicate read performs a fresh request. Identical calls in one model execution remain blocked by the common guard; a new user operation can read again. Operation sharing/refresh can be considered later if measured repeated reads justify it.

Publish the first typed input/output schema and tool contract as v1 using existing version metadata. `Tool<JiraInput, JiraReadOutput>` has a statically generated output schema: it cannot return a future mutation payload safely by changing only `kind`. Before the first new operation, replace that result contract with a host-owned discriminated `JiraOutput` containing common operation/identity fields and typed operation-specific payloads, and bump the tool major version and affected schema versions to v2. Do not pre-expose unused mutation fields now. Preserve v1 read argument compatibility, explicitly document the output-shape change, and test schema generation, model adapters, configured wrappers, persisted v1 result display/replay, and new read/write payload validation. Existing recorded v1 result JSON stays immutable; it must not be decoded as a v2 envelope or trigger a refetch. This is a scheduled compatibility obligation of the later write plan, not a promise of schema-free extensibility.

Future writes must revise consent and admission semantics rather than inherit a grant that promised read-only access. New `kind` values require explicit schema, validation, policy, documentation, and test changes.

## 14 Acceptance Criteria

1. A user configures a trusted Jira Cloud account with a standard secret reference, enables `jira` through normal consent/policy, and an agent can call `kind: "read"` with a matching key or browse URL.
2. Both declared API-token endpoint modes perform the correct bounded read and return identity plus the ticket description as readable text, without unrelated ticket fields or additional resource requests.
3. Null, empty, unsupported, malformed, and over-limit descriptions have the distinct documented outcomes. An in-bound valid response yields a usable readable prefix when body/output/work limits apply, with `bodyComplete = false`, limitations, and `IsTruncated = true`; transport/JSON failures and an envelope that cannot fit fail explicitly. No partial body is silently represented as complete.
4. Ambiguous accounts require explicit selection. Invalid kinds/URLs/account bindings and denied policy cannot reach Secrets or HTTP. Credentials never enter tool results, model-facing descriptions, or diagnostics.
5. Ordinary conversation, skill, child, interactive, and headless paths retain the same lifecycle, scope, cancellation, enablement, and output controls.
6. Existing PR behavior remains intact; useful shared facilities are reused and no Jira-specific execution lifecycle or cache is introduced.
7. Future edit/transition kinds fail validation today. The later feature must complete both shared admission/approval work and the explicit versioned output-contract migration before exposing them.
8. Prompt catalog, deployment, configuration examples, operations guidance, meaningful tests, and targeted adversarial review are complete before marking implementation complete.
9. A user can identify a ticket solely by pasting a supported browse URL, including query/fragment decorations and exact trusted custom-domain aliases. The tool extracts the site/key itself, selects or requests the configured account, and constructs the API call without fetching the browse page, contacting aliases, or forwarding decorations. Real Copy Link fixtures substantiate the documented route scope; unsupported routes fail actionably.
10. Actual output-window progress and completion show the kind operation, ticket key/number, selected account, and outcome through the normal shared lifecycle, including failure and cancellation. They do not dump ticket content; the agent receives the readable body in the ordinary result. Main and delegated-agent views are covered by integration/presentation verification.
11. When the user asks to see the ticket description, the agent can present the returned body in its normal response, with identity and coverage limitations. Compact tool-progress output does not hide content from the model or prevent requested display. This uses the same `kind: "read"` call and existing response presentation, analogous to asking the agent to list changed files after `pr_fetch`.
12. Profiles, aliases, prompt generation, ambiguity messages, projection work, and body delivery satisfy the concrete bounds and deterministic ordering in sections 6.1-6.4, including lower configured runtime limits and sanitizer expansion.
13. The initial classic scope recipe is documented and both scoped/unscoped live read gates, secret rotation checks, and required compatibility/visibility gates in section 10 have recorded evidence before implementation completion. Missing environments remain explicit pending gates; fake-handler coverage is not substituted for live proof.

Jira behavior is owned by [Scenario AY](acceptance-scenarios.md#scenario-ay---governed-jira-issue-reads) and [MTP-272](manual-test-plan.md#mtp-272---jira-scopedunscoped-reads-and-visible-output). Existing secret invariants remain owned by [Scenario AB](acceptance-scenarios.md#scenario-ab--extensible-secret-discovery) and MTP-227 through MTP-230. Deterministic implementation evidence does not claim the pending live MTP-272 rows.

## 15 Risks

- Cloud versus Data Center is a material API/authentication distinction. This proposal explicitly chooses Cloud; support for another deployment requires revised scope, not a hostname substitution.
- Scoped-token gateway routing can fail despite a valid token when cloud ID, endpoint mode, scopes, or site binding are wrong. Give actionable sanitized setup guidance without probing other tenants.
- Rich descriptions may include non-text requirements. Preserve supported text and report media/extension omissions so the agent can identify missing evidence.
- ADF projection is new code. Keep the supported textual subset explicit, exercise realistic structured fixtures, and avoid broad renderer/framework work.
- Large valid descriptions return explicitly bounded partial text; transport/JSON overflow still fails because an incomplete JSON issue cannot be trusted. There is no continuation, so requirements beyond the prefix require direct Jira inspection or a trusted limit change. Reduced runtime caps and sanitizer expansion must not accidentally turn a deliverable prefix into a total failure.
- Old/moved issue keys can resolve to different current keys. Preserve the returned identity and requested key without treating update timestamps as concurrency tokens.
- A future write operation cannot safely rely on today's static read-only tool metadata or old consent. This is an architectural dependency of that later feature.
- Custom browse aliases are operator assertions about tenant identity, not discovered or network-verified mappings. Incorrect trusted bindings may address the wrong tenant; document setup and verify actual copied links without credentialed alias requests.
- Live tenant, permission, token-scope, secret rotation, and ADF variations remain unverified until the section 10 gates are exercised; missing credentials do not justify declaring them covered.

## 16 Documentation

During implementation, document account setup in `docs/operations/jira.md`: trusted location/restart, both endpoint modes, obtaining the correct cloud ID, email/token reference, standard Secrets storage/rotation, the classic `read:jira-work` recipe and deferred granular-only validation, tool enablement/consent, network hosts, bounded browse aliases, supported and rejected URL examples, progress output, partial-body limits/no continuation, and safe failure remediation. Put illustrative configuration in `.threadsmith/config.example` with an explicit user/machine-only account-binding note. Include manual procedures for every section 10 gate, including a real decorated Copy Link example, kind/key/account/outcome activity, failure/cancellation, and requested assistant-body display without automatic tool-progress content dumps.

Add `Tool-jira-Description.md` to the flat prompt catalog and update both `docs/operations/prompts.md` and `docs/prompt-file-reference.md` in the same change. Its `Providers` token contains enabled safe account IDs and site hosts only. The prose describes `kind: "read"`, full browse-URL input, account ambiguity, description coverage, untrusted evidence, and unavailable write operations. It also explains that the returned body supports displaying the description when the user requests it; compact progress is not a content-access restriction. Code owns permissions and schema. Include "Show me the ticket description" in the documented/manual conversational examples.

This implementation is owned by M31, Scenario AY and MTP-272. Historical completed contracts stay frozen.

Official sources consulted on 2026-09-18:

- [Jira Cloud Get issue](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/#api-rest-api-3-issue-issueidorkey-get).
- [Jira Cloud REST v3 introduction](https://developer.atlassian.com/cloud/jira/platform/rest/v3/intro).
- [Basic authentication for REST APIs](https://developer.atlassian.com/cloud/jira/platform/basic-auth-for-rest-apis/).
- [Manage Atlassian API tokens](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/).
- [Atlassian Document Format structure](https://developer.atlassian.com/cloud/jira/platform/apis/document/structure/).
- [Jira Cloud rate limiting](https://developer.atlassian.com/cloud/jira/platform/rate-limiting/).

Research used official documentation and indexed endpoint extracts. The full issues reference exceeded the browser fetch limit, and a direct OpenAPI download was blocked by the local network connection. The indexed Get issue section confirms the recommended classic `read:jira-work` scope; this plan does not claim an exhaustive granular-scope recipe. Recheck for API changes during implementation and perform the live gates; no live Jira request was made for this draft.

Self-adversarial review disposition: section 6.2 fixes the scope recipe and resource bounds; sections 6.3-6.4 define partial delivery and its real pipeline integration; sections 6.1-6.2 separate URL aliases from API authority; section 13 makes future output-schema migration explicit; section 6.4 closes ADF mapping gaps; section 10 gates live endpoint, rotation, Copy Link, and user-visible verification. These are plan corrections, not implementation/test-completion claims.

Implementation adversarial review disposition: body limits now stop ADF traversal and bound repeated-mark expansion; query/fragment decorations are ignored after route parsing; metadata-only fitting preserves body completeness; known ADF containers and empty labels fail or fall back honestly; merged-cell annotations stay within their cell; and an explicit tool-definition capability admits Jira, but not ordinary external-search tools, to narrowed read-only children. Deterministic regressions exercise sanitizer expansion, ordinary conversation display, native-skill execution, child policy plus execution, and body-free activity metadata. Live tenant and physical frontend checks remain governed by section 10.

## 17 Open Decisions

Confirmed user requirements: the initial implementation supports both scoped and unscoped API tokens, accepts ticket URLs and parses their identity, and visibly reports operation kind/ticket number in the output window. Ticket content belongs in the agent's tool result and may be displayed in the normal assistant response when requested, not automatically dumped into tool-progress blocks. These are not open decisions.

The following are proposed defaults, not unanswered prerequisites to writing this plan:

- Initial deployment: Jira Cloud API on standard `.atlassian.net` tenants or the scoped gateway, accepting standard browse links and trusted custom-domain browse aliases. Data Center, custom API endpoints, and arbitrary Jira link routes are deferred.
- Body: standard description only, as bounded plain text with explicit partial/unsupported-content limitations; comments, custom acceptance fields, raw ADF, and continuation are deferred.
- Scoped-token recipe: classic `read:jira-work`; granular-only scope minimization is deferred, not a blocker to supporting scoped tokens now.
- Account surface: named `providers` dictionary and optional `provider` input for consistency with `pr_fetch`; no separate account-discovery tool or default-account heuristic.
- Delivery: allocate a new capability milestone when this exploratory proposal is accepted. Future writes belong to a subsequent plan and do not block the read-only implementation.

Revisit these only when the target Jira deployment or a concrete consumer requirement differs. Implementation must record deviations and their impact on trust, body completeness, and compatibility here.
