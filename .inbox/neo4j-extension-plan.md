# Neo4j extension plan

**Status:** Ready for one implementation pass when requested.
**Delivery track:** Bundled extension in the main repository.
**Host contract:** Use the [implemented extension secret and settings contract](../docs/implementation-plans/maintenance-extension-secrets-and-configuration.md). Section 17 of that plan records validation limits. Choose and verify driver, server, and deployment settings in the same implementation pass. Record the choices in the extension documentation.
**Language:** ASD-STE100 Simplified Technical English (STE) inspired. Use short sentences and direct instructions. This document does not claim formal STE compliance. Product names, API names, and Cypher terms are technical names.

## 1. Objective

Add a Threadsmith extension that lets a model:

1. Submit Cypher as a string.
2. Receive structured query results.
3. Create, update, and delete database data when permission is granted.
4. Request structured schema information.

Use configured connection settings. Resolve the password through the host secret service. Keep all database work inside the normal extension tool invocation path.

## 2. Architectural context

Use `IThreadsmithExtension` and `IToolCapability`. Register the tools through the existing capability registrar. Let the host perform policy checks, approval, scheduling, activity publication, and completion.

Keep Neo4j driver types inside an internal adapter in the extension. Return contract-owned JSON data through `ExtensionToolResult`. Do not add a driver reference to Core, the extension contract package, or terminal interfaces.

Use one connection and execution implementation for both tools. Schema discovery is a separate model operation, not a separate transport or permission path.

This proposal is stored in `.inbox`. It does not change the milestone index or create implementation authority. Follow `docs/implementation-plans/planning-governance.md` when the proposal becomes active work.

## 3. Scope

- Connection configuration and validation.
- Password resolution during admitted execution.
- A Cypher execution tool.
- A structured schema-discovery tool.
- A forced read-only deployment mode.
- Host approval and server authorization for writes.
- Bounded query results and schema discovery.
- Safe failure reporting, cancellation, and explicit transaction outcomes.
- Packaging, automated tests, and operator instructions.
- Integration with the host external-service category, statement review and transaction outcome reporting.

## 4. Non-scope

- A general database administration interface.
- Model-supplied endpoints, usernames, or passwords.
- A graph browser, visualization system, or terminal-specific renderer.
- File import, export, backup, or restore features.
- Automatic database migrations or schema changes during activation.
- An unrestricted procedure catalog or server administration API.
- A custom Cypher language parser.
- Automatic replay of uncertain writes.
- Source-edit rollback for database changes.
- Multiple named connections in the first version.

## 5. Current state

The following inspected sources establish the extension points:

| Source | Relevant behavior |
| --- | --- |
| `samples/extensions/MinimalToolExtension/MinimalToolExtension.cs:7-36,39-155` | Shows activation, configuration reads, tool registration, and JSON results. |
| `src/Threadsmith.Extensions.Abstractions/CapabilityContracts.cs:97-170,205-249` | Defines tool schemas, static safety metadata, declarations, and invocation context. |
| `src/Threadsmith.Extensions.Abstractions/ExtensionContracts.cs:98-123` | Exposes package trust, permissions, and access disclosures. Enabling the package grants its disk and network access. |
| `src/Threadsmith.Extensions.Runtime/CapabilityProxy.cs:92-153` | Adapts execution and holds an invocation lease. It maps failed extension results to host failures and preserves bounded result JSON. |
| `src/Threadsmith.Tools/ToolInvocationPipeline.cs:628-698` | Keeps the durable approval action generic and sends the complete statement review to the operator. |
| `src/Threadsmith.Tools/ToolInvocationPipeline.cs:749-845` | Serializes and sanitizes results, then applies output limits. The extension must also bound data before building its result. |
| `src/Threadsmith.Extensions.Runtime/HotReplacementCoordinator.cs:36-74` | Publishes a new generation and drains the old one. |

Set `Category` to `ExtensionToolCategory.ExternalService` on both tools. Use `WritesExternalData` for write-capable execution and `ReadOnly` for schema and forced-read-only tools.

No Neo4j driver or server behavior was verified for this plan. Verify supported versions, APIs, authorization features, and discovery queries while implementing the adapter. Do not treat the example settings or tool schemas below as shipped interfaces.

## 6. Proposed design

### 6.1 Model tools

Use two tools with one internal adapter:

| Proposed tool | Purpose |
| --- | --- |
| `neo4j_execute` | Execute one parameterized Cypher statement and return bounded results and transaction metadata. |
| `neo4j_schema` | Return requested schema sections with explicit evidence and completeness limits. |

Keep execution input simple:

```json
{
  "cypher": "MATCH (p:Person {id: $id}) RETURN p.name",
  "parameters": { "id": "person-123" }
}
```

`cypher` is required. `parameters` is optional. Do not accept credentials, URI overrides, approval flags, or a read-only override from the model.

Execute one statement in one explicit transaction in the first version. Reject unsupported multi-statement input. Do not use a keyword filter to determine whether the statement is safe or read-only. Select and test a driver call that enforces the single-statement contract.

An illustrative schema request is:

```json
{
  "sections": ["nodes", "relationships", "constraints", "indexes"],
  "sampleLimit": 1000
}
```

Use a host-configured upper bound for sampling. A request can lower the limit but cannot raise it. The tool does not accept arbitrary discovery queries.

### 6.2 Configuration

Declare `FullyTrustedAutomation` in the package manifest `RequiredTrust` field. Declare `network-access` and `secret-access` permissions. List `database-read` and, if supported by the package, `database-write` in `AccessTypes`. List disk reads or writes if the implementation performs them. The operator must set repository trust to this level before loading the package. In each tool, declare the configured password reference during policy inspection. Resolve it through `context.Secrets` during execution and pass the revealed value directly to the driver.

Supply settings through the host configuration path. Use a URI, not only a hostname. A URI specifies scheme, port, and TLS mode.

Illustrative extension settings, shown as string values to match the existing configuration view:

```json
{
  "uri": "neo4j+s://graph.example.com:7687",
  "database": "neo4j",
  "username": "threadsmith",
  "passwordReference": "secrets:neo4j:primary:password",
  "forceReadOnly": "true",
  "queryTimeoutSeconds": "30",
  "maximumRows": "500",
  "maximumResultBytes": "65536",
  "schemaSampleLimit": "1000"
}
```

These values are proposed defaults, not measured capacity limits. Confirm them against host invocation and output limits. Reserve space for metadata in the result bound.

Validate required values during activation without opening a connection. Reject credential-bearing URIs, unsupported schemes, invalid values, and unsafe limit combinations. Reject unknown safety-related keys. Treat an invalid `forceReadOnly` value as a configuration error. Default it to `true` when absent.

Require TLS with normal certificate validation for remote deployments. Permit an explicit local development exception only if the deployment contract allows it. Never silently disable certificate checks.

A configuration change must use the host load or replacement process. A model argument must not change the target, permissions, or limits.

### 6.3 Invocation and connection lifecycle

1. Validate and bound tool arguments.
2. Declare the configured password reference for policy evaluation. Use the endpoint from the captured settings.
3. Let the host finish normal policy, approval, budget, and scheduling checks.
4. Resolve the password through the invocation-scoped secret service.
5. Create the driver connection and session inside execution.
6. Run the bounded operation with linked cancellation and a server-side transaction timeout.
7. Complete or roll back the transaction as appropriate.
8. Dispose each acquired transaction, session, and driver in the extension operation’s `finally` path (`await using` where supported). Finish disposal before the operation exits. The host may already have returned an interruption receipt.
9. Return a driver-free result.

Start with invocation-owned driver lifetime. This avoids keeping authentication material in a generation-wide pool after secret access ends. Measure connection cost before proposing a longer-lived pool. A pool requires an explicit credential lifetime, rotation, policy, and disposal contract. It must not be a silent optimization.

Do not connect or fetch schema during activation. Do not create background discovery tasks. Do not cache credentials across invocations.

Enabling the extension grants its driver network access, including connections to cluster members discovered by routing. Verify the supported driver URI schemes and document direct and routed behavior. The deployment supplies TLS and operating-system network controls.

### 6.4 Forced read-only behavior

`forceReadOnly` is a hard safety setting. The model cannot override it.

Use the driver's read access mode where supported. Also use a database account with server-enforced read-only privileges. Read routing alone is not authorization. A query prefix or keyword list is not authorization.

Confirm which supported Neo4j editions and versions can enforce the required account and procedure restrictions. Do not assume all editions provide the same role controls. Restrict procedures and server features that can cause side effects. Do not describe read-only mode as safe for arbitrary Cypher until the supported server configuration enforces that claim.

If that guarantee cannot be established for a target, reject the claimed forced read-only configuration or explicitly narrow the supported query surface. Do not silently switch to write-capable execution. Do not probe write privileges by mutating a production database.

Database read access can still disclose confidential data or consume resources. Disclose network use and use the normal secret service. Document server-side outbound network controls for features that fetch external resources.

### 6.5 Write permissions and approval

When `forceReadOnly` is false, treat `neo4j_execute` as write-capable for every invocation. Mark it non-idempotent. Require explicit host approval. Keep Neo4j's own privileges as a separate final check.

Do not classify arbitrary Cypher as read-only from its text. Static conservative metadata is acceptable for the first version. It means a read query submitted through the write-capable tool can also need approval.

Set `RequiredApproval` to `User` for the write-capable tool. Register `StatementReview = new ExtensionStatementReview(destination, database, "cypher", "parameters")`. The host builds a bounded review with:

- The extension and tool identity.
- The configured destination and database.
- The submitted statement.
- Parameter names and safe type information. Hide parameter values by default.
- The fact that external data can change and source rollback does not apply.

Reuse host approval presentation. Do not print an extension-owned prompt or ask the model to grant permission. Bind approval to the admitted arguments, settings snapshot, generation, and destination. Never execute hidden query text beyond the approved representation. If a full safe representation cannot be shown within the bound, reject the request or use an existing host full-content review path.

Cypher can contain sensitive literals. Prefer parameters. Do not log full query text by default. Ensure approval text follows the existing sanitization and retention rules. Reject credential material when the extension can identify it safely; do not promise a complete sensitive-data detector.

Set `SideEffect` to `WritesExternalData` and `Idempotency` to `NonIdempotent` on the write-capable tool.

### 6.6 Query execution and outcomes

Use the selected driver's transaction API without automatic write retries. Inspect driver defaults. A retrying managed-transaction API is not acceptable unless its replay behavior is explicitly controlled.

Distinguish these states in the output contract:

- `committed`: the server confirmed commit.
- `notStarted`: the database operation never began. A host denial before dispatch also has this outcome.
- `rolledBack`: the database confirmed rollback.
- `unknown`: execution began, but the final outcome cannot be proved.

Use a separate phase field to distinguish connection, execution, result consumption, and commit failures. A generic timeout does not prove rollback. Never advise automatic replay of an `unknown` write.

Bound retained rows and serialized bytes while reading. Do not build an unlimited list and then truncate it. Handle a single oversized record as well as many small records.

Reaching an output limit is not, by itself, a failed transaction. Define driver-specific cursor handling so a bounded response cannot accidentally abandon a write before commit. If completion requires consuming more rows, discard excess rows without retaining them, within the operation's time and work limits. If those limits prevent a known outcome, report the actual outcome or `unknown`. Do not claim success merely because some records arrived.

Do not append a textual `LIMIT` to user Cypher. It can change semantics and does not bound all database work. Use server resource controls and transaction limits as well as client output limits.

### 6.7 Structured result values

Return versioned JSON with:

- Column names and ordered records.
- Transaction outcome and execution phase.
- Update counters when available.
- Safe server notifications.
- Truncation state, returned row count, and the limits reached.
- Safe failure classification when the operation fails.

Use tagged values where plain JSON loses information. Specify mappings for nodes, relationships, paths, large integers, bytes, temporal values, durations, spatial points, null, and nested collections. Preserve node labels and relationship direction. Do not use driver `ToString()` as the data contract.

Represent large integers without JSON precision loss. Bound nesting depth, collection entries, string lengths, and individual value size. Document entity identifiers as database-scoped identifiers, not permanent business keys.

Return `Succeeded = false`, a safe `Error`, and bounded `ResultJson` for database failures. Preserve confirmed transaction outcomes in the JSON. A host interruption after dispatch yields `unknown` when no confirmed result is available. A denial before dispatch yields `notStarted`.

### 6.8 Structured schema discovery

Keep declared schema separate from observed data shape.

Declared sections:

- Constraint definitions, affected labels/types, properties, and constraint kind.
- Index definitions, entity type, properties, index type, and safe state information.

Observed sections:

- Node labels and observed label combinations.
- Node property names and observed types.
- Relationship types and property names/types.
- Observed source-label, relationship-type, and target-label combinations.

For each section, return its source method, status, limits, and completeness. Distinguish `complete`, `sampled`, `truncated`, `permissionDenied`, and `unsupported` where applicable. A missing section is not an empty schema. A sampled absence is not proof that a property or relationship cannot exist.

Return no example property values by default. Do not return node instances as schema. Do not infer mandatory properties from a sample. Constraints, not observations, establish declared requirements.

Choose discovery queries after verifying supported server versions and editions. Prefer built-in metadata. Do not require APOC or another plugin in the first version. Do not silently fall back from a denied metadata query to an unrestricted graph scan.

Bound server work, sample size, result size, and invocation duration. A Cypher `LIMIT` on returned rows does not prove that a discovery operation avoids a full scan. Use a query plan or documented server behavior to check expensive discovery methods.

Allow useful partial results when a schema section is unavailable. Mark the partial status in the tool output. Do not cache schema across invocations in the first version. This avoids stale schema after writes and keeps the initial ownership model simple.

## 7. Public contracts

Use JSON schemas with an explicit output schema version. Make errors and transaction outcomes machine-readable. Keep descriptions clear about limits and uncertainty.

The model supplies only query data or schema options. Configuration supplies the connection. Host policy supplies permission. The server supplies final database authorization.

Use separate input/output DTOs for execution and schema discovery. Share the adapter and bounded value conversion. Keep driver interfaces private to the extension project.

## 8. Project and file changes

Implement Neo4j in `src/Extensions/Neo4j/Threadsmith.Extensions.Neo4j.csproj`. The `src/Extensions/Extensions.csproj` project builds the extensions distributed with this repository. Each extension owns its folder and assembly.

Expected changes:

- Update `eng/release/Publish-Release.ps1` and its payload checks to stage the existing Neo4j project output under `extensions/Neo4j/` in the distribution. Include `threadsmith.extension.json`, the extension assembly, and all private driver runtime dependencies. Exclude the host SDK assembly from that folder. Document how to select the bundled package for explicit load.
- An extension manifest and supported configuration example with no real credentials.
- One internal driver adapter and shared bounded value conversion.
- Execution and schema capability implementations.
- A self-contained extension test project and disposable database fixtures.
- A central package pin in `Directory.Packages.props` for the verified driver version.
- Extension metadata and tests that exercise the host contracts in Section 6.
- Documentation and applicable architecture checks.

Do not add a second extension loader, secret reader, policy engine, or result renderer.

## 9. Ordered tasks

1. Use the existing host contracts and Neo4j project. Carry the work through implementation, packaging, tests, and documentation in one pass.
2. Select the supported Neo4j server versions, editions, and .NET driver version. Verify licensing and .NET compatibility.
3. Verify cancellation, transaction timeout, commit, retry, routing, and schema APIs against that support set.
4. Implement and test server-enforced read-only access. Exclude unsupported deployments from the support matrix and document supported routing modes.
5. Specify JSON schemas, value mappings, outcome states, settings defaults, and resource limits.
6. Use the shared statement review and external-write outcome contracts. Verify them with the packaged tool.
7. Add packaging and activation-time settings validation. Keep activation offline.
8. Implement the shared adapter and execution tool. Start with read-only test coverage.
9. Add write execution, explicit commit handling, and uncertain-outcome tests.
10. Implement bounded schema discovery with declared/observed separation.
11. Run through real extension load, tool policy, approval, completion, cancellation, replacement, and unload paths.
12. Add disposable-server integration coverage. Never use a production endpoint for destructive tests.
13. Complete user documentation, final validation, and adversarial review.

## 10. Testing

Follow the repository testing rules in `AGENTS.md`. Before editing test files, list the exact test names and one sentence for each purpose. If proposing more than four tests, obtain the approval required by that file. Reuse existing coverage. The lists below describe behaviors to verify, not a required test count. Use pure unit tests by default; keep each under 50 ms. Every individual test, including each theory case and disposable-server test, must finish in under 60 seconds. Use deterministic barriers and test-runner cancellation. Do not enforce runtime targets with sleeps, retries, or arbitrary deadlines.

### Unit and contract tests

- Required settings, safe defaults, invalid boolean handling, URI validation, and byte/row bounds.
- No network or password resolution during activation.
- Argument validation and parameter type conversion.
- Exact secret declarations and connection settings from the captured generation.
- No per-call override of destination, credentials, or read-only mode.
- Safe metadata for both configured execution modes.
- Tagged conversion for all supported driver values and oversized individual values.
- Structured schema JSON and declared/observed separation.
- Explicit partial, unsupported, and permission-denied schema sections.
- Sanitized failures and correct host failure classification.
- No password, authentication token, or unrestricted driver exception in results or logs.

### Host integration tests

Load the packaged extension through the real manager. Invoke it through the normal tool pipeline.

Verify policy denial, approval denial, secret denial, and cancelled approval. Verify the enabled extension can use the configured endpoint when the host tool destination list is empty. Assert that the adapter does no database work before admission. Verify manual and model-driven entry points use the same policy and lifecycle owners. Check child-agent visibility where tools are exposed to delegated runs.

Verify replacement and unload. Hold a dispatched write at a controlled adapter barrier, interrupt the host, and observe one `unknown` receipt. Release the barrier. Verify that the operation disposes its resources, executes the write only once, publishes no second terminal event, and permits generation unload.

### Disposable database tests

- Read queries, parameters, empty results, nulls, and driver-specific value types.
- Create, update, and delete operations under an authorized write account.
- Server rejection of mutations under the supported read-only account configuration.
- Procedure and schema-mutation restrictions required by the read-only contract.
- Wrong credentials, missing database, denied privileges, and unavailable server.
- TLS validation and direct/routed destination behavior for supported modes.
- Result truncation with a confirmed write commit.
- Failure before execution, confirmed rollback, and lost connection near commit.
- No automatic replay after an uncertain write.
- Server-side timeout and explicit cancellation.
- Empty and mixed-label graphs, constraints, indexes, and relationship endpoints.
- Sample limits, oversized schema, permission-limited metadata, and version differences.

Use explicit barriers and test-runner cancellation for concurrency tests. Do not add arbitrary runner-speed assertions. Keep destructive tests confined to disposable databases with generated test data.

### Verification during implementation

Read current CI workflows during implementation. Follow `CONTRIBUTING.md` for the pinned ripgrep fixture, restore, build, formatting, and analyzer checks. Run targeted test classes locally with the repository runner. The full suite belongs to normal CI. Include architecture and extension-package checks. Run `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` before any PR or follow-up push.

Run server integration tests against the declared support matrix. Report which OS, server edition/version, and driver version each result covers. Skipped server tests are not passing server validation. Do not claim Windows results cover Linux or macOS.

This planning task does not authorize package restore, container downloads, database connections, builds, or tests now.

## 11. Security and permissions

Use a dedicated least-privilege database account. Do not use an administrator account. Store only the logical password reference in normal settings.

Treat query results, schema names, server notices, and error text as untrusted data. They must not become host instructions. Preserve the ordinary tool-result trust boundary.

Disclose network use and resolve credentials through the admitted secret service for read-only operations. Server permissions, host permissions, and user approval are separate controls. None replaces the others.

An in-process extension is trusted code, not sandboxed code. The approved deployment must also control server procedures, outbound server access, and database resource limits.

## 12. Observability

Use existing host tool activity for start, policy, approval, completion, failure, and cancellation. Do not add a parallel status stream.

Report safe phase, duration, row counts, truncation, update counters, and transaction outcome. Omit passwords, parameter values, and full query text from normal logs. Bound and sanitize diagnostic text and graph identifiers.

A schema result must state sampling and unsupported sections. A write failure must state when commit outcome is unknown. These are model-visible correctness requirements, not optional log details.

## 13. Migration and compatibility

Install the extension explicitly. Do not enable it by default. Start with `forceReadOnly=true` and document the required server account setup.

Pin the driver centrally. Publish the supported server and extension-contract versions. Test package dependency isolation through the existing collectible load context.

Keep configuration changes generation-scoped. Resolve the current secret at invocation time. Document that an operation already in flight can still use the credential it previously obtained.

Database writes cannot be undone by Threadsmith source rollback. Operator recovery uses database-owned backups or corrective queries. Do not advertise automatic undo.

## 14. Acceptance criteria

- [ ] Extract the assembled release artifact into a clean directory. Check the Neo4j package for its manifest, extension DLL, and private driver dependencies. Verify `Threadsmith.Extensions.Abstractions.dll` is absent from the extension folder. Load the extracted package through the normal manager and invoke both tools against the disposable database.
- [ ] Query input accepts a Cypher string and optional parameters without connection overrides.
- [ ] Password resolution occurs only after admitted execution starts.
- [ ] The enabled extension connects in each supported routing mode without host destination grants.
- [ ] Forced read-only behavior is enforced by the documented supported server configuration.
- [ ] The model cannot weaken read-only mode or grant its own write permission.
- [ ] Write-capable calls receive conservative metadata and meaningful host approval.
- [ ] Authorized create, update, and delete operations work in disposable-server tests.
- [ ] Results remain structured and bounded during consumption and serialization.
- [ ] Commit outcome remains distinct from output truncation and tool failure.
- [ ] Unknown write outcomes do not trigger automatic replay.
- [ ] Schema discovery separates declared rules from observed data and reports its limits.
- [ ] Failure, cancellation, replacement, and unload preserve host lifecycle behavior.
- [ ] No driver types or credentials leak into host durable state in the tested paths.
- [ ] Final compiler, analyzer, test, package, and documentation results are recorded with honest limits.

## 15. Risks

- Read routing can be mistaken for read-only authorization. Enforce server privileges.
- Routing can introduce new network targets. Document this behavior for operators.
- Driver retry defaults can duplicate writes. Verify and control them.
- Cursor truncation can hide an incomplete transaction. Report confirmed outcomes separately.
- Schema discovery can scan a large graph before returning a small result. Bound server work.
- Database values can be large or deeply nested. Bound conversion before serialization.
- Raw driver messages can contain sensitive query data. Translate failures at the adapter boundary.
- Static metadata can understate external side effects. Verify the registered category and side effect in each configured mode.
- Connection reuse can retain credentials beyond invocation lifetime. Start without a cross-invocation pool.

## 16. Documentation

Provide installation, configuration, secret-reference setup, and safe server-account instructions. Document TLS, routing behavior, resource limits, procedures, and supported versions.

Provide examples for a parameterized read, an approved write, a truncated result, an unknown commit outcome, and partial schema discovery. Use generated example data only.

Explain that `forceReadOnly=false` permits write requests; it does not grant host approval or database privileges. Explain that schema samples are observations, not guaranteed database rules.

Add applicable acceptance scenarios and manual checks through their existing owners. Keep completed milestone contracts unchanged.

## 17. Decisions to record during implementation

Make these choices as part of the same delivery:

1. Confirm the publisher identity, license notice, and delivery ownership.
2. Select the .NET driver and supported Neo4j versions/editions. Verify APIs against those exact versions.
3. Establish a provable forced read-only deployment contract, including procedure restrictions.
4. Select supported direct and routed URI schemes for the driver and deployment.
5. Bind the registered review destination and database to the driver settings for that generation.
6. Specify driver-specific conditions for confirmed commit, confirmed rollback, and unknown outcome.
7. Finalize configuration bounds, result mappings, and schema sampling methods.
8. Define the disposable-server CI fixture and supported integration matrix.

**Plan preparation checks:** The existing extension sample, tool contracts, proxy, pipeline, and replacement path were inspected. No product source was changed. No package, server, build, analyzer, test, or performance validation was run. Verify driver and server behavior as part of implementation.
