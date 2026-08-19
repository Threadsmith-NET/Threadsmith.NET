## Milestone 16 — OpenAI-Compatible Model Reasoning Compatibility  *(plan 46)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Reproduce the configured reasoning behavior of heterogeneous OpenAI-compatible models through typed bounded compatibility settings instead of assuming every endpoint accepts standard `reasoning_effort`.

**Deliverables:**
- Versioned closed OpenAI-compatible reasoning-control modes for standard effort, explicit level mapping, typed binary thinking toggles, bounded fixed thinking settings, always-on/uncontrollable reasoning, and unsupported reasoning.
- Complete deterministic mappings from Threadsmith `ReasoningLevel` values to validated provider request values with explicit `None` semantics.
- Bounded typed request additions that cannot override messages, tools, model identity, streaming, token limits, sampling, reasoning ownership, response schemas, transport, authentication, or other protected fields.
- Allowlisted provider reasoning-response extraction normalized into `ModelChunk.Reasoning`, followed by a separate bounded transient-only display stream that is never subscribed to persistence, domain telemetry/hooks, evidence, context, diagnostics, or replay; a transactional migration purges historical reasoning event rows.
- Provider-neutral effective controllability projected consistently through model selection, session switch/reset, `/reasoning`, footer/status, context inspection, and headless output.
- Backward-compatible catalog migration and examples for Pi-style vLLM/Ollama compatibility without runtime Pi dependency or automatic user-file mutation.
- The repository-owned versioned `plan46-pi-reasoning-v1` specification plus sanitized exact-request/stream fixtures covering its 14 profiles, with adversarial configuration/collision/bounds/redaction tests, separate runtime cancellation/retry/stream-failure tests, and transient-delivery/persistence/architecture tests.
- ADR, provider/user/operations documentation, Scenario P, maintained manual cases, milestone/index/status updates, and DOX closeout when implementation lands.

**Exit criteria:**
- Models can express their real standard, custom-mapped, binary-toggle, fixed, always-on, or unsupported reasoning behavior without arbitrary JSON transformers, scripts, callbacks, or custom CLR types.
- Every selectable host level produces exactly one validated request representation; explicit M16 modes reject unsupported levels and invalid `None` behavior before network I/O, while catalogs without `reasoningCompatibility` retain the pre-M16 unsupported-level clamp.
- Compatibility configuration cannot collide with or weaken host-owned request, trust, endpoint, sensitive-data, secret, tool, schema, mutation, or validation authority.
- Known response reasoning shapes stream through the normalized reasoning channel and transient-only display path; new text never reaches a durable/general domain-event subscriber, historical reasoning rows are purged, and reasoning remains excluded from archives, memory, prompts, tools, logs, telemetry, hooks, evidence, persistence, and diagnostics.
- Interactive and headless model surfaces distinguish selectable, always-on, and unsupported reasoning honestly and reset/revalidate session preferences on model switch.
- Sanitized fixtures for every profile in `plan46-pi-reasoning-v1` assert exact effective capability, outbound JSON, and response normalization, documenting intentionally unsupported behavior rather than claiming false parity.
- Existing catalogs without compatibility settings retain their pre-M16 request behavior—including unsupported-level clamping—and stable model/profile identities; no user/repository configuration is rewritten automatically.
- Focused provider/configuration/security/UI/context/persistence tests, architecture gates, Scenario P, maintained manual checks, docs, ADR, status, and DOX pass.

**Prerequisites:** plans 07, 18, 26, 31, 32, and 35. This is one cohesive plan because configuration without wire projection—or wire projection without honest capability/UI behavior—would expose an unusable or misleading intermediate state.

**Scope decisions:**
- Compatibility is closed typed data, not arbitrary request transformation.
- Provider-neutral reasoning taxonomy and host authority remain unchanged; only the compiled OpenAI-compatible adapter understands wire compatibility.
- Hidden reasoning uses a separate transient-only event boundary, not the durable domain-event stream; M16 also purges historical reasoning domain-event rows.
- Pi import, multimodal support, generic sampling expansion, native non-OpenAI protocols, and session-affinity behavior are excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
