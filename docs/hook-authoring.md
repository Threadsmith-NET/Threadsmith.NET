# Lifecycle hook authoring reference

A handler consumes one UTF-8 JSON `HookInvocationEnvelope` (schema 1) and returns one UTF-8 JSON `HookHandlerResult` (schema 1). HTTP uses a single POST. Executables read standard input and write only the response to standard output.

## Envelope

The envelope contains `schemaVersion`, `hookPoint`, `invocationId`, immutable `handlerIdentity` (`id`, `version`, `configurationDigest`), session/run/repository correlation, `operationId`, `generation`, `attempt`, timestamp, effective `dataScope`, bounded point-specific `payload`, permitted content-addressed artifact references, and recursion `callChain`. Fields not granted by policy are absent or empty.

Handlers must treat invocation/operation IDs as idempotency keys, accept cancellation/connection closure, avoid side effects unless independently authorized outside Threadsmith, and never assume that a denial is authoritative.

## Closed responses

```json
{ "$type": "acknowledge", "schemaVersion": 1 }
```

```json
{ "$type": "advice", "schemaVersion": 1, "findings": ["bounded finding"], "labels": ["security"] }
```

```json
{ "$type": "deny", "schemaVersion": 1, "code": "approved-code", "explanation": "bounded explanation" }
```

```json
{ "$type": "failure", "schemaVersion": 1, "code": "temporary-unavailable", "explanation": "bounded explanation", "transient": true }
```

Responses cannot contain commands, patches, approvals, credentials, capability registrations, workflow steps, or host-state replacements. Extra semantics in free text are ignored.

## Bounds and compatibility

Current compiled limits allow at most 64 handlers, 16 points and secret references per declaration, 100 ms–2 minute timeout, 1 KiB–1 MiB input/output, concurrency 1–8, and at most two transient retries. A declaration can narrow these bounds. Unknown envelope/result schema versions are inspectable but unavailable.

Use stable handler IDs and semantic versions. Any authority-relevant configuration change produces a different SHA-256 digest and invalidates repository approval or managed identity matching.
