# ADR-28: Secret-free diagnostic bundles

Status: Accepted
Date: Milestone 8 (plan-20); amended 2026-11-08
Strategy: §23.4

## Context

Diagnostic bundles combine logs, events, artifacts, configuration, and version information.
The host sanitizes output before persistence. Bundle generation is another aggregation boundary that requires sanitization.

## Decision

- `DiagnosticBundleGenerator` (Telemetry) assembles logs, events, artifacts, sanitized configuration,
  and version info into a zip. Every text entry passes through the host `SecretOutputSanitizer`
  before it is written; the generator counts redactions and refuses bundles that exceed the
  configured byte limit (deleting the partial archive).
- `BundleExcludesCanaryAsync` scans a generated bundle for a canary secret; this is the objective
  gate for the "diagnostic bundles exclude secrets" exit criterion.
- Remove retrospective persisted-store auditing and automatic artifact repair from the product.
  Startup initializes storage and retention without scanning stored content for secrets.
  Remove the audit configuration and its dedicated storage APIs.

## Consequences

- Bundle generation retains sanitization, size limits, and the canary-secret regression gate.
- Diagnostic bundle options remain configurable through `diagnostics:*`.
- Normal output sanitization remains at the existing persistence, logging, and rendering boundaries.
