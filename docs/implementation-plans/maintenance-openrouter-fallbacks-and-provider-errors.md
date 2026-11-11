# OpenRouter fallbacks and provider failure details

**Status:** Planned
**Delivery track:** Maintenance
**Prerequisites:** Use the existing provider adapters, Responses replay, retry policy, prompt catalog, and diagnostic display.

## 1 Objective

Permit an ordered list of OpenRouter upstream providers for one model.
Let OpenRouter perform fallback within that list.
Show useful failure details from every model provider.
Keep the implementation small.

Use [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf) as the language reference.
Treat code identifiers and repository terms as technical names.

## 2 Architectural Context

Extend the existing OpenRouter configuration and request projection.
Use the existing provider exceptions and diagnostic messages for failures.
Keep the normal model, tool, cancellation, usage, and retry paths.

Follow these contracts:

- [Planning governance](planning-governance.md).
- [Shared implementation context](00-shared-context.md#g-implementation-document-template-and-agent-instructions).
- [Host-owned model abstraction](../architecture/adr-10-host-owned-model-abstraction.md).
- [Provider isolation](../architecture/adr-30-openai-compatible-provider-isolation-and-legacy-migration.md).
- [Request and continuation ownership](../architecture/adr-41-canonical-cache-optimized-model-requests.md).
- [Private response replay](../architecture/adr-60-native-anthropic-provider-and-transient-replay.md).
- [C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 3 Scope

- Add an ordered OpenRouter upstream list.
- Preserve existing single-upstream configuration.
- Accept a response from any configured upstream.
- Preserve failure details from OpenRouter, OpenAI-compatible, Anthropic, and Codex adapters.
- Use the existing diagnostic display in interactive and headless operation.
- Add focused tests and update the maintained documentation.

## 4 Non-Scope

Do not add a host routing engine, provider discovery, or automatic model selection.
Do not add a new retry framework, event stream, database table, or display system.
Do not add a provider compatibility registry or a separate execution path.
Do not require a forced outage or live failover simulation.
Do not expand this work into a general provider refactor.
Do not stage, commit, push, or open a pull request without a user instruction.

## 5 Current State

`OpenRouterModelConfiguration` has one `Upstream` and one `ExpectedProviderName`.
The projection sends one provider in `order` and `only`.
It disables fallback when an upstream is configured.
Replay validation requires this single configured upstream.

The adapters can replace useful provider errors with generic messages.
This occurs for HTTP rejections, stream errors, and SDK exceptions.
The existing run failure path already sanitizes and displays exception messages.

Live investigation found an Azure capacity rejection through OpenRouter.
The same adapter passed separate tool-continuation checks through OpenAI.
Those checks did not exercise a provider switch within a continuation.

## 6 Proposed Design

### 6.1 Ordered configuration

Add an optional `upstreams` array to each OpenRouter model entry.
Each entry contains a routing slug and its expected response name.
Keep the existing model ID and profile ID.

```json
"upstreams": [
  { "provider": "azure", "expectedProviderName": "Azure" },
  { "provider": "openai", "expectedProviderName": "OpenAI" }
]
```

Preserve array order.
Reject an empty array, blank entries, and duplicate routing slugs.
Compare routing slugs without case.
Permit different endpoint slugs with the same expected provider name.
For example, regional Azure endpoints can return the same display name.

Retain `upstream` and `expectedProviderName` for existing catalogs.
Normalize that legacy pair to one entry inside the adapter.
Reject configuration that supplies both forms.
Do not introduce a custom configuration language.

### 6.2 Request projection

For the example above, send this provider object:

```json
"provider": {
  "order": ["azure", "openai"],
  "only": ["azure", "openai"],
  "allow_fallbacks": true,
  "require_parameters": true
}
```

Use the same ordered list for `order` and `only`.
Keep requests inside the configured provider list.
Enable fallback for a list with multiple entries.
Preserve existing single-upstream behavior for a list with one entry.
Preserve the current behavior when no upstream restriction is configured.

Let OpenRouter choose the next eligible upstream.
Do not make one host HTTP call for each list entry.
Keep the configured model and required request parameters unchanged.

The [OpenRouter routing contract](https://openrouter.ai/docs/guides/routing/provider-selection) defines these fields.
Fallback cannot repair every stream failure after output reaches the client.
See [OpenRouter streaming errors](https://github.com/OpenRouterTeam/docs/blob/main/api_reference/errors-and-debugging.mdx#mid-stream-errors).

### 6.3 Response identity and replay

Replace the single-name check with a check against the configured response names.
Keep the exact model identity check.
Reject a reported provider identity outside the configured list.
Preserve the current treatment of an absent provider field.

Bind replay authority to the complete configured list.
Preserve the existing profile, credential, endpoint, history, and tool-inventory checks.
Do not discard private replay when the reported provider changes within that list.
Do not add a route lock that disables fallback on tool continuations.

Replay the original output items without modification.
Preserve reasoning content, signatures, encrypted content, order, and tool-call IDs.
Do not repeat a completed tool to create new provider state.
Do not remove reasoning to make a fallback request pass.

The user accepts reliance on OpenRouter for this routing behavior.
Cross-upstream replay compatibility remains an explicit assumption.
Do not require a live failover demonstration before delivery.
Do not describe this assumption as a verified live result.

### 6.4 Failure details from every provider

Use `ModelProviderException` and `TransientModelException`.
Retain the existing run and child failure paths.
Do not add an event schema or a durable failure store.

Extract these details when the provider supplies them:

- The configured provider and the reported upstream.
- The HTTP status and provider error code or type.
- The provider's explanatory message.
- The request or generation ID.

Put the safe details in the exception message used by the existing diagnostic path.
Preserve the explanatory text instead of returning only an HTTP status.
Keep the original provider wording after required redaction.
Label the details as a provider failure.

Apply this rule to all four integrations:

| Integration | Error sources |
|---|---|
| OpenRouter | HTTP bodies and Responses failure events. |
| OpenAI-compatible | HTTP bodies and protocol error objects inside streams. |
| Anthropic | Structured HTTP and SSE error fields, including wrapped SDK failures. |
| Codex | HTTP rejections and Responses failure or incomplete events. |

An HTTP 200 response can still contain a provider failure.
Read its error details before the adapter creates the host exception.
Do not copy an SDK exception's complete `ToString()` output.

Use one small shared formatter if multiple adapters need the same formatting or redaction.
Keep protocol parsing inside each adapter.
Do not create a new error framework or require a new public DTO.

If a response has no usable explanatory text, retain a clear status-based fallback.
If parsing fails, report that the provider error body could not be read.
Do not invent an upstream explanation.
Treat cancellation separately from a provider rejection.

### 6.5 Safe display and retry classification

Use existing response, output, and presentation limits.
Read error bodies within the existing transport bounds.
Identify any missing error-read bound before implementation.
Use one shared bound if the current transport has none.
Disclose omitted text when a limit truncates the message.

Remove active credential values and apply the existing output sanitizer.
Do not include authorization headers, cookies, complete requests, or private reasoning payloads.
Treat provider text as untrusted display data.
Preserve terminal escaping and redaction in the existing presentation path.

Classify known availability and rate-limit errors through the existing retry policy.
For OpenRouter, read `error_type` as well as the native error code.
Recognize `provider_unavailable` and `provider_overloaded` as transient failures.
Do not classify authentication, invalid requests, or refusals as availability failures.

Keep retries bounded by the existing attempt count, deadline, and cancellation.
Preserve the existing rule against retry after committed answer or tool output.
Keep interrupted usage and charges when an attempt fails.
Do not let usage on a retryable terminal error falsely commit answer output.
After retry exhaustion, show the last useful provider error.
Include earlier upstream failures only when OpenRouter actually reports them.

New model-facing wrapper wording must use the existing Markdown prompt catalog.
Provider-supplied message text remains response data.
Do not add a separate prompt loader or move existing unrelated messages.

## 7 Public Contracts

The OpenRouter model gains one optional ordered `upstreams` array.
The legacy scalar pair remains valid.
Add the array to the existing repository-protected model properties.

Keep existing exception types and diagnostic event contracts.
Keep SDK and wire types inside their adapters.
No database migration is required.

## 8 Project/File Changes

| Area | Expected changes |
|---|---|
| OpenRouter | Configuration, validation, projection, identity checks, and error extraction. |
| Other adapters | Local HTTP, SSE, or SDK error extraction. |
| Shared model code | A small formatter and classification adjustments only where necessary. |
| Execution and presentation | Reuse current paths. Change them only if they discard the improved message. |
| Tests | Extend existing provider fixtures and relevant display coverage. |
| Documentation | Provider configuration, failure behavior, prompt references where applicable, and maintained verification cases. |

Keep the expected implementation to focused changes in existing components.
Do not turn a local correction into a subsystem rewrite.

## 9 Ordered Tasks

1. Inspect the four adapter error paths and the existing display path.
2. Add the upstream array and legacy normalization.
3. Update request projection and response identity validation.
4. Preserve unchanged replay under the configured list.
5. Preserve safe failure details in all four adapters.
6. Extend existing retry classification for known availability errors.
7. Add only the tests needed for the changed behavior.
8. Update the maintained documentation and run final validation.
9. Review the final diff for unnecessary abstractions and duplicate execution paths.
10. Record exact validation results and unverified assumptions in this plan.

## 10 Testing

Use the existing test projects and provider fixtures.
Extend existing cases before adding new fixtures.
Use a small set of focused checks:

| Check | Required evidence |
|---|---|
| Configuration | Legacy input, ordered arrays, invalid entries, and protected-source handling. |
| Projection | Exact `order`, `only`, fallback, and parameter requirements. |
| Identity and replay | Configured response names pass. Other names fail. Replay items remain unchanged. |
| Provider failures | All four integrations preserve safe explanatory text and error codes. |
| Retry | A known transient error uses existing retries. A permanent error does not. |
| Display and redaction | Parent, child, and headless paths retain the message without leaking a credential canary. |

Use parameterized cases for related error forms.
Do not create an exhaustive provider, frontend, model, and failure matrix.
Do not write tests that merely repeat implementation details.
Use runner cancellation and explicit completion signals under guardrail G-24.

Do not simulate a real provider outage or require live failover.
Ordinary live checks are optional and require authorized credentials.
Report any live provider switch as unverified unless it actually occurs.
Do not block delivery on that missing observation.

Run focused tests first.
Then run the product validation required by `CONTRIBUTING.md`:

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
git diff --check
```

Use the platform-specific ripgrep setup from `CONTRIBUTING.md`.
Run focused formatting and informational analyzer checks for changed C# files.
Record failures, skips, and unavailable platforms accurately.

## 11 Security/Permissions

Use existing trusted catalog ownership for upstream selection.
Repository configuration must not widen the permitted upstream list.
Keep the same OpenRouter endpoint, credential boundary, model, and tool authority.

Sanitize details before logs, events, persistent projections, or user display receive them.
Preserve useful failure text after redaction.
Do not replace all provider messages with generic text as a redaction shortcut.
Keep private replay outside diagnostics and durable state.

## 12 Observability

Reuse the existing busy notice, failure diagnostic, and child failure detail.
Show the provider's final rejection after retries stop.
Keep HTTP status, error code, and request ID when available.
Preserve these safe details in raw exchange failure logs.

Do not claim which provider attempts OpenRouter made unless its response reports them.
Do not add a new dashboard or renderer.

## 13 Migration/Compatibility

Existing single-provider catalogs keep their behavior.
Users opt into fallback through the new array.
Keep existing provider and model IDs.
Require the normal restart for catalog changes.

Preserve current callers of provider exceptions.
Old diagnostic events remain readable through the existing event contracts.
Change error text only to retain useful safe details or identify a missing explanation.

## 14 Acceptance Criteria

1. A model can configure Azure and OpenAI in an ordered list.
2. OpenRouter receives that order and the same restricted provider list.
3. The adapter accepts reported identities from the configured list.
4. Private replay remains intact without a host route lock or repeated tool effects.
5. Existing single-provider catalogs remain valid.
6. All four provider integrations expose useful failure details to the user.
7. Stream errors retain their explanation even when HTTP status is 200.
8. Existing retry limits and output-commit rules remain effective.
9. Credentials and private reasoning remain absent from diagnostics.
10. No live failover simulation or new routing subsystem is required.

During implementation, extend [Scenario OPENROUTER](acceptance-scenarios.md#scenario-openrouter--governed-manually-configured-provider).
Update [MTP-OPENROUTER](manual-test-plan.md#mtp-openrouter--manual-provider-compatibility).
Add a small shared failure-reporting case for the other provider integrations.
Do not change the unrelated secret-provider verification case.

## 15 Risks

An allowed upstream can differ in supported parameters or reasoning behavior.
Keep required parameters and preserve the original replay items.
Cross-upstream replay remains an accepted assumption, not a proven live result.

A provider can echo secrets in its error text.
Redact known credentials and use the existing sanitizer before publication.

Automatic retries can duplicate visible work after output commits.
Preserve the shared commit boundary.

Excessive abstraction can make this change larger than the user requested.
Prefer local parsing changes and the existing exception and display paths.

## 16 Documentation

Update provider examples and `docs/operations/model-providers.md` during implementation.
Document ordering, restricted destinations, legacy compatibility, restart behavior, and stream limitations.
Describe useful failure reporting for all four integrations.

Update both prompt references if a filename, token contract, or model-facing wrapper changes.
Use `docs/operations/prompts.md` and `docs/prompt-file-reference.md`.
Keep completed milestone documents unchanged.
Add only one navigation row to the planning README.

## 17 Open Decisions

None for the requested scope.
OpenRouter owns fallback.
The user accepts delivery without a forced failover demonstration.
Failure reporting covers every model provider integration.
Implementation must remain focused and reuse existing components.
