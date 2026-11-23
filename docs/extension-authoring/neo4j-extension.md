# Neo4j extension

The bundled Neo4j extension lets a model run one Cypher statement at a time against one configured
database and ask for structured schema information. It ships in the release payload under
`extensions/Neo4j/` and is **not** enabled by default.

See [Authoring a Threadsmith.NET Extension](authoring-guide.md) for the contracts this extension
implements.

## 1. What it provides

| Tool | Purpose |
| --- | --- |
| `neo4j_execute` | Runs one Cypher statement in one explicit transaction and returns bounded, tagged records with an explicit transaction outcome. |
| `neo4j_schema` | Returns cached labels, directed relationship patterns, sampled properties and types, and index definitions. Detailed property discovery and constraints can be requested separately. |

The model supplies only query data. The connection target, account, credential reference, access
mode, and every resource limit come from repository configuration. A model argument can never change
them.

## 2. Install and enable

1. Copy `extensions/Neo4j/` from the release payload into your extension directory. The folder must
   keep `threadsmith.extension.json`, `Threadsmith.Extensions.Neo4j.dll`, and `Neo4j.Driver.dll`.
   It must not contain `Threadsmith.Extensions.Abstractions.dll`; the host owns the single copy of
   the contract assembly and rejects a package that ships its own.
2. Set repository trust to `FullyTrustedAutomation`. The package declares that requirement and the
   `network-access` and `secret-access` permissions, with `database-read` and `database-write`
   access types.
3. Add the settings section below.
4. Enable the extension through `/extensions`.

Enabling the extension grants its driver network access under the account running Threadsmith,
including connections to cluster members discovered by routing.

## 3. Configuration

All values are strings. Only these keys are accepted; any other key is a configuration error.

```json
{
  "uri": "neo4j+s://graph.example.com:7687",
  "database": "neo4j",
  "username": "threadsmith-reader",
  "passwordReference": "secrets:neo4j:primary:password",
  "readOnly": "true",
  "queryTimeoutSeconds": "30",
  "maximumRows": "500",
  "maximumResultBytes": "65536",
  "schemaSampleLimit": "1000"
}
```

| Key | Required | Default | Rule |
| --- | --- | --- | --- |
| `uri` | yes | — | A credential-free `bolt`, `bolt+s`, `bolt+ssc`, `neo4j`, `neo4j+s`, or `neo4j+ssc` address with a host and no path, query, or fragment. |
| `database` | yes | — | At most 63 ASCII letters, digits, `.`, `-`, or `_`. |
| `username` | yes | — | The least-privilege account. |
| `passwordReference` | yes | — | A logical reference the host resolves; never the password itself. |
| `readOnly` | no | `true` | `true` uses driver read mode without an approval prompt; `false` uses write mode with normal approval. Any other value is a configuration error. |
| `queryTimeoutSeconds` | no | `30` | 1–300. Applied as the server-side transaction timeout. |
| `maximumRows` | no | `500` | 1–10000 retained records. |
| `maximumResultBytes` | no | `65536` | 4096–262144 retained UTF-8 record bytes. |
| `schemaSampleLimit` | no | `1000` | 1–10000. A request can lower it but never raise it. |
| `allowInsecureLocalConnections` | no | `false` | Permits the direct `bolt` and `bolt+ssc` schemes **for a loopback host only**. Remote and routed addresses are rejected either way. |

The settings are validated when the extension activates. Activation opens no connection, resolves no
credential, and starts no background work. A configuration change takes effect through the normal
host load or replacement process.

For existing configurations, rename `forceReadOnly` to `readOnly` and remove
`readOnlyEnforcedByServerAccount`. The old keys are no longer accepted.

### TLS

Use `bolt+s` or `neo4j+s` so the driver validates server certificates. Those are the only schemes
accepted for a remote server. Certificate validation is never silently disabled.

`allowInsecureLocalConnections` relaxes this for a loopback host only, and only for the direct
`bolt` and `bolt+ssc` schemes. The self-signed `+ssc` schemes trust whatever certificate the server
presents, so they prove nothing about its identity. A remote address is rejected even when the
setting is `true`, and a routed `neo4j` scheme is rejected with it because the routing table can
advertise members on other hosts.

### Routing

`neo4j`, `neo4j+s`, and `neo4j+ssc` use cluster routing: the driver may connect to any member the
routing table returns. `bolt`, `bolt+s`, and `bolt+ssc` connect to exactly one server. Choose the
direct schemes when your network controls must enumerate every destination.

### Credential

Store only the logical reference in settings. The host resolves it through the secret service
**after** it admits the invocation, and the extension passes the revealed value straight to the
driver. The driver and its credential live and die inside that one invocation; nothing is pooled
across invocations. An operation already in flight keeps the credential it previously obtained.

Host output protection retains resolved credentials until the owning invocation's result conversion
and failure translation finish. Overlapping owners and callback readers share reference-counted
registrations; the last release forgets the plaintext and encoded value. The capacity limit applies
to distinct active credentials, not credential-rotation history. Retained extension loggers withhold
all free-form messages after the first credential retirement, including otherwise harmless messages,
so late logging cannot disclose forgotten credentials. This does not make in-process extensions a
security sandbox: extensions must not cache credentials for later results or admission callbacks.
Exact-value filtering of those structured boundaries covers active registrations, not an indefinite
history of credentials.

## 4. Database account

Use a dedicated least-privilege account. Never an administrator.

`readOnly` controls the driver's access mode and the tool's approval requirement. When `true`,
execution uses read mode, is classified as read-only and idempotent, and does not prompt for approval.
When `false`, execution uses write mode and follows normal approval for a non-idempotent external write.

**Read routing is not authorization.** Database account permissions are your responsibility. For
server-enforced read-only access, use an account that denies writes and restrict procedures and
features with side effects, including outbound network access. The extension does not verify account
permissions or ask you to confirm them through another setting.

Setting `readOnly` to `false` does not grant host approval or database privileges; those remain
separate controls.

## 5. Server requirements

Compact discovery uses `db.labels()`, `db.relationshipTypes()`, `COLLECT` subqueries, and `valueType()`. It takes one bounded node sample and one bounded directed relationship sample. It does not use dynamic token matches or search the graph for each catalog name.

The extension also uses Bolt, explicit transactions, and these built-in metadata sources:
`SHOW CONSTRAINTS`, `SHOW INDEXES`, `db.schema.nodeTypeProperties()`, and
`db.schema.relTypeProperties()`. No plugin such as APOC is required. If the server or the account
rejects one of them, that section is reported as `unsupported`, `permissionDenied`, or `unavailable`
rather than replaced by a graph scan. Validate your exact server version and edition against these
sources before you rely on the extension.

## 6. Using `neo4j_execute`

Input accepts only `cypher` (required, at most 4096 characters) and `parameters` (optional). One
statement per call. Prefer parameters over literals: the whole statement is shown to the operator
during approval, parameter values are not.

```json
{
  "cypher": "MATCH (p:Person {id: $id}) RETURN p.name AS name",
  "parameters": { "id": "person-123" }
}
```

With `readOnly` set to `true` (the default), calls use driver read mode without an approval prompt.
With `readOnly` set to `false`, every call is classified as a non-idempotent external write requiring
user approval, even if the statement only reads. Statement text does not determine the access mode.
The output `readOnly` field reports requested routing, not independently verified authorization.

### Output

```json
{
  "schemaVersion": 1,
  "outcome": "committed",
  "phase": "completed",
  "readOnly": true,
  "destination": "neo4j+s://graph.example.com:7687",
  "database": "neo4j",
  "columns": ["name"],
  "records": [{ "name": "Ada" }],
  "returnedRows": 1,
  "truncated": false,
  "limitsReached": [],
  "counters": { "containsUpdates": false },
  "statuses": [],
  "error": null
}
```

`outcome` is one of:

| Outcome | Meaning |
| --- | --- |
| `committed` | The server confirmed the commit. |
| `notStarted` | No database operation began. Argument rejection and credential denial land here. |
| `rolledBack` | The database confirmed the rollback. |
| `unknown` | Execution began and the final state cannot be proved. |

`phase` reports where the operation got to: `admission`, `connection`, `execution`, `consumption`,
`commit`, or `completed`.

**Truncation is not failure.** A bounded response still reports its real transaction outcome:

```json
{ "outcome": "rolledBack", "phase": "consumption", "truncated": true, "returnedRows": 500, "limitsReached": ["maximumRows"] }
```

When the row or retained-result-byte limit leaves unread records, the extension stops fetching and
requests rollback. The driver waits for outstanding results to be discarded before it sends rollback;
these output limits do not bound cleanup latency. This applies to reads and writes: a write returning
an oversized result is **not committed**. Retained records are a partial, rolled-back view, not proof
of persisted updates. If rollback cannot be confirmed, the result reports `unknown` with an error.
Individual-value truncation alone does not abandon an otherwise fully consumed result.

These limits bound retained output and explicit cursor fetching, not total server execution, the size
of an individual incoming record, or the driver's fetch buffer. The server query timeout still applies.

Canceling the request stops Threadsmith from waiting for the result. The driver does not expose
cancellation for an outstanding explicit transaction call. The invocation retains the credential,
session, and extension lease until that call and cleanup finish. After observing cancellation, it
starts no further query work and does not send a new commit. Cancellation does not undo a commit
already sent or prove that the server has stopped the query.

**An unknown commit is never replayed.** If the commit acknowledgement is lost, the result is:

```json
{ "outcome": "unknown", "phase": "commit", "error": { "kind": "transient" } }
```

The extension does not retry it, and neither should a caller without first checking the database.
Threadsmith source rollback cannot undo a database write; recovery uses database backups or
corrective queries.

### Value mapping

Values that plain JSON cannot represent exactly are tagged with `$type` and explicit components:
`node`, `relationship`, `path`, `date`, `localTime`, `time`, `localDateTime`, `dateTime`,
`duration`, `point`, `bytes`, and `text` or `float` for bounded or non-finite values. Driver
`ToString()` output is never the contract. Node and relationship `elementId` values are
database-scoped identifiers, not durable business keys. Nesting depth, collection entries, string
length, and individual value size are bounded; when any bound applies, `limitsReached` includes
`maximumValueSize`.

Points include `dimensions` (2 or 3); 2D points omit `z`, while 3D points retain it. Non-finite
coordinates use the same tagged `float` representation as ordinary non-finite numbers. Datetimes
include `zoneKind`: named zones retain `zoneId`, including unresolved identifiers, while offset-only
zones use `offsetSeconds` without `zoneId`. `zoneResolved: false` omits the unavailable offset but
preserves the UTC instant, nanoseconds, and original zone identifier.

## 7. Using `neo4j_schema`

```json
{ "sections": ["constraints", "indexes", "nodes", "relationships"], "sampleLimit": 500 }
```

For normal query writing, use `{}`. It returns three sections:

| Section | Entries |
| --- | --- |
| `nodeSamples` | Label, number sampled, and observed property names with their distinct Cypher types. |
| `relationshipEndpoints` | Relationship type, observed pairs of source and target label sets, and properties observed across the sampled relationships of that type. |
| `indexes` | Index name, type, entity type, labels or relationship types, ordered properties, and state from `SHOW INDEXES`. |

Each compact section takes the first `min(256, sampleLimit)` entities available, then uses up to eight from that sample for each catalog name. Rare labels and relationship types may have no observations even if they exist. It lists additional names within the row and byte limits with `inspected: false`; an empty property list for such a name means **not inspected**. Explicit `nodes` and `relationships` sections still run the more expensive property-discovery procedures. `constraints` is also available explicitly. All fields are optional; `sampleLimit` can only lower the configured bound.

The default timeout for the whole schema call is five minutes. `queryTimeoutSeconds` still bounds each database transaction separately (30 seconds by default). A [tool runtime override](../operations/resource-limits.md#tool-runtime-overrides) or mandatory host ceiling can shorten the whole call.

`forceRefresh` (boolean, default `false`): **by user request only**. For example, `{"forceRefresh":true}` refreshes the default sections. Each loaded extension instance keeps the last successful result per section in memory. A matching effective `sampleLimit` reuses it without querying the database, including when requesting just one cached section. A different limit fetches that section again. A failed refresh reports the failure and does not replace that section's last good result. Writes do not refresh the cache; reload or restart clears it.

Each section reports its own `kind` (`declared` or `observed`), `status`, `sourceMethod`, `limit`,
`returned`, `entries`, and `detail`:

```json
{
  "schemaVersion": 1,
  "sampleLimit": 500,
  "sections": {
    "constraints": { "kind": "declared", "status": "permissionDenied", "sourceMethod": "SHOW CONSTRAINTS" },
    "nodes": { "kind": "observed", "status": "complete", "sourceMethod": "db.schema.nodeTypeProperties", "returned": 12 },
    "relationshipEndpoints": { "kind": "observed", "status": "complete", "returned": 1, "entries": [{ "relationshipType": "WORKS_AT", "inspected": true, "sampled": 2, "endpoints": [{ "sourceLabels": ["Person"], "targetLabels": ["Company"] }], "properties": [{ "name": "since", "types": ["DATE NOT NULL"] }] }] }
  }
}
```

Declared sections state rules the database enforces. Observed sections describe sampled data. A
sampled absence is not proof that a label, property, or relationship cannot exist, and a missing
section is not an empty schema. An observed endpoint pair shows that the pattern existed at sampling time; it does not enumerate all valid connections. Only constraints establish mandatory properties.

## 8. Failure reporting

Failures are classified at the adapter boundary and never carry raw driver text, statement text, or
credential material: `authentication`, `authorization`, `unavailable`, `timeout`,
`invalidStatement`, `transient`, `database`, `invalidArguments`, and `credentialUnavailable`. Start,
approval, completion, failure, and cancellation all flow through ordinary host tool activity. Query
results, schema names, server notices, and error text are untrusted data, never host instructions.

## 9. Delivery decisions

- Driver: `Neo4j.Driver` 6.5.1, Apache-2.0, pinned centrally in `Directory.Packages.props` and used
  only by this extension. Its license and provenance are recorded in the release third-party
  notices and SBOM.
- Managed-transaction replay is disabled, so the driver never silently repeats a write.
- The statement review is bound to the generation's configured destination and database.
- Commit is reported as confirmed only when the server acknowledges it. A failure during commit is
  always `unknown`; a confirmed rollback is `rolledBack`; a failure before the transaction starts is
  `notStarted`.
- Driver lifetime is per invocation. A longer-lived pool would need an explicit credential lifetime,
  rotation, and disposal contract, so it is deliberately not used.

## 10. Known limitations

- No disposable-server integration suite ships with the repository, so the behaviour described here
  is covered by unit and contract tests against a fake session, not by automated server tests.
  Validate against your own disposable database before production use.
- Only one connection can be configured. Multiple named connections are out of scope.
