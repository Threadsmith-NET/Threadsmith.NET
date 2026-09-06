# ADR-10: Host-owned model abstraction

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §11, §29 (ADR 10), §36
- **Validated by:** `Threadsmith.Milestone3.Tests`

## Context

Threadsmith.NET must support OpenAI-compatible and future model providers without allowing provider SDK types, endpoint assumptions, or credentials to enter core events, persistent state, or execution contracts. The host must negotiate capabilities and select only from its configured profile catalog before a run begins.

## Decision

Keep `IModelProvider`, `ModelChunk`, `ModelUsage`, model outputs, profiles, and selection results host-owned in `Threadsmith.Models`. The first real adapter uses `HttpClient` directly against a configured OpenAI-compatible chat-completions endpoint and translates server-sent events into those contracts.

Profiles carry endpoint identity, model identity, capabilities, limits, intended workloads, sensitive-data policy, retry policy, and cost metadata. They carry only a logical secret reference; the composition root resolves the credential from the final configuration layer. Host selection filters incompatible profiles before applying user/session preferences or advisory hints. Profiles outside the configured catalog cannot be selected.

The direct HTTP adapter is intentionally small. The plan-01 `Microsoft.Extensions.AI` spike proved the streaming abstraction, but using its provider adapter here would add another public shape without reducing the required OpenAI-compatible normalization. A future provider may use `Microsoft.Extensions.AI` internally while preserving the same host contracts.

## Consequences

- Model-provider SDK objects cannot leak through the public model boundary.
- Missing token usage is conservatively estimated and marked; cost uses the greater of reported and local estimates before accruing against the execution budget.
- HTTP 429, 503, and 529 responses are bounded transient failures; other 4xx responses are permanent; malformed output is classified separately.
- Cancellation covers connection, retry delay, and streamed response reads.
- Network-target policy remains owned by the plan-08 tool/policy runtime. Until then, the adapter can contact only the endpoint explicitly present in the selected profile.

## Validation

Recorded SSE tests cover text, fragmented tool calls, usage, cost, missing-usage estimation, retry classification, malformed output, and mid-stream cancellation. Architecture tests continue to enforce that model SDK packages and types do not enter `Threadsmith.Core`, events, or persistence.

## Operational clarification — 2026-08-11

Ordinary conversation is not charged to an execution-token budget because cancellation, tool-call accounting, per-tool timeouts, output accounting, and user-controlled budgets govern it. Mutation-proposal operations receive fresh configured budget scopes rather than accumulating usage across the application lifetime; session usage remains separately observable telemetry. The OpenAI-compatible adapter also treats explicit DNS, connection, HTTP-protocol, and prematurely-ended-response transport categories as bounded transient failures under the selected profile's existing retry and timeout policy. TLS, authentication, and configuration transport categories remain non-transient.
