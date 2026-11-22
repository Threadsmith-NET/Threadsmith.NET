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
| `neo4j_schema` | Returns declared constraints and indexes plus observed labels, relationship types, and property types, each with its source method, status, and limits. |

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
  "forceReadOnly": "true",
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
| `forceReadOnly` | no | `true` | `true` or `false`. Any other value is a configuration error. |
| `queryTimeoutSeconds` | no | `30` | 1–300. Applied as the server-side transaction timeout. |
| `maximumRows` | no | `500` | 1–10000 retained records. |
| `maximumResultBytes` | no | `65536` | 4096–262144 retained UTF-8 record bytes. |
| `schemaSampleLimit` | no | `1000` | 1–10000. A request can lower it but never raise it. |
| `readOnlyEnforcedByServerAccount` | no | `false` | Set `true` only after the account itself denies writes. Waives per-statement approval when `forceReadOnly` is `true`. |
| `allowInsecureLocalConnections` | no | `false` | Permits the direct `bolt` and `bolt+ssc` schemes **for a loopback host only**. Remote and routed addresses are rejected either way. |

The settings are validated when the extension activates. Activation opens no connection, resolves no
credential, and starts no background work. A configuration change takes effect through the normal
host load or replacement process.

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

## 4. Database account

Use a dedicated least-privilege account. Never an administrator.

`forceReadOnly` is a client-side safety setting: it makes the extension request the server's read
access mode and classifies the tool as read-only and idempotent. **Read routing is not
authorization.** For a genuinely read-only deployment the account itself must deny writes, and the
server must restrict procedures and features with side effects, including any that perform outbound
network access. Confirm your edition supports the role controls you need before you describe the
deployment as safe for arbitrary Cypher. If you cannot enforce that, narrow the queries you allow
instead of relying on the setting alone.

The extension cannot verify any of that from the client, so `forceReadOnly` alone does **not** waive
operator approval. A forced read-only deployment still requires approval for every statement until
you also set `readOnlyEnforcedByServerAccount` to `true`. That setting records your confirmation
that the account and procedure restrictions above are actually in place. It is an operator
assertion, not a check the extension can make, so set it only when it is true.

Setting `forceReadOnly` to `false` only permits the model to *request* a write. It does not grant
host approval and it does not grant database privileges; those remain separate controls.

## 5. Server requirements

The extension uses Bolt, explicit transactions, and these built-in metadata sources:
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

When `forceReadOnly` is `false`, every call is classified conservatively as a non-idempotent
external write that requires user approval, even if the statement only reads. Statement text cannot
prove that a query is read-only. A forced read-only deployment also requires approval unless
`readOnlyEnforcedByServerAccount` confirms that the server itself denies writes.

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
{ "outcome": "committed", "truncated": true, "returnedRows": 500, "limitsReached": ["maximumRows"] }
```

Remaining records are discarded without being retained, so bounding the output never abandons the
statement before its commit.

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

## 7. Using `neo4j_schema`

```json
{ "sections": ["constraints", "indexes", "nodes", "relationships"], "sampleLimit": 500 }
```

Both fields are optional. Omitting `sections` requests all of them. `sampleLimit` can only lower the
configured bound.

Each section reports its own `kind` (`declared` or `observed`), `status`, `sourceMethod`, `limit`,
`returned`, `entries`, and `detail`:

```json
{
  "schemaVersion": 1,
  "sampleLimit": 500,
  "sections": {
    "constraints": { "kind": "declared", "status": "permissionDenied", "sourceMethod": "SHOW CONSTRAINTS" },
    "nodes": { "kind": "observed", "status": "complete", "sourceMethod": "db.schema.nodeTypeProperties", "returned": 12 },
    "relationshipEndpoints": { "kind": "observed", "status": "unsupported", "sourceMethod": "none" }
  }
}
```

Declared sections state rules the database enforces. Observed sections describe sampled data. A
sampled absence is not proof that a label, property, or relationship cannot exist, and a missing
section is not an empty schema. Only constraints establish mandatory properties. Schema is not
cached between invocations, so it never goes stale after a write.

`relationshipEndpoints` is declared but not implemented in this version: observed
source-label/type/target-label combinations need a graph scan, so the section is reported as
`unsupported` instead of being guessed.

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
- `relationshipEndpoints` is not implemented, as described above.
