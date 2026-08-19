# Plan 53 — Hierarchical Repository Instruction Bundles

**Milestone:** M19 — Cache-Optimized Context Generation

**Prerequisites:** plan 52 and plans 05, 09, 18, 29, 35

**Depends on by:** plans 54–55

**Status:** Complete. Confined hierarchy and independent turn-boundary revalidation coverage pass. MTP-216 remains the maintained cross-platform filesystem/watcher regression procedure.

## 1 Objective

Resolve hierarchical `AGENTS.md` files and configured prompt appends into confined, normalized, versioned repository-stable instruction bundles placed near the start of model requests with precise invalidation.

## 2 Architectural Context

Threadsmith already supports bounded prompt append assets, while this repository's DOX contract demonstrates parent-to-child instruction precedence. Model-visible repository instructions need host-owned resolution rather than ad hoc evidence retrieval.

## 3 Scope

Root and applicable nested `AGENTS.md`; parent-to-child precedence; repository/working-scope confinement; link/reparse protection; bounded strict decoding; normalized hashing/versioning; prompt-append integration; bundle cache and invalidation; inspection/provenance; trust-change handling.

## 4 Non-Scope

Executing instruction content, treating repository text as higher authority than host policy, broad documentation crawling, or invalidating bundles for ordinary source changes.

## 5 Current State

The host resolves a confined root-to-working-scope `AGENTS.md` chain and configured prompt appends into an ordered content-addressed bundle at every turn boundary. Sources are strict UTF-8, bounded, prohibited from reparse traversal, checked for read races, and remain untrusted below host policy. Prompt appends re-fingerprint independently of watcher delivery. Focused hierarchy/change coverage passes; maintained cross-platform filesystem/watcher checks remain.

## 6 Proposed Design

Resolve from canonical repository root toward the canonical working scope. Include only `AGENTS.md` files on the applicable directory chain. Preserve explicit parent then child ordering; content remains untrusted repository instruction subordinate to host policy. Combine with ordered configured prompt appends under distinct provenance.

Bundle identity includes repository identity, canonical working scope, ordered path/version chain, prompt-append IDs/versions, trust generation, normalization version, and content digest. At every turn boundary, re-resolve the applicable source chain and compare race-safe source fingerprints derived from confined snapshots, including content digests, before reusing a cached bundle; filesystem watchers provide eager invalidation only and are never the correctness authority. Normal watcher events invalidate affected scopes, while watcher overflow, error, or loss conservatively invalidates every instruction bundle for that repository before watcher recovery. Symlinks/junctions/reparse points, case collisions, replacement races, size/count/depth limits, and malformed encoding fail closed or produce bounded actionable diagnostics according to source policy.

## 7 Public Contracts

Add host-owned instruction source/version, ordered bundle, bundle identity/digest, resolution diagnostic, and cache invalidation reason records. Filesystem/provider types do not cross boundaries.

## 8 Project/File Changes

`Threadsmith.Context`, repository lifecycle/watchers, configuration prompt assets, persistence/execution records, `/context` and headless inspection, tests/fixtures, docs, ADR, and DOX.

## 9 Ordered Tasks

1. Inspect prompt-append and repository path/link safety precedents.
2. Define precedence, bounds, identities, and normalization.
3. Implement confined hierarchical resolution with replacement-race checks.
4. Integrate prompt appends and cache bundles by exact source chain.
5. Place the bundle after host policy in structured requests.
6. Add turn-boundary source-chain fingerprint revalidation, precise watcher/trust/scope invalidation, conservative repository invalidation on watcher overflow/error/loss, and restoration tolerance.
7. Add adversarial tests, inspection, docs, manual tests, ADR, and DOX.

## 10 Testing

Cover root-only, nested precedence, sibling isolation, scope changes, one-file invalidation, ordinary source non-invalidation, prompt-append changes, trust changes, restart, path traversal, escaping links/reparse points, races, case collisions, malformed UTF-8, and bounds. Simulate dropped notifications plus watcher overflow/error/loss and prove the next turn-boundary fingerprint check cannot reuse stale applicable instructions. Golden fixtures prove unchanged bundles are byte-identical.

## 11 Security and Permissions

Repository instructions are untrusted data and cannot override host safety, authority, tool, approval, secret, or confinement policy. Resolution performs no execution and never follows an escaping link.

## 12 Observability

Record bounded paths relative to repository, source versions/digests, bundle digest, cache hit/miss, invalidation reason, and token count; never duplicate private content into logs.

## 13 Migration and Compatibility

Repositories without AGENTS.md preserve existing prompt-append behavior. Existing prompt asset execution references remain readable. Bundle versions change only through explicit normalization/source changes.

## 14 Acceptance Criteria

- Applicable parent-to-child instructions resolve deterministically and safely.
- Unchanged bundles are byte-identical and cacheable.
- Ordinary source edits do not invalidate instruction bundles.
- Every turn boundary revalidates the applicable instruction and prompt-append source fingerprints independently of watcher delivery.
- Watcher overflow, error, or loss conservatively invalidates all instruction bundles for the affected repository.
- With reliable watcher delivery, applicable instruction/prompt/trust/scope changes invalidate exactly affected bundles; watcher failure uses the conservative repository-wide rule above.
- Host policy remains authoritative.

## 15 Risks

Ambiguous nested scope, filesystem aliasing, watcher races or notification loss, excessive instructions, and policy-confusion attacks. Mitigate with canonical identity, bounded reads, pre/post validation, mandatory turn-boundary fingerprint snapshots, conservative watcher-failure invalidation, and labeled roles.

## 16 Documentation

Document discovery, precedence, limits, trust, invalidation, prompt-append interaction, and inspection. Implementation must update repository/user guidance and manual tests.

## 17 Open Decisions

Finalize whether a missing root AGENTS.md permits nested discovery and how multiple active file scopes produce a least-common instruction bundle before implementation.
