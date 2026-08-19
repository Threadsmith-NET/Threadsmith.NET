# MCP connections

Threadsmith imports MCP tools through the same host-owned policy and execution pipeline as built-in tools. Repository trust, per-tool approval, timeouts, output sanitization, and cancellation still apply; an MCP server is never an authority over host policy.

## Stdio profiles

A stdio profile launches a bare executable name resolved through the host environment. Path-qualified commands are rejected. Threadsmith forwards only a curated set of operating-system startup variables, explicitly configured environment values, and secrets named by `secretScope`; it does not inherit arbitrary parent-process variables.

```json
{
  "mcp": {
    "defaultDrainKillTimeoutSeconds": 10,
    "profiles": [
      {
        "id": "local-docs",
        "name": "Local documentation server",
        "transport": "stdio",
        "command": "dotnet",
        "arguments": [ "tools/docs-server.dll" ],
        "trust": "TrustedRead",
        "secretScope": [],
        "startupTimeoutSeconds": 30,
        "requestTimeoutSeconds": 60,
        "drainKillTimeoutSeconds": 10,
        "allowedCapabilities": [ "tools" ],
        "environment": {},
        "workingDirectory": ".",
        "autoConnect": true
      }
    ]
  }
}
```

Place auto-connect profiles in machine or user configuration (or trusted process environment configuration). Repository-owned `.threadsmith` configuration cannot authorize startup command execution: its profiles and self-assigned trust/`autoConnect` values are excluded from startup auto-connect. Auto-connect is best-effort, so a failed optional profile—including a profile-local startup timeoutâ€”is logged with sanitized diagnostics and does not prevent Threadsmith from starting. Connected tools are published to the shared host registry and removed when the profile disconnects. On shutdown, active requests drain within `drainKillTimeoutSeconds`; an unresponsive stdio process tree is then terminated.

## SSE and streamable HTTP

Use `transport: "sse"` for legacy HTTP-with-SSE and `transport: "http"` for streamable HTTP. The command is an absolute HTTP(S) endpoint. The standard network-host allowlist evaluates the endpoint host before an imported tool runs; production remote endpoints should use HTTPS.

```json
{
  "id": "remote-mcp",
  "name": "Remote MCP",
  "transport": "http",
  "command": "https://mcp.example.com/mcp",
  "trust": "TrustedExecution",
  "secretScope": [ "secrets:MCP_HTTP_TOKEN" ],
  "headers": {
    "Authorization": "secrets:MCP_HTTP_TOKEN",
    "X-Tenant-Id": "example"
  },
  "allowedCapabilities": [ "tools" ],
  "startupTimeoutSeconds": 30,
  "requestTimeoutSeconds": 60,
  "autoConnect": false
}
```

Header values may be ordinary static configuration. A value beginning with `secrets:` is resolved only when that exact logical reference appears in the profile's `secretScope`; missing or out-of-scope references fail before connection. Header values are never included in connection status or logs.

## Authentication

Static bearer tokens and API keys are supported through `headers` plus `secretScope`. Store the actual value under the matching nested path in `~/.threadsmith/secrets/config.json` or an exact environment variable; trusted MCP bootstrap requires a `UserOwned` source, so repository secret values are rejected. Never place a credential directly in repository configuration. Imported tools also declare the profile's complete `secretScope` to invocation policy, so every reference must appear in trusted `tools.allowedSecretReferences`. See [static secret discovery](secret-discovery.md).

### Interactive OAuth SSO

Milestone 10 supports authorization-code + PKCE for SSE and streamable-HTTP profiles. The official MCP SDK performs protected-resource and authorization-server discovery, state and RFC 9207 issuer validation, token exchange, bearer attachment, and transparent refresh. Threadsmith owns browser/callback UX and durable secret caching.

```json
{
  "id": "remote-sso",
  "name": "Remote MCP with SSO",
  "transport": "http",
  "command": "https://mcp.example.com/mcp",
  "trust": "TrustedRead",
  "secretScope": [ "secrets:MCP_OAUTH_CLIENT_SECRET" ],
  "oauth": {
    "enabled": true,
    "scopes": [ "tools.read", "offline_access" ],
    "clientId": "threadsmith",
    "clientSecret": "secrets:MCP_OAUTH_CLIENT_SECRET",
    "redirectPort": 8400
  },
  "allowedCapabilities": [ "tools" ],
  "autoConnect": true
}
```

`clientId` must be pre-registered; dynamic client registration is not enabled. `clientSecret` is optional for public PKCE clients, but when supplied it must be a logical `secrets:` reference present in `secretScope`. OAuth cannot be used by stdio profiles or combined with a configured `Authorization` header. `redirectPort: 0` selects an ephemeral loopback port; a fixed pre-registered redirect should use its configured port. Authorization-server metadata must be advertised by the MCP endpoint; `oauth.discoveryUrl` overrides are rejected because the pinned SDK does not expose an arbitrary discovery-document override.

Interactive mode opens the system browser and binds an HTTP callback listener to `localhost` only. Headless mode prints the authorization URL and accepts the complete pasted callback URL. Providers such as Microsoft Entra ID/Azure AD, Okta, and other standards-compliant OAuth authorization servers are supported when the MCP server advertises compatible metadata and the client registration permits the loopback redirect.

Tokens are stored outside the repository in the user-owned `~/.threadsmith/mcp-oauth-tokens.json` cache under `mcp:oauth:<profileId>:accessToken`, `refreshToken`, `expiresAt`, and related metadata. Cache files are created with owner-only permissions on Unix; malformed or unreadable optional cache content is ignored and replaced by a later successful token write. Never copy this file into a repository or diagnostic bundle. Access-token expiry triggers SDK-managed refresh; an unusable refresh token restarts interactive authorization. Tokens, secrets, authorization codes, and callback query values are not logged or projected.

MCP lifecycle management retains one identity per profile. Dynamic client registration and stdio OAuth are not implemented. Use `/mcp logout`, `/mcp revoke`, or `/mcp switch-account`; do not edit the live cache while Threadsmith is running.

## Lifecycle commands

`/mcp` and `/mcp list` show every effective trusted profile, including disconnected definitions. Output contains a sanitized executable basename or HTTP origin, coarse authentication state, connection generation, bounded capability counts, enabled-tool count, and sanitized last outcome. It never prints command arguments, environment, headers, endpoint path/query, token data, or account claims.

```text
/mcp inspect [profile]
/mcp connect [profile]
/mcp disconnect [profile]
/mcp reconnect [profile]
/mcp capabilities [profile] [tools|resources|resource-templates|prompts]
/mcp capability [profile] [capability]
/mcp enable|disable [profile] [tool]
/mcp resource read [profile] [resource-or-template] [key=value ...]
/mcp prompt get [profile] [prompt] [key=value ...]
/mcp auth|logout|revoke|switch-account|diagnose [profile]
```

Missing IDs use numbered selectors. Connect/disconnect/reconnect serialize per profile. Reconnect always drains the retiring generation and performs fresh profile, secret, transport, authentication, discovery, and registry evaluation. Disconnect atomically closes invocation admission before removing registry entries, waits for admitted requests, then retunes SDK stdio shutdown to the remaining drain/kill deadline; a required forced termination remains visible as `Killed` rather than being reported as a clean disconnect. Live connection handles are never restored by resume, clone, or restart.

Tools, resources, resource templates, and prompts are discovered only when both the server and profile allow them. Tool IDs stay profile-qualified; tool availability remains owned by Plan 27 but additionally requires an exact repository-bound, schema-digest-bound approval in the owner-protected user-owned `~/.threadsmith/mcp-tool-approvals.json` file. Repository `tools:enabled` or `tools:defaultEnabledOverrides` values can narrow availability but cannot grant this approval. Imported tools default disabled. Advertised list-change notifications debounce into one complete replacement within the 256-capability connection bound; tool publication changes atomically, schema changes advance the manager generation, and pre-resolved tools from a replaced generation are denied. Resources and prompts are not model tools: exact explicit operations return bounded, sanitized text marked `UNTRUSTED MCP`, including aggregate truncation disclosure when server items are omitted; binary content is withheld as safe metadata.

`/mcp diagnose` checks eligibility, endpoint/executable shape, secret-reference count, coarse OAuth state, capability translation, and—only while connected—a protocol ping. Servers whose negotiated protocol has no ping report that check honestly; Threadsmith never invokes an arbitrary tool as a health probe. Startup/discovery and recent explicit resource/prompt/ping measurements use monotonic labels and bounded aggregates.

### Identity effects

- `auth` explicitly connects and starts OAuth only for an OAuth-enabled HTTP/SSE profile. Automatic startup/repository-rebind attempts may reuse or refresh cached identity but suppress the authorization callback, so list, inspect, diagnose, and other implicit startup paths never launch a browser or wait for pasted input.
- `logout` drains/disconnects and atomically removes only `mcp:oauth:<profileId>:*`. It does not claim or attempt remote revocation.
- `revoke` drains/disconnects, retrieves request-timeout-bounded advertised authorization-server metadata without following redirects, and submits RFC 7009 revocation only to a same-origin HTTPS endpoint. Missing or cross-origin endpoints report unsupported. Network failures and timeouts are unconfirmed remote outcomes: they preserve local identity unless the user explicitly chooses local-only cleanup, in which case the exact profile cache is cleared.
- `switch-account` replaces the single identity. Interactive mode asks whether to log out locally or revoke first, then starts fresh authentication. It never stores two accounts.
- Static-token profiles reject OAuth identity actions and never delete or rotate the external secret.

Headless syntax is `threadsmith --mcp <action> [profile] [capability] [key=value ...]`. MCP management bypasses optional extension discovery/loading even if `--tui` is also supplied, so extension diagnostics cannot contaminate machine output. The interactive `resource read` and `prompt get` names become `resource-read` and `prompt-get`. Identity mutations require `--confirm`; switch-account can add `--revoke-current`, and an unconfirmed remote outcome can add `--allow-local-cleanup`. Standard output contains exactly one stable JSON result envelope. Explicit headless OAuth authorization instructions and the pasted-callback prompt use standard error, leaving stdout machine-parseable. Exit codes are `0` success, `2` not found, `3` policy/ineligible, `4` authentication/revocation action required, `124` timeout, `130` cancellation, and `1` other failure. Headless mode never opens selectors or confirmation prompts.

## Trust and capability policy

- `Untrusted` profiles cannot connect.
- `TrustedRead`, `TrustedExecution`, and `FullyTrusted` map into the existing repository trust and approval policy.
- `allowedCapabilities` accepts `tools`, `resources`, `resource-templates`, and `prompts`; imported tools still require user-owned repository/schema approval and undergo repository narrowing availability plus invocation policy.
- Server descriptions, schemas, stderr, and results are untrusted input. Threadsmith translates them into host-owned DTOs and sanitizes rendered/persisted output.
- For HTTP transports, add the endpoint host to the repository network allowlist before invocation.

## Timeouts and lifecycle

`startupTimeoutSeconds` is one end-to-end startup deadline covering scoped static-secret resolution, transport creation, connection, handshake, and initial allowed-capability discovery; multiple scoped secrets do not receive independent startup windows. `requestTimeoutSeconds` bounds imported tool calls plus explicit resource/prompt/ping requests, and `drainKillTimeoutSeconds` bounds shutdown. HTTP disconnect attempts capability-subscription, SDK client, and transport cleanup independently within the shared deadline, observes abandoned cleanup failures, and always releases the owned HTTP client. Stdio disconnect closes protocol streams and forcibly terminates an unresponsive process tree after the bound.

## Live HTTP verification

The Milestone 9 suite always runs the in-repo real stdio server. HTTP verification is opt-in because it requires a real endpoint:

```powershell
$env:THREADSMITH_MCP_HTTP_ENDPOINT = "https://mcp.example.com/mcp"
$env:THREADSMITH_MCP_HTTP_TOOL = "echo"
$env:THREADSMITH_MCP_HTTP_ARGUMENTS = '{"message":"hello"}'
$env:THREADSMITH_MCP_HTTP_MODE = "http" # or sse
tests\Threadsmith.Milestone9.Tests\bin\Debug\net10.0\Threadsmith.Milestone9.Tests.exe
```

Unset the variables after testing. Use only an endpoint you trust and are authorized to call.
