## Milestone 22.1 - Low-friction Web Fetch Authorization  *(plan 61)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Remove unnecessary command friction from ordinary public-document retrieval by recognizing exact URLs in the current user turn and offering inline approval for model-proposed direct destinations, while preserving every Plan-58 consent, policy, SSRF, transport, content, and untrusted-evidence control.

**Deliverables:**
- Bounded deterministic recognition of exact public-HTTPS candidates from the fresh raw top-level user message.
- Opaque one-shot user-URL references bound to message, repository, session, run, URL digest, consent/tool/policy/options generations, and expiry.
- Progressive `web_fetch` activation for current-user candidates without permanent schema advertisement or automatic network access.
- A revised disclosure/consent version explaining current-message URL retrieval and failing closed for older consent.
- Host-owned inline approval for an exact model-proposed direct URL only while fetch is already legitimately active.
- Sanitized approval projection, safe denial/cancellation defaults, exact invocation-bound grants, and deterministic serialized prompting.
- Non-interactive headless rejection plus explicit authorization parity; continued `/fetch-authorize` support for redirect chains and automation.
- Complete convergence on Plan-58 policy, public-address/connection, redirect, credential, bounds, extraction, provenance, lifecycle, and privacy controls.
- ADR-47, Scenario AA, focused TUI/headless/security/canonical/lifecycle tests, documentation, and DOX.

**Exit criteria:**
- A user can ask Threadsmith to read an exact HTTPS page in one natural-language turn without separately typing `/fetch-authorize`, after accepting the revised disclosure.
- Candidate recognition itself performs no DNS/network I/O; only a valid governed invocation may contact the exact destination.
- Current-user references are one-shot, non-restorable, raw-intake-proven, and invalid after replay, expiry, next top-level turn, terminal run, repository/session transition, or consent/tool/policy/options change.
- URLs originating from model, repository, prior/restored conversation, memory, tools, fetched content, extensions, MCP, or hooks cannot acquire current-user authority.
- A model-proposed URL performs zero DNS/network activity until an explicit inline approval grants that exact pending invocation; denial, cancellation, or unavailable prompting fails safely.
- Headless mode never prompts or silently approves, and explicit headless or `/fetch-authorize` flows retain deterministic exact-chain behavior.
- Neither current-user nor inline approval authorizes an origin or redirect; direct redirect chains remain atomically pre-authorized through the existing explicit surface.
- Unrelated turns retain the smaller canonical tool inventory, and all Plan-58 consent, policy, SSRF, transport, content, provenance, lifecycle, and privacy regressions pass unchanged.
- Focused automated and maintained interactive/headless coverage, ADR-47, Scenario AA, docs, status, and DOX pass.

**Prerequisites:** plan 58 plus plans 03, 08, 18, 20, 27, 35, 36, 40, 49, and 51-57.

**Scope decisions:**
- Improve authority ergonomics rather than broadening network trust.
- Exact current-user URLs carry one-run, one-shot authority only under a revised disclosure; no intent-classifier request is added.
- Model-proposed direct URLs remain denied until host-owned inline approval and are never remembered by origin/session.
- Progressive disclosure remains mandatory; no permanent authorization-request tool is added.
- Interactive approval is unavailable in headless mode; explicit pre-authorization remains the automation contract.
- `/fetch-authorize` remains supported for known redirect groups and advance authorization.
- Plan-58 public-HTTPS, connection-time SSRF, credential isolation, content bounds, extraction, and untrusted-evidence rules are unchanged.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
