## Milestone 8 — MCP, Persistence Completion, and Operational Hardening  *(plans 18, 19, 20)*

**Status:** See the [authoritative milestone index](../milestones.md).

**Implementation note:** Plans 18–20 landed the ordered transactional migration runner (`MigrationRunner`, `DefaultMigrations`), content-addressed artifact storage (`ArtifactStore`),
event/schema-version-tolerant session restoration (`SessionRestorer`, `DomainEventMigrationRegistry`,
gap #3 — migrate or mark Legacy, never crash), age-based retention (`RetentionService`), the
host-owned MCP adapter isolating the official C# SDK (`McpAdapter`, `IMcpTransport`, `McpImportedTool`),
MCP connection profile configuration (`McpProfileConfigurationLoader`), per-server secret scope and
drain/kill timeout (gap #6), secret-free diagnostic bundles (`DiagnosticBundleGenerator` with a
canary-secret gate), and a defense-in-depth redaction audit (`RedactionAudit`). See ADR-25–ADR-28.

**Objective:** Complete integration and make the harness suitable for sustained use.

**Deliverables:**
- MCP adapter and connection profiles.
- Imported MCP tools through the standard tool pipeline.
- Full SQLite persistence and migrations.
- Session restoration.
- Diagnostic export.
- Retention and redaction policy.
- Cross-platform terminal verification.
- Performance baselines.
- Packaging and update strategy.
- Security review.
- Documentation.

**Exit criteria:**
- Sessions survive restart.
- MCP tools are governed like built-in tools.
- Diagnostic bundles exclude secrets.
- Supported terminal/OS combinations pass smoke tests.
- Installation and first-run documentation is complete.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
