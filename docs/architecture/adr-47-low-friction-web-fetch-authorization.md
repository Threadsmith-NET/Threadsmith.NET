# ADR-47: Fresh user URLs and model-proposed destinations use exact transient fetch authority

**Status:** Accepted

## Context

ADR-44 requires opaque search-result references or explicit exact direct grants before `web_fetch` is advertised. Those controls prevent model, repository, tool, and restored-conversation text from becoming network authority, but they make a user who already supplied an exact documentation URL repeat it through `/fetch-authorize`. They also leave an interactive model-proposed destination with no normal exact approval boundary.

## Decision

ADR-44 remains authoritative for transport, SSRF, redirect, content, provenance, and untrusted-evidence handling. Threadsmith changes only direct-fetch authorization ergonomics.

- After retrieval consent schema 3 is explicitly accepted, the host scans only the fresh raw top-level user message with a deterministic 32-KiB/8-candidate recognizer. It accepts normalized absolute default-port HTTPS bare or Markdown destinations only at message start or after a supported opening/token delimiter, rejects embedded substrings such as `prefixhttps://...`, collapses duplicates, and performs no DNS or network work. A candidate reaching the scan boundary is accepted only when the raw message itself ends there; the host never authorizes a prefix whose delimiter or continuation lies outside the bounded scan.
- Each accepted destination receives a random opaque `userUrlId` bound to the exact message, repository, session, run, URL digest, policy/scope generation, and expiry. The protected URL remains transient. Only the opaque ordinal mapping enters current model context; archive replay never reconstructs authority.
- A current-user reference is exact and one-shot. It activates the existing `web_fetch` schema only for its current run and is revoked on the next intake, terminal run, repository/session transition, consent/tool/options/policy rebinding, cancellation, expiry, or shutdown.
- Consent schema 2 remains valid for ADR-44 search-result and explicit direct-group behavior. It does not authorize current-message inference. Schema 3 adds disclosure that exact URLs in the current request may be contacted if the model invokes fetch and that model-proposed destinations need separate approval.
- While the schema is legitimately active, an ungranted structurally valid `url` is an authorization request. The interactive host serializes a terminal-neutral prompt showing model provenance, sanitized origin, a conservatively redacted path shape whose non-empty segments are never printed, query presence, and an exact digest without query values. Denial is the default. Approval creates one grant bound to the same pending invocation and never authorizes redirects, retries, siblings, the origin, session, or another run. Prompt lifecycle notifications are URL-free and delivered only through the attached process-local interactive adapter; they never enter the domain event stream, projections, persistence, telemetry, hooks, or restoration.
- Headless execution never prompts or reads opportunistically from standard input. It returns the stable `DirectAuthorizationRequired` tool classification. Existing exact headless grants and `/fetch-authorize` remain the automation and redirect-chain surfaces.
- Model-proposed fetch calls serialize per registration. Managed policy, hooks, repository configuration, mutation trust, extensions, MCP, model output, and fetched content may deny or narrow but cannot grant or remember authority.

All routes converge on ADR-44 before network I/O: tool availability, consent, phase/policy/hooks, URL normalization, public-address classification, connection-time pinning, manual redirect authorization, credential-free transport, bounds, cancellation, extraction, and untrusted provenance.

## Consequences

Ordinary public-document research can begin from one natural-language user turn after revised consent. Unrelated turns keep the smaller canonical tool inventory. The exact URL may still fail public-address, redirect, content, policy, timeout, or resource checks, and current-user or inline authorization never follows an unapproved redirect. Interactive clients without the prompt adapter behave like headless clients and fail closed. Protected URLs, query values, opaque references, pending grants, and approval internals are not durable state.
