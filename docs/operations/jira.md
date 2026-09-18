# Jira issue reads

The optional `jira` tool reads one Jira Cloud issue's standard description, summary and identity through the governed tool pipeline. It does not search Jira, retrieve comments or attachments, follow description links, or modify tickets. Ticket content is untrusted evidence.

## Account configuration

Configure accounts only in trusted user or machine configuration, then restart Threadsmith. Store token values through the standard [Secrets boundary](secret-discovery.md); ordinary configuration contains only the logical reference.

Scoped API tokens use Atlassian's gateway and a trusted cloud ID:

```json
{
  "tools": {
    "jira": {
      "providers": {
        "work-jira": {
          "type": "jiraCloud",
          "enabled": true,
          "siteUrl": "https://example.atlassian.net",
          "endpointMode": "scopedGateway",
          "cloudId": "11111111-2222-4333-8444-555555555555",
          "browseHostAliases": ["issues.example.org"],
          "authentication": {
            "mode": "basic",
            "username": "developer@example.org",
            "secretReference": "secrets:jira:work-token"
          }
        }
      }
    }
  }
}
```

Create the scoped token with the classic `read:jira-work` scope. The account also needs Browse projects and any applicable issue-security permission. Threadsmith does not inspect token claims, and this release does not advertise an unverified granular-only scope recipe.

For an API token without scopes, set `endpointMode` to `site` and omit `cloudId`. Scoped mode sends requests only to `api.atlassian.com/ex/jira/{cloudId}`; unscoped mode sends them only to the configured `.atlassian.net` tenant. No endpoint fallback occurs. Allow the corresponding API host in normal network policy, enable **Jira** through `/tools`, and accept its repository-bound outbound consent.

`browseHostAliases` contains up to four exact custom-domain hostnames whose `/browse/{key}` links identify the configured account. An alias is parsed locally and never contacted. API requests and canonical result URLs still use `siteUrl`; wildcards, redirects, board/search links and arbitrary API URLs are unsupported.

## Reading issues

The model-facing operation is deliberately small:

```json
{"kind":"read","issue":"APP-123"}
```

```json
{"kind":"read","issue":"https://example.atlassian.net/browse/APP-123?source=mail#description"}
```

`kind` must be the string `read`. A key selects the only enabled account or uses the explicit optional `provider`. A browse URL selects the account by its trusted site or alias. Ambiguity returns a bounded account list and requires an explicit provider; Threadsmith never probes credentials to choose an account.

The output window shows compact operation metadata such as `read`, `APP-123`, the account ID and outcome. It does not print the summary or description. The readable plain-text description remains in the tool result, so a request such as "Show me the ticket description" can display it in the normal assistant response.

Jira descriptions use Atlassian Document Format. Threadsmith preserves ordinary text, headings, lists, quotes, code, tables, panels, labels and safe links. It does not fetch cards or media. `bodyState` distinguishes absent and present descriptions; `bodyComplete`, `limitations` and the standard truncation flag disclose unsupported rich content or bounded partial delivery. There is no continuation for an omitted suffix.

## Bounds and failures

| Key | Default | Meaning |
|---|---:|---|
| `maximumResponseBytes` | 2097152 | Maximum decompressed JSON response. Exceeding it fails because partial JSON is not trusted. |
| `maximumBodyBytes` | 65536 | Maximum UTF-8 plain-text description before result framing. A valid larger body returns a marked prefix. |
| `timeoutSeconds` | 30 | Jira invocation timeout. |

The standard runtime output cap defaults to 256 KiB and may further shorten a valid description while retaining identity and explicit limitations. Repository configuration can disable trusted accounts or lower limits, but cannot add, enable or rebind an account, alias, cloud ID, endpoint mode, email or secret reference.

Tokens resolve for every permitted request and are not cached in Jira headers. File-backed secret rotation can therefore apply on the next request according to the provider's normal refresh semantics; process-environment changes generally require restart. Authentication, authorization, not-found, throttling, malformed-response and limit failures are sanitized and never include response bodies or authorization values.

Official references: [Get issue](https://developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-issues/#api-rest-api-3-issue-issueidorkey-get), [Basic authentication](https://developer.atlassian.com/cloud/jira/platform/basic-auth-for-rest-apis/), [API tokens](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/), and [ADF structure](https://developer.atlassian.com/cloud/jira/platform/apis/document/structure/).
