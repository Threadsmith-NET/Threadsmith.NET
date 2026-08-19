# ADR-28: Secret-free diagnostic bundles and redaction audit

Status: Accepted
Date: Milestone 8 (plan-20)
Strategy: §23.4, §19.6

## Context

The M8 exit criterion requires that diagnostic bundles exclude secrets by construction, and that
persisted stores are auditable for unredacted secrets. The host already sanitizes every output before
it is persisted (M1), but a support bundle is a new aggregation path and a defense-in-depth audit is
required for the durable store.

## Decision

- `DiagnosticBundleGenerator` (Telemetry) assembles logs, events, artifacts, sanitized configuration,
  and version info into a zip. Every text entry passes through the host `SecretOutputSanitizer`
  before it is written; the generator counts redactions and refuses bundles that exceed the
  configured byte limit (deleting the partial archive).
- `BundleExcludesCanaryAsync` scans a generated bundle for a canary secret; this is the objective
  gate for the "diagnostic bundles exclude secrets" exit criterion.
- `RedactionAudit` (Persistence) scans a bounded sample of persisted event payloads and artifacts at
  startup for configured secret patterns. Findings are logged; artifacts are re-sanitized when
  `persistence:redactionAudit:repairArtifacts` is set. Event payloads are immutable history and are
  never rewritten — findings are reported only.

## Consequences

- Support bundles are safe to share by construction; the canary test gates regressions.
- The redaction audit is defense-in-depth: a correctly functioning store has zero findings.
- Both are configurable (`diagnostics:*` and `persistence:redactionAudit:*`) and disabled-by-config
  capable.